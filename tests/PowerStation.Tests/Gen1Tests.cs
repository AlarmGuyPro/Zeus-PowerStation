// SPDX-License-Identifier: GPL-2.0-or-later
using System.Net;
using System.Text.Json.Nodes;
using KQ4WLR.PowerStation.Services;

namespace PowerStation.Tests;

public static class Gen1Tests
{
    private static JsonNode Channel(JsonNode device, string key) =>
        device["status"]!["channels"]!.AsArray().First(c => c!["key"]!.GetValue<string>() == key)!;

    [Test]
    public static async Task AddsShellyEmWithRelayAndMeters()
    {
        await using var em = await FakeShellyGen1.StartAsync("SHEM", f =>
        {
            f.Relays.Add(new FakeShellyGen1.Relay { On = true, Name = "Contactor" });
            f.EMeters.Add(new FakeShellyGen1.EMeter { Name = "Leg A" });
            f.EMeters.Add(new FakeShellyGen1.EMeter { Power = 342.8, Reactive = 0, Voltage = 120.8, Name = "Leg B" });
        });
        await using var host = await PluginHost.StartAsync();

        var (probeStatus, probe) = await host.SendAsync(HttpMethod.Post, "devices/probe", new { host = em.Host });
        Assert.Equal(HttpStatusCode.OK, probeStatus, "probe");
        Assert.True(probe!["supported"]!.GetValue<bool>(), "EM supported");
        Assert.Equal("ShellyEM", probe["app"]!.GetValue<string>(), "app");

        var (status, device) = await host.SendAsync(HttpMethod.Post, "devices", new { host = em.Host });
        Assert.Equal(HttpStatusCode.OK, status, "add");
        Assert.Equal(1, device!["generation"]!.GetValue<int>(), "generation");
        Assert.Equal("Online", device["status"]!["health"]!.GetValue<string>(), "online");

        var relay = Channel(device, "switch:0");
        Assert.Equal("Switch", relay["kind"]!.GetValue<string>(), "relay kind");
        Assert.True(relay["on"]!.GetValue<bool>(), "relay on");
        Assert.Equal("Contactor", relay["name"]!.GetValue<string>(), "relay name from /settings");

        var legA = Channel(device, "emeter:0");
        Assert.Equal("Meter", legA["kind"]!.GetValue<string>(), "meter kind");
        Assert.Equal("Leg A", legA["name"]!.GetValue<string>(), "meter name");
        Assert.Equal(121.3, legA["voltageV"]!.GetValue<double>(), "voltage");
        // Current from apparent power: sqrt(1184.2² + 150²) / 121.3
        Assert.Equal(9.84, legA["currentA"]!.GetValue<double>(), "derived current");
        Assert.Equal(412876.0, legA["energyWh"]!.GetValue<double>(), "energy in Wh");
        Assert.Equal(50.0, legA["limits"]!["ratedA"]!.GetValue<double>(), "EM clamp rating");

        // Meters can't be switched.
        var (meterCmd, _) = await host.SendAsync(HttpMethod.Post, $"devices/{device["deviceId"]}/channels/meter/0", new { action = "on" });
        Assert.Equal(HttpStatusCode.BadRequest, meterCmd, "meter command");

        var (offStatus, off) = await host.SendAsync(HttpMethod.Post, $"devices/{device["deviceId"]}/channels/switch/0", new { action = "off" });
        Assert.Equal(HttpStatusCode.OK, offStatus, "relay off");
        Assert.False(Channel(off!, "switch:0")["on"]!.GetValue<bool>(), "relay now off");
        Assert.Contains("/relay/0?turn=off", string.Join(" ", em.Requests), "relay request");
    }

