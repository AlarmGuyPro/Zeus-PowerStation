// SPDX-License-Identifier: GPL-2.0-or-later
using System.Net;
using System.Text.Json.Nodes;

namespace PowerStation.Tests;

public static class LayoutTests
{
    private static async Task<(FakeShellyGen2 A, FakeShellyGen2 B, FakeShellyGen2 C)> ThreeDevices()
    {
        var a = await FakeShellyGen2.StartAsync("shelly1g4-00000000000a", f => f.Switches.Add(new FakeShellyGen2.FakeSwitch()));
        var b = await FakeShellyGen2.StartAsync("shelly1g4-00000000000b", f => f.Switches.Add(new FakeShellyGen2.FakeSwitch()));
        var c = await FakeShellyGen2.StartAsync("shellydimmerg3-00000000000c", f => f.Lights.Add(new FakeShellyGen2.FakeLight()));
        return (a, b, c);
    }

    [Test]
    public static async Task LayoutIsSavedAndSurvivesRestart()
    {
        var (a, b, c) = await ThreeDevices();
        await using var _a = a; await using var _b = b; await using var _c = c;
        var context = new FakePluginContext();
        await using (var host = await PluginHost.StartAsync(context))
        {
            foreach (var d in new[] { a, b, c }) await host.SendAsync(HttpMethod.Post, "devices", new { host = d.Host });
            var (_, before) = await host.SendAsync(HttpMethod.Get, "status");
            Assert.Equal(null, before!["layout"], "no layout until the operator arranges");

            var (saved, layout) = await host.SendAsync(HttpMethod.Put, "layout", new
            {
                columns = 2,
                order = new[] { new[] { c.DeviceId, a.DeviceId }, new[] { b.DeviceId } },
            });
            Assert.Equal(HttpStatusCode.OK, saved, $"save: {layout?.ToJsonString()}");
            Assert.Equal(2, layout!["columns"]!.GetValue<int>(), "columns");
        }

        await using (var host = await PluginHost.StartAsync(context))
        {
            var (_, status) = await host.SendAsync(HttpMethod.Get, "status");
            var layout = status!["layout"]!;
            Assert.Equal(2, layout["columns"]!.GetValue<int>(), "columns after restart");
            Assert.Equal(c.DeviceId, layout["order"]![0]![0]!.GetValue<string>(), "first card after restart");
            Assert.Equal(b.DeviceId, layout["order"]![1]![0]!.GetValue<string>(), "second column after restart");
        }
    }

    [Test]
    public static async Task RemovedDeviceDisappearsFromLayout()
    {
        var (a, b, c) = await ThreeDevices();
        await using var _a = a; await using var _b = b; await using var _c = c;
        await using var host = await PluginHost.StartAsync();
        foreach (var d in new[] { a, b, c }) await host.SendAsync(HttpMethod.Post, "devices", new { host = d.Host });
        await host.SendAsync(HttpMethod.Put, "layout", new { columns = 3, order = new[] { new[] { a.DeviceId }, new[] { b.DeviceId }, new[] { c.DeviceId } } });
        await host.SendAsync(HttpMethod.Delete, $"devices/{b.DeviceId}");
        var (_, status) = await host.SendAsync(HttpMethod.Get, "status");
        var ids = status!["layout"]!["order"]!.AsArray().SelectMany(col => col!.AsArray()).Select(x => x!.GetValue<string>()).ToArray();
        Assert.Equal(2, ids.Length, "removed device dropped");
        Assert.False(ids.Contains(b.DeviceId), "b gone");
    }

    [Test]
    public static async Task LayoutRequestsAreValidated()
    {
        var (a, b, c) = await ThreeDevices();
        await using var _a = a; await using var _b = b; await using var _c = c;
        await using var host = await PluginHost.StartAsync();
        await host.SendAsync(HttpMethod.Post, "devices", new { host = a.Host });

        async Task<HttpStatusCode> Put(object body) => (await host.SendAsync(HttpMethod.Put, "layout", body)).Status;
        Assert.Equal(HttpStatusCode.BadRequest, await Put(new { columns = 5, order = Array.Empty<string[]>() }), "5 columns");
        Assert.Equal(HttpStatusCode.BadRequest, await Put(new { columns = -1, order = Array.Empty<string[]>() }), "negative");
        Assert.Equal(HttpStatusCode.BadRequest, await Put(new { columns = 1, order = new[] { new[] { a.DeviceId }, Array.Empty<string>() } }), "more columns than chosen");
        Assert.Equal(HttpStatusCode.BadRequest, await Put(new { columns = 2, order = new[] { new[] { a.DeviceId }, new[] { a.DeviceId } } }), "duplicate");

        var (ok, layout) = await host.SendAsync(HttpMethod.Put, "layout", new { columns = 0, order = new[] { new[] { "shelly-not-added", a.DeviceId } } });
        Assert.Equal(HttpStatusCode.OK, ok, "auto with an unknown id");
        var only = layout!["order"]![0]!.AsArray();
        Assert.Equal(1, only.Count, "unknown id dropped");
        Assert.Equal(a.DeviceId, only[0]!.GetValue<string>(), "known id kept");
    }
}
