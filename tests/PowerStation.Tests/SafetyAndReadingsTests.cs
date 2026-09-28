// SPDX-License-Identifier: GPL-2.0-or-later
using System.Net;
using System.Text.Json.Nodes;
using KQ4WLR.PowerStation.Model;
using KQ4WLR.PowerStation.Services;

namespace PowerStation.Tests;

public static class SafetyTests
{
    private static int ToggleAfterCalls(FakeShellyGen2 fake, string method) =>
        fake.Calls.Count(c => c["method"]!.GetValue<string>() == method && c["params"]?["toggle_after"] is not null);

    [Test]
    public static async Task SafetyTimerIsSetOnlyWhileOnAndRenewedSparingly()
    {
        await using var fake = await FakeShellyGen2.StartAsync("shellypro4pm-5afe7i3e0000", f =>
        {
            f.Switches.Add(new FakeShellyGen2.FakeSwitch { On = true, Name = "Amplifier" });
            f.Switches.Add(new FakeShellyGen2.FakeSwitch { On = false, Name = "Monitors" });
        });
        await using var host = await PluginHost.StartAsync();
        await host.SendAsync(HttpMethod.Post, "devices", new { host = fake.Host });

        var (status, _) = await host.SendAsync(HttpMethod.Patch, $"devices/{fake.DeviceId}",
            new { safetyMinutes = new Dictionary<string, int> { ["switch:0"] = 10, ["switch:1"] = 10 } });
        Assert.Equal(HttpStatusCode.OK, status, "save");
        var set = fake.Calls.Where(c => c["params"]?["toggle_after"] is not null).ToArray();
        Assert.Equal(1, set.Length, "only the output that's on gets a timer");
        Assert.Equal(0, set[0]["params"]!["id"]!.GetValue<int>(), "amplifier");
        Assert.Equal(600, set[0]["params"]!["toggle_after"]!.GetValue<int>(), "10 minutes");
        Assert.True(set[0]["params"]!["on"]!.GetValue<bool>(), "keeps it on");

        // Polls well inside a third of the period don't renew again.
        await host.SendAsync(HttpMethod.Post, $"devices/{fake.DeviceId}/refresh");
        await host.SendAsync(HttpMethod.Post, $"devices/{fake.DeviceId}/refresh");
        Assert.Equal(1, ToggleAfterCalls(fake, "Switch.Set"), "no renewal yet");

        // Turning the other output on starts its timer at once.
        await host.SendAsync(HttpMethod.Post, $"devices/{fake.DeviceId}/channels/switch/1", new { action = "on" });
        Assert.Equal(2, ToggleAfterCalls(fake, "Switch.Set"), "second output protected");

        // Clearing it stops renewals.
        await host.SendAsync(HttpMethod.Patch, $"devices/{fake.DeviceId}", new { safetyMinutes = new Dictionary<string, int?> { ["switch:1"] = null } });
        var (_, view) = await host.SendAsync(HttpMethod.Post, $"devices/{fake.DeviceId}/refresh");
        var monitors = view!["status"]!["channels"]!.AsArray().First(c => c!["key"]!.GetValue<string>() == "switch:1")!;
        Assert.True(monitors["safetyMinutes"] is null, "cleared");
    }

    [Test]
    public static async Task SceneSafetyTimerCoversOutputsItTurnsOn()
    {
        await using var fake = await FakeShellyGen2.StartAsync("shellydimmerg3-5afe00000001", f => f.Lights.Add(new FakeShellyGen2.FakeLight()));
        await using var host = await PluginHost.StartAsync();
        await host.SendAsync(HttpMethod.Post, "devices", new { host = fake.Host });
        var (_, scene) = await host.SendAsync(HttpMethod.Post, "scenes", new
        {
            name = "Bench", safetyMinutes = 30,
            targets = new[] { new { deviceId = fake.DeviceId, kind = "Light", index = 0, on = true, brightness = 60 } },
        });
        Assert.Equal(30, scene!["safetyMinutes"]!.GetValue<int>(), "saved");
        await host.SendAsync(HttpMethod.Post, $"scenes/{scene["id"]}/run", new { mode = "apply" });
        var timer = fake.Calls.LastOrDefault(c => c["method"]!.GetValue<string>() == "Light.Set" && c["params"]?["toggle_after"] is not null);
        Assert.NotNull(timer, "light got a timer");
        Assert.Equal(1800, timer!["params"]!["toggle_after"]!.GetValue<int>(), "30 minutes");
    }
}

public static class ReadingsTests
{
    private static DeviceRecord Record(string id, string app = "Pro4PM") => new() { DeviceId = id, Host = "10.0.0.2", Generation = 2, App = app };

    private static ChannelState Ch(string key, double volts, double amps, bool on = true, string[]? errors = null) => new()
    {
        Key = key, Kind = ChannelKind.Switch, Index = int.Parse(key.Split(':')[1]), On = on, VoltageV = volts, CurrentA = amps,
        PowerW = volts * amps, Errors = errors ?? [],
    };

