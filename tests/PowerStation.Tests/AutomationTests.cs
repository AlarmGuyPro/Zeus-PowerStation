// SPDX-License-Identifier: GPL-2.0-or-later
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using KQ4WLR.PowerStation.Model;
using KQ4WLR.PowerStation.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace PowerStation.Tests;

/// <summary>A device manager, a pretend radio and a hand-moved clock around one simulated Pro 4PM + dimmer.</summary>
internal sealed class Rig : IAsyncDisposable
{
    public required FakeShellyGen2 Fake { get; init; }
    public required ManualTime Time { get; init; }
    public required FakeRadio Radio { get; init; }
    public required DeviceManager Devices { get; init; }
    public required AutomationService Auto { get; init; }
    public required HttpClient Http { get; init; }
    public string Id => Fake.DeviceId;

    public const int Amp = 0, Preamp = 1;

    public static async Task<Rig> StartAsync(DateTimeOffset? start = null)
    {
        var fake = await FakeShellyGen2.StartAsync("shellypro4pm-a0703a710000", f =>
        {
            f.Switches.Add(new FakeShellyGen2.FakeSwitch { On = true, Name = "Amplifier" });
            f.Switches.Add(new FakeShellyGen2.FakeSwitch { On = false, Name = "Preamp" });
            f.Lights.Add(new FakeShellyGen2.FakeLight { On = false, Brightness = 100, Name = "On Air" });
        });
        var time = new ManualTime(start ?? new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero));
        var settings = new MemorySettings();
        var http = KQ4WLR.PowerStation.PowerStationPlugin.CreateLanHttpClient();
        var devices = new DeviceManager(new SettingsDeviceStore(settings), http, NullLogger.Instance, time);
        await devices.LoadAsync(CancellationToken.None);
        await devices.AddAsync(new AddDeviceRequest(fake.Host, "Rack", null), CancellationToken.None);
        var radio = new FakeRadio();
        var auto = new AutomationService(settings, devices, radio, NullLogger.Instance, time);
        await auto.LoadAsync(CancellationToken.None);
        return new Rig { Fake = fake, Time = time, Radio = radio, Devices = devices, Auto = auto, Http = http };
    }

    public static RuleAction Output(string deviceId, int index, bool on, ChannelKind kind = ChannelKind.Switch, double? level = null) =>
        new() { Type = "output", DeviceId = deviceId, Kind = kind, Index = index, On = on, Brightness = level };

    public Task<Rule> AddRule(string name, RuleTrigger trigger, RuleAction action, RuleAction? end = null,
        double? debounce = null, double? delay = null, double? endDelay = null) =>
        Auto.CreateAsync(new RuleRequest(name, true, trigger, action, end, debounce, delay, endDelay), CancellationToken.None);

    /// <summary>Moves the clock, lets rules act, and refreshes the device.</summary>
    public async Task Step(double seconds = 0)
    {
        Time.Advance(TimeSpan.FromSeconds(seconds));
        Auto.Tick();
        await Auto.DrainAsync();
        await Devices.RefreshAsync(Id, CancellationToken.None);
    }

    public bool On(int index) => Fake.Switches[index].On;
    public bool LightOn => Fake.Lights[0].On;

    public JsonNode View() => JsonNode.Parse(JsonSerializer.Serialize(Auto.View(), new JsonSerializerOptions(JsonSerializerDefaults.Web)))!;

    public async ValueTask DisposeAsync()
    {
        await Auto.DisposeAsync();
        await Devices.DisposeAsync();
        Http.Dispose();
        await Fake.DisposeAsync();
    }
}