    [Test]
    public static async Task Gen1PasswordIsCheckedAndStoredWithItsUser()
    {
        await using var one = await FakeShellyGen1.StartAsync("SHSW-1", f =>
        {
            f.User = "shack";
            f.Password = "gen1-pw";
            f.Relays.Add(new FakeShellyGen1.Relay());
        });
        await using var host = await PluginHost.StartAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await host.SendAsync(HttpMethod.Post, "devices", new { host = one.Host })).Status, "no password");
        Assert.Equal(HttpStatusCode.Forbidden,
            (await host.SendAsync(HttpMethod.Post, "devices", new { host = one.Host, password = "gen1-pw" })).Status, "wrong user (admin)");
        Assert.Equal(HttpStatusCode.Forbidden,
            (await host.SendAsync(HttpMethod.Post, "devices", new { host = one.Host, username = "shack", password = "nope" })).Status, "wrong password");

        var (status, device) = await host.SendAsync(HttpMethod.Post, "devices", new { host = one.Host, username = "shack", password = "gen1-pw" });
        Assert.Equal(HttpStatusCode.OK, status, "add");
        Assert.True(device!["hasCredential"]!.GetValue<bool>(), "has credential");
        Assert.Equal("Online", device["status"]!["health"]!.GetValue<string>(), "online with Basic auth");
        Assert.Equal("Shelly1", device["app"]!.GetValue<string>(), "app");
        Assert.DoesNotContain("gen1-pw", device.ToJsonString(), "API never returns the password");

        var (_, on) = await host.SendAsync(HttpMethod.Post, $"devices/{device["deviceId"]}/channels/switch/0", new { action = "on" });
        Assert.True(Channel(on!, "switch:0")["on"]!.GetValue<bool>(), "switched with auth");
    }

    [Test]
    public static async Task Gen1DimmerLevelAndFade()
    {
        await using var dimmer = await FakeShellyGen1.StartAsync("SHDM-2", f =>
        {
            f.Lights.Add(new FakeShellyGen1.Light { On = false, Brightness = 30 });
            f.MeterPower.Add(12.5);
        });
        await using var host = await PluginHost.StartAsync();
        var (_, device) = await host.SendAsync(HttpMethod.Post, "devices", new { host = dimmer.Host });
        var id = device!["deviceId"]!.GetValue<string>();
        Assert.Equal("Dimmer2", device["app"]!.GetValue<string>(), "app");

        var (status, lit) = await host.SendAsync(HttpMethod.Post, $"devices/{id}/channels/light/0",
            new { action = "brightness", brightness = 40, transitionSeconds = 1.5 });
        Assert.Equal(HttpStatusCode.OK, status, "brightness");
        var light = Channel(lit!, "light:0");
        Assert.True(light["on"]!.GetValue<bool>(), "on");
        Assert.Equal(40.0, light["brightness"]!.GetValue<double>(), "level");
        Assert.Equal(1500, dimmer.Lights[0].LastTransitionMs, "fade in ms");
        Assert.Equal(12.5, light["powerW"]!.GetValue<double>(), "meter power");
    }

    [Test]
    public static async Task Gen1SafetyTimerUsesTheRelayTimer()
    {
        await using var one = await FakeShellyGen1.StartAsync("SHSW-1", f => f.Relays.Add(new FakeShellyGen1.Relay { On = true }));
        await using var host = await PluginHost.StartAsync();
        var (_, device) = await host.SendAsync(HttpMethod.Post, "devices", new { host = one.Host });
        var id = device!["deviceId"]!.GetValue<string>();

        var (status, updated) = await host.SendAsync(HttpMethod.Patch, $"devices/{id}", new { safetyMinutes = new Dictionary<string, int> { ["switch:0"] = 10 } });
        Assert.Equal(HttpStatusCode.OK, status, "save safety");
        Assert.Contains("/relay/0?turn=on&timer=600", string.Join(" ", one.Requests), "timer set on the device");
        Assert.Equal(600, one.Relays[0].TimerSeconds, "device timer");
        var (_, again) = await host.SendAsync(HttpMethod.Post, $"devices/{id}/refresh");
        var ch = Channel(again!, "switch:0");
        Assert.Equal(10, ch["safetyMinutes"]!.GetValue<int>(), "safety minutes shown");
        Assert.NotNull(ch["timerEndsAt"], "device timer shown");

        var (bad, _) = await host.SendAsync(HttpMethod.Patch, $"devices/{id}", new { safetyMinutes = new Dictionary<string, int> { ["switch:0"] = 5000 } });
        Assert.Equal(HttpStatusCode.BadRequest, bad, "too long");
    }

    [Test]
    public static void ParsesPlugSAndTwoPointFiveStatus()
    {
        var status = JsonNode.Parse("""
            {"relays":[{"ison":true,"overpower":false},{"ison":false,"overpower":true}],
             "meters":[{"power":230.5,"total":6000},{"power":0,"total":120}],
             "voltage":121.0,"temperature":45.1,"overtemperature":false}
            """)!.AsObject();
        var channels = KQ4WLR.PowerStation.Shelly.Gen1Client.ParseStatus(status, new Dictionary<string, string>(), DateTimeOffset.UnixEpoch);
        Assert.Equal(2, channels.Count, "two relays");
        Assert.Equal(230.5, channels[0].PowerW ?? 0, "power");
        Assert.Equal(1.9, channels[0].CurrentA ?? 0, "current from device voltage");
        Assert.Equal(100.0, channels[0].EnergyWh ?? 0, "watt-minutes to Wh");
        Assert.Equal("overpower", channels[1].Errors.Single(), "overpower");
        Assert.Equal(null, ReadingsService.RatedAmps("PlugS", KQ4WLR.PowerStation.Model.ChannelKind.Switch), "unknown rating left blank");
    }
}

public static class Gen1NamingTests
{
    [Test]
    public static async Task RenamesEmRelayAndMeters()
    {
        await using var em = await FakeShellyGen1.StartAsync("SHEM", f =>
        {
            f.Relays.Add(new FakeShellyGen1.Relay());
            f.EMeters.Add(new FakeShellyGen1.EMeter());
            f.EMeters.Add(new FakeShellyGen1.EMeter());
        });
        await using var host = await PluginHost.StartAsync();
        var (_, device) = await host.SendAsync(HttpMethod.Post, "devices", new { host = em.Host });
        var id = device!["deviceId"]!.GetValue<string>();
        var (status, body) = await host.SendAsync(HttpMethod.Patch, $"devices/{id}", new
        {
            channelNames = new Dictionary<string, string> { ["switch:0"] = "Emergency Generator Shutdown", ["emeter:0"] = "L1 Leg", ["emeter:1"] = "L2 Leg" },
        });
        Assert.Equal(System.Net.HttpStatusCode.OK, status, $"rename: {body}");
        var names = body!["status"]!["channels"]!.AsArray().Select(c => c!["name"]?.GetValue<string>()).ToArray();
        Assert.Equal("Emergency Generator Shutdown|L1 Leg|L2 Leg", string.Join("|", names), "names");
    }
}
