// SPDX-License-Identifier: GPL-2.0-or-later
using System.Net;
using System.Text.Json.Nodes;

namespace PowerStation.Tests;

public static class DebugLogTests
{
    private static async Task<JsonArray> Log(PluginHost host, string query = "")
    {
        var (status, body) = await host.SendAsync(HttpMethod.Get, $"debug/log?max=1000{query}");
        Assert.Equal(HttpStatusCode.OK, status, "log");
        return body!["entries"]!.AsArray();
    }

    [Test]
    public static async Task CommandsAndErrorsAreRecordedButRoutinePollsOnlyOnRequest()
    {
        await using var fake = await FakeShellyGen2.StartAsync("shellyplus1-deb000000001", f =>
        {
            f.Password = "secret-pw";
            f.Switches.Add(new FakeShellyGen2.FakeSwitch());
        });
        await using var host = await PluginHost.StartAsync();
        await host.SendAsync(HttpMethod.Post, "devices", new { host = fake.Host, password = "wrong" });
        await host.SendAsync(HttpMethod.Post, "devices", new { host = fake.Host, password = "secret-pw" });
        await host.SendAsync(HttpMethod.Post, $"devices/{fake.DeviceId}/channels/switch/0", new { action = "on" });

        var entries = await Log(host);
        var text = entries.ToJsonString();
        Assert.True(entries.Any(e => e!["method"]!.GetValue<string>() == "/shelly"), "identify recorded");
        Assert.True(entries.Any(e => !e!["ok"]!.GetValue<bool>() && e["error"]!.GetValue<string>().Contains("Unauthorized")), "wrong password recorded as an error");
        var set = entries.FirstOrDefault(e => e!["method"]!.GetValue<string>() == "Switch.Set");
        Assert.NotNull(set, "command recorded");
        Assert.Contains("\"on\":true", set!["request"]!.GetValue<string>(), "request params");
        Assert.Equal(fake.DeviceId, set["deviceId"]!.GetValue<string>(), "device id");
        Assert.DoesNotContain("secret-pw", text, "password never logged");
        Assert.DoesNotContain("Digest ", text, "auth header never logged");
        Assert.False(entries.Any(e => e!["method"]!.GetValue<string>() == "Shelly.GetStatus" && e["ok"]!.GetValue<bool>()),
            "routine polls not kept by default");

        var (_, on) = await host.SendAsync(HttpMethod.Put, "debug", new { recordAll = true });
        Assert.True(on!["recordAll"]!.GetValue<bool>(), "record all");
        await host.SendAsync(HttpMethod.Post, $"devices/{fake.DeviceId}/refresh");
        entries = await Log(host);
        var poll = entries.Last(e => e!["method"]!.GetValue<string>() == "Shelly.GetStatus");
        Assert.True(poll!["routine"]!.GetValue<bool>(), "marked routine");
        Assert.Contains("switch:0", poll["response"]!.GetValue<string>(), "reply kept");

        var since = entries.Last()!["seq"]!.GetValue<long>();
        Assert.Equal(0, (await Log(host, $"&since={since}")).Count(e => e!["seq"]!.GetValue<long>() <= since), "since cursor");
        Assert.Equal(HttpStatusCode.OK, (await host.SendAsync(HttpMethod.Delete, "debug/log")).Status, "clear");
        Assert.Equal(0, (await Log(host)).Count, "cleared");
    }

    [Test]
    public static void LogIsBoundedByCountSizeAndAge()
    {
        var time = new ManualTime(new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero));
        var log = new KQ4WLR.PowerStation.Services.TrafficLog(time);
        for (var i = 0; i < 800; i++) log.Add("rpc", "d", "h", "Switch.Set", "{}", 200, new string('x', 10_000), null, 1);
        var view = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(log.Query(new(null, null, false, false, null, 1000), _ => null)))!;
        Assert.Equal(500, view["total"]!.GetValue<int>(), "at most 500 entries");
        Assert.True(view["entries"]![0]!["Response"]!.GetValue<string>().Length < 2100, "replies trimmed to about 2,000 characters");

        time.Advance(TimeSpan.FromHours(25));
        view = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(log.Query(new(null, null, false, false, null, 1000), _ => null)))!;
        Assert.Equal(0, view["total"]!.GetValue<int>(), "older than a day is dropped");
    }

    [Test]
    public static async Task TurningTheLogOffStopsAndClearsItAndIsRemembered()
    {
        await using var fake = await FakeShellyGen2.StartAsync("shellyplus1-deb000000002", f => f.Switches.Add(new FakeShellyGen2.FakeSwitch()));
        var context = new FakePluginContext();
        await using (var host = await PluginHost.StartAsync(context))
        {
            await host.SendAsync(HttpMethod.Post, "devices", new { host = fake.Host });
            Assert.True((await Log(host)).Count > 0, "on by default");
            var (_, off) = await host.SendAsync(HttpMethod.Put, "debug", new { enabled = false });
            Assert.False(off!["enabled"]!.GetValue<bool>(), "off");
            await host.SendAsync(HttpMethod.Post, $"devices/{fake.DeviceId}/channels/switch/0", new { action = "on" });
            Assert.Equal(0, (await Log(host)).Count, "nothing recorded while off, and cleared");
        }
        await using (var again = await PluginHost.StartAsync(context))
        {
            var (_, body) = await again.SendAsync(HttpMethod.Get, "debug/log");
            Assert.False(body!["enabled"]!.GetValue<bool>(), "stays off after a restart");
        }
    }

    [Test]
    public static async Task Gen1TrafficIsRecordedWithoutTheBasicAuthHeader()
    {
        await using var one = await FakeShellyGen1.StartAsync("SHSW-1", f => { f.Password = "gen1-pw"; f.Relays.Add(new FakeShellyGen1.Relay()); });
        await using var host = await PluginHost.StartAsync();
        var (_, device) = await host.SendAsync(HttpMethod.Post, "devices", new { host = one.Host, password = "gen1-pw" });
        await host.SendAsync(HttpMethod.Post, $"devices/{device!["deviceId"]}/channels/switch/0", new { action = "on" });
        var entries = await Log(host);
        var relay = entries.First(e => e!["method"]!.GetValue<string>() == "/relay/0");
        Assert.Equal("turn=on", relay!["request"]!.GetValue<string>(), "query");
        Assert.DoesNotContain("gen1-pw", entries.ToJsonString(), "password");
        Assert.DoesNotContain("Basic ", entries.ToJsonString(), "header");
    }
}