public static class AutomationTests
{
    [Test]
    public static async Task BandRuleWaitsForTheDebounceAndPutsThingsBack()
    {
        await using var rig = await Rig.StartAsync();
        await rig.AddRule("6 m preamp", new RuleTrigger { Type = "band", Bands = ["6m"] },
            Rig.Output(rig.Id, Rig.Preamp, true), new RuleAction { Type = "restore" }, debounce: 2);
        rig.Auto.Start(runLoop: false);

        // Passing through 6 m while tuning doesn't count.
        rig.Radio.Tune(50.1);
        await rig.Step(1);
        rig.Radio.Tune(14.074);
        await rig.Step(3);
        Assert.False(rig.On(Rig.Preamp), "passing through");

        rig.Radio.Tune(50.313);
        await rig.Step(1);
        Assert.False(rig.On(Rig.Preamp), "within debounce");
        await rig.Step(1.5);
        Assert.True(rig.On(Rig.Preamp), "on after debounce");

        rig.Radio.Tune(14.074);
        await rig.Step();
        Assert.False(rig.On(Rig.Preamp), "put back off");
    }

    [Test]
    public static async Task OnlyTheOnAirLightActsDuringTx()
    {
        await using var rig = await Rig.StartAsync();
        await rig.AddRule("On air", new RuleTrigger { Type = "tx" },
            Rig.Output(rig.Id, 0, true, ChannelKind.Light, 100), new RuleAction { Type = "off" }, endDelay: 3);
        await rig.AddRule("6 m preamp", new RuleTrigger { Type = "band", Bands = ["6m"] },
            Rig.Output(rig.Id, Rig.Preamp, true), new RuleAction { Type = "off" });
        rig.Auto.Start(runLoop: false);

        rig.Radio.Key(true);
        await rig.Step();
        Assert.True(rig.LightOn, "on-air light follows TX");

        rig.Radio.Tune(50.313);
        await rig.Step();
        Assert.False(rig.On(Rig.Preamp), "preamp waits while transmitting");
        var pending = rig.View()["pending"]!.AsArray();
        Assert.True(pending.Any(p => p!["waitingForTx"]!.GetValue<bool>()), "shown as waiting for TX");

        rig.Radio.Key(false);
        await rig.Step();
        Assert.True(rig.On(Rig.Preamp), "runs when TX ends");
        Assert.True(rig.LightOn, "light held between overs");
        await rig.Step(3);
        Assert.False(rig.LightOn, "light off after the hold");
    }

    [Test]
    public static async Task IdleRuleWarnsRunsAndPutsBackOnlyUntouchedOutputs()
    {
        await using var rig = await Rig.StartAsync();
        rig.Fake.Switches[Rig.Preamp].On = true;
        await rig.Devices.RefreshAsync(rig.Id, CancellationToken.None);
        var standby = await rig.Devices.Scenes.CreateAsync(new SceneRequest("Standby", null,
        [
            new SceneTarget { DeviceId = rig.Id, Kind = ChannelKind.Switch, Index = Rig.Amp, On = false },
            new SceneTarget { DeviceId = rig.Id, Kind = ChannelKind.Switch, Index = Rig.Preamp, On = false },
        ]), CancellationToken.None);
        await rig.AddRule("Standby after an hour", new RuleTrigger { Type = "idle", Minutes = 60, WarnMinutes = 5, ExtendMinutes = 30 },
            new RuleAction { Type = "scene", SceneId = standby.Id, Mode = "apply" }, new RuleAction { Type = "restore" });
        rig.Auto.Start(runLoop: false);

        await rig.Step(54 * 60);
        Assert.Equal("active", rig.View()["idle"]!["state"]!.GetValue<string>(), "active");
        await rig.Step(2 * 60);
        Assert.Equal("warning", rig.View()["idle"]!["state"]!.GetValue<string>(), "countdown");

        rig.Auto.ExtendIdle();
        await rig.Step(10 * 60);
        Assert.True(rig.On(Rig.Amp), "extended by 30 min");

        await rig.Step(30 * 60);
        Assert.False(rig.On(Rig.Amp), "standby applied");
        Assert.False(rig.On(Rig.Preamp), "standby applied");
        Assert.Equal("idle", rig.View()["idle"]!["state"]!.GetValue<string>(), "idle");

        // Someone switches the amp back on at the device while the station is idle.
        rig.Fake.Switches[Rig.Amp].On = true;
        await rig.Devices.RefreshAsync(rig.Id, CancellationToken.None);


        rig.Radio.Tune(7.074); // back at the radio
        await rig.Step();
        Assert.True(rig.On(Rig.Preamp), "untouched output put back");
        Assert.True(rig.On(Rig.Amp), "the amp stays as the person left it");
        Assert.Contains("left 1 someone changed", rig.Auto.RulesView() is var v ? JsonSerializer.Serialize(v) : "", "reported");
    }

