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