    private static JsonNode View(ReadingsService r) => JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(r.View(),
        new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)))!;

    [Test]
    public static void MainsSagIsOneEventAfterTheHoldTime()
    {
        var time = new ManualTime(new DateTimeOffset(2026, 9, 28, 18, 0, 0, TimeSpan.Zero));
        var r = new ReadingsService(new MemorySettings(), time);
        var a = Record("dev-a");
        var b = Record("dev-b");

        r.Observe(a, [Ch("switch:0", 108.2, 1)]);
        r.Observe(b, [Ch("switch:0", 108.6, 1)]);
        Assert.Equal(0, r.Decorate(a, [Ch("switch:0", 108.2, 1)])[0].Alerts.Count, "not before the hold time");
        Assert.True(View(r)["mainsNow"] is null, "no banner yet");

        time.Advance(TimeSpan.FromSeconds(6));
        r.Observe(a, [Ch("switch:0", 108.0, 1)]);
        r.Observe(b, [Ch("switch:0", 108.5, 1)]);
        var alert = r.Decorate(a, [Ch("switch:0", 108.0, 1)])[0].Alerts.Single();
        Assert.Equal("voltageLow", alert.Kind, "kind");
        Assert.Equal("limit", alert.Level, "below 110 V is past the limit");
        var view = View(r);
        Assert.Equal(2, view["mainsNow"]!["outputs"]!.GetValue<int>(), "seen on both outputs");
        var events = view["events"]!.AsArray();
        Assert.Equal(1, events.Count, "one mains event, not one per output");
        Assert.Equal("Mains", events[0]!["label"]!.GetValue<string>(), "label");
        Assert.Contains("lowest 108.0 V", events[0]!["text"]!.GetValue<string>(), "worst value");

        time.Advance(TimeSpan.FromSeconds(5));
        r.Observe(a, [Ch("switch:0", 121.0, 1)]);
        Assert.True(View(r)["events"]![0]!["end"] is null, "still open while the other output sees it");
        r.Observe(b, [Ch("switch:0", 121.2, 1)]);
        Assert.NotNull(View(r)["events"]![0]!["end"], "closed when all are back in range");
        Assert.True(View(r)["mainsNow"] is null, "banner gone");
    }

    [Test]
    public static void CurrentLimitsComeFromTheRatingOrTheOperator()
    {
        var time = new ManualTime(DateTimeOffset.UnixEpoch);
        var r = new ReadingsService(new MemorySettings(), time);
        var pro = Record("pro");
        r.Observe(pro, [Ch("switch:0", 121, 13.5), Ch("switch:1", 121, 17)]);
        time.Advance(TimeSpan.FromSeconds(6));
        r.Observe(pro, [Ch("switch:0", 121, 13.5), Ch("switch:1", 121, 17)]);
        var chans = r.Decorate(pro, [Ch("switch:0", 121, 13.5), Ch("switch:1", 121, 17)]);
        Assert.Equal(16.0, chans[0].Limits!.RatedA ?? 0, "Pro 4PM rating");
        Assert.Equal(12.8, chans[0].Limits!.WarnA ?? 0, "80% warning");
        Assert.Equal("warn", chans[0].Alerts.Single().Level, "13.5 A warns");
        Assert.Equal("limit", chans[1].Alerts.Single().Level, "17 A is past the limit");

        var custom = pro with { Limits = new() { ["switch:0"] = new LimitOverride { MinOnA = 0.2 } } };
        r.Observe(custom, [Ch("switch:0", 121, 0.01)]);
        time.Advance(TimeSpan.FromSeconds(6));
        r.Observe(custom, [Ch("switch:0", 121, 0.01)]);
        var low = r.Decorate(custom, [Ch("switch:0", 121, 0.01)])[0];
        Assert.Equal("currentLow", low.Alerts.Single().Kind, "low current while on");
        Assert.True(low.Limits!.Custom, "custom flag");
        r.Observe(custom, [Ch("switch:0", 121, 0.01, on: false)]);
        Assert.Equal(0, r.Decorate(custom, [Ch("switch:0", 121, 0.01, on: false)])[0].Alerts.Count, "no low-current alert when off");
    }

    [Test]
    public static void DeviceErrorsAreLoggedOnce()
    {
        var r = new ReadingsService(new MemorySettings(), new ManualTime(DateTimeOffset.UnixEpoch));
        var pro = Record("pro");
        r.Observe(pro, [Ch("switch:3", 121, 0, errors: ["overpower"])]);
        r.Observe(pro, [Ch("switch:3", 121, 0, errors: ["overpower"])]);
        var events = View(r)["events"]!.AsArray();
        Assert.Equal(1, events.Count, "once");
        Assert.Contains("overpower", events[0]!["text"]!.GetValue<string>());
    }

    [Test]
    public static async Task ReadingsSettingsAreValidated()
    {
        await using var host = await PluginHost.StartAsync();
        var (bad, _) = await host.SendAsync(HttpMethod.Put, "readings", new { mains = new { preset = "custom", normalLowV = 126, normalHighV = 114, limitLowV = 110, limitHighV = 127 } });
        Assert.Equal(HttpStatusCode.BadRequest, bad, "inverted range");
        var (ok, view) = await host.SendAsync(HttpMethod.Put, "readings", new { mains = new { preset = "230", normalLowV = 216, normalHighV = 244, limitLowV = 207, limitHighV = 253 }, holdSeconds = 10 });
        Assert.Equal(HttpStatusCode.OK, ok, "230 V");
        Assert.Equal(10, view!["holdSeconds"]!.GetValue<int>(), "hold");
        var (_, status) = await host.SendAsync(HttpMethod.Get, "status");
        Assert.Equal("230", status!["readings"]!["mains"]!["preset"]!.GetValue<string>(), "in status");
    }
}