    [Test]
    public static async Task TimeOfDayRuleChecksAgainWhileTheStationIsBusy()
    {
        await using var rig = await Rig.StartAsync(new DateTimeOffset(2026, 9, 28, 22, 50, 0, TimeSpan.Zero));
        await rig.AddRule("Lights out", new RuleTrigger { Type = "time", At = "23:00", IdleMinutes = 15, ExtendMinutes = 30 },
            Rig.Output(rig.Id, Rig.Amp, false));
        rig.Auto.Start(runLoop: false);

        await rig.Step(9 * 60);
        rig.Radio.Tune(7.1); // 22:59, someone is operating
        await rig.Step(60);
        Assert.True(rig.On(Rig.Amp), "busy at 23:00");
        Assert.Contains("checking again at 23:30", JsonSerializer.Serialize(rig.Auto.RulesView()), "says when");

        await rig.Step(30 * 60);
        Assert.False(rig.On(Rig.Amp), "quiet at 23:30, so it runs");
    }

    [Test]
    public static async Task ZeusStartWaitsItsDelayAndCloseRunsImmediately()
    {
        await using var rig = await Rig.StartAsync();
        rig.Fake.Switches[Rig.Amp].On = false;
        await rig.AddRule("Amp on at start", new RuleTrigger { Type = "zeusStart" }, Rig.Output(rig.Id, Rig.Amp, true), delay: 5);
        await rig.AddRule("Amp off at close", new RuleTrigger { Type = "zeusStop" }, Rig.Output(rig.Id, Rig.Amp, false));
        rig.Auto.Start(runLoop: false);
        await rig.Step(2);
        Assert.False(rig.On(Rig.Amp), "waiting");
        await rig.Step(3);
        Assert.True(rig.On(Rig.Amp), "on after 5 s");

        await rig.Auto.StopAsync(TimeSpan.FromSeconds(3));
        Assert.False(rig.On(Rig.Amp), "off as Zeus closes");
    }

    [Test]
    public static async Task PausedRulesDoNothing()
    {
        await using var rig = await Rig.StartAsync();
        await rig.AddRule("6 m preamp", new RuleTrigger { Type = "band", Bands = ["6m"] }, Rig.Output(rig.Id, Rig.Preamp, true));
        rig.Auto.Start(runLoop: false);
        await rig.Auto.SetPausedAsync(true, CancellationToken.None);
        rig.Radio.Tune(50.313);
        await rig.Step(5);
        Assert.False(rig.On(Rig.Preamp), "paused");
        await rig.Auto.SetPausedAsync(false, CancellationToken.None);
        await rig.Step();
        Assert.True(rig.On(Rig.Preamp), "running again");
    }

    [Test]
    public static async Task RulesAreValidated()
    {
        await using var rig = await Rig.StartAsync();
        var scene = await rig.Devices.Scenes.CreateAsync(new SceneRequest("S", null,
            [new SceneTarget { DeviceId = rig.Id, Kind = ChannelKind.Switch, Index = 0, On = true }]), CancellationToken.None);
        async Task Bad(string what, RuleTrigger t, RuleAction a, RuleAction? end = null) =>
            await Assert.ThrowsAsync<PowerStationRequestException>(() => rig.AddRule(what, t, a, end));

        await Bad("TX with a scene", new RuleTrigger { Type = "tx" }, new RuleAction { Type = "scene", SceneId = scene.Id });
        await Bad("TX restoring", new RuleTrigger { Type = "tx" }, Rig.Output(rig.Id, 0, true, ChannelKind.Light), new RuleAction { Type = "restore" });
        await Bad("no bands", new RuleTrigger { Type = "band", Bands = [] }, Rig.Output(rig.Id, 1, true));
        await Bad("unknown band", new RuleTrigger { Type = "band", Bands = ["11m"] }, Rig.Output(rig.Id, 1, true));
        await Bad("inverted range", new RuleTrigger { Type = "frequency", FromMHz = 54, ToMHz = 50 }, Rig.Output(rig.Id, 1, true));
        await Bad("idle too short", new RuleTrigger { Type = "idle", Minutes = 1 }, Rig.Output(rig.Id, 1, true));
        await Bad("bad time", new RuleTrigger { Type = "time", At = "25:00" }, Rig.Output(rig.Id, 1, true));
        await Bad("missing output", new RuleTrigger { Type = "zeusStart" }, Rig.Output(rig.Id, 9, true));
        await Bad("meter", new RuleTrigger { Type = "zeusStart" }, Rig.Output(rig.Id, 0, true, ChannelKind.Meter));

        var ok = await rig.AddRule("Range", new RuleTrigger { Type = "frequency", FromMHz = 1.8, ToMHz = 2.0 },
            Rig.Output(rig.Id, 1, true), new RuleAction { Type = "off" }, debounce: 2, delay: 0, endDelay: 5);
        Assert.Equal(5.0, ok.EndDelaySeconds ?? 0, "kept end delay");
        var start = await rig.AddRule("Start", new RuleTrigger { Type = "zeusStart" }, Rig.Output(rig.Id, 1, true), debounce: 9, delay: 4);
        Assert.Equal(null, start.DebounceSeconds, "debounce means nothing for Zeus start");
        Assert.Equal(4.0, start.DelaySeconds ?? 0, "delay kept");
    }

    [Test]
    public static async Task RulesApiRoundTripsThroughStatus()
    {
        await using var fake = await FakeShellyGen2.StartAsync("shellyplus1pm-aa0000000001", f => f.Switches.Add(new FakeShellyGen2.FakeSwitch()));
        var radio = new FakeRadio();
        await using var host = await PluginHost.StartAsync(new FakePluginContext { Radio = radio });
        await host.SendAsync(HttpMethod.Post, "devices", new { host = fake.Host });

        var (created, rule) = await host.SendAsync(HttpMethod.Post, "rules", new
        {
            name = "Beverage on 160 m", enabled = true,
            trigger = new { type = "frequency", fromMHz = 1.8, toMHz = 2.0 },
            action = new { type = "output", deviceId = fake.DeviceId, kind = "Switch", index = 0, on = true },
            endAction = new { type = "off" }, debounceSeconds = 0,
        });
        Assert.Equal(HttpStatusCode.OK, created, "create");
        var (bad, err) = await host.SendAsync(HttpMethod.Post, "rules", new { name = "x", trigger = new { type = "tx" }, action = new { type = "scene", sceneId = "nope" } });
        Assert.Equal(HttpStatusCode.BadRequest, bad, "invalid");
        Assert.NotNull(err!["error"], "message");

        radio.Tune(1.84);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!fake.Switches[0].On && DateTime.UtcNow < deadline) await Task.Delay(100);
        Assert.True(fake.Switches[0].On, "the running clock acted on the tune");

        var (_, status) = await host.SendAsync(HttpMethod.Get, "status");
        Assert.Equal("Beverage on 160 m", status!["rules"]![0]!["name"]!.GetValue<string>(), "rules in status");
        Assert.Equal("160m", status["automation"]!["radio"]!["band"]!.GetValue<string>(), "band from frequency");
        Assert.True(status["automation"]!["radio"]!["connected"]!.GetValue<bool>(), "radio connected");

        var (paused, view) = await host.SendAsync(HttpMethod.Put, "automation", new { paused = true });
        Assert.Equal(HttpStatusCode.OK, paused, "pause");
        Assert.True(view!["paused"]!.GetValue<bool>(), "paused");
        Assert.Equal(HttpStatusCode.OK, (await host.SendAsync(HttpMethod.Delete, $"rules/{rule!["id"]}")).Status, "delete");
    }
}
