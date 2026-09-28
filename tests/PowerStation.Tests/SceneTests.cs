// SPDX-License-Identifier: GPL-2.0-or-later
using System.Net;
using System.Text.Json.Nodes;
using KQ4WLR.PowerStation.Services;

namespace PowerStation.Tests;

public static class SceneTests
{
    private static async Task<(FakeShellyGen2 Rack, FakeShellyGen2 Dimmer, PluginHost Host)> StationAsync()
    {
        var rack = await FakeShellyGen2.StartAsync("shellypro4pm-000000000001", f =>
        {
            for (var i = 0; i < 4; i++) f.Switches.Add(new FakeShellyGen2.FakeSwitch());
        });
        var dimmer = await FakeShellyGen2.StartAsync("shellydimmerg3-000000000002", f =>
            f.Lights.Add(new FakeShellyGen2.FakeLight { On = false, Brightness = 20 }));
        var host = await PluginHost.StartAsync();
        await host.SendAsync(HttpMethod.Post, "devices", new { host = rack.Host, name = "Rack" });
        await host.SendAsync(HttpMethod.Post, "devices", new { host = dimmer.Host, name = "Lamp" });
        return (rack, dimmer, host);
    }

    private static object Operating(FakeShellyGen2 rack, FakeShellyGen2 dimmer) => new
    {
        name = "Operating",
        fadeSeconds = 2,
        targets = new object[]
        {
            new { deviceId = rack.DeviceId, kind = "Switch", index = 0, on = true },
            new { deviceId = rack.DeviceId, kind = "Switch", index = 1, on = true },
            new { deviceId = rack.DeviceId, kind = "Switch", index = 3, on = false },
            new { deviceId = dimmer.DeviceId, kind = "Light", index = 0, on = true, brightness = 70 },
        },
    };

    [Test]
    public static async Task CreateRunAndTurnGroupOff()
    {
        var (rack, dimmer, host) = await StationAsync();
        await using var _ = rack; await using var __ = dimmer; await using var ___ = host;
        rack.Switches[3].On = true;

        var (created, scene) = await host.SendAsync(HttpMethod.Post, "scenes", Operating(rack, dimmer));
        Assert.Equal(HttpStatusCode.OK, created, $"create: {scene?.ToJsonString()}");
        var id = scene!["id"]!.GetValue<string>();

        var (ran, result) = await host.SendAsync(HttpMethod.Post, $"scenes/{id}/run", new { mode = "apply" });
        Assert.Equal(HttpStatusCode.OK, ran, $"run: {result?.ToJsonString()}");
        Assert.Equal(4, result!["succeeded"]!.GetValue<int>(), "succeeded");
        Assert.True(rack.Switches[0].On && rack.Switches[1].On, "rack outputs on");
        Assert.False(rack.Switches[2].On, "untouched output left alone");
        Assert.False(rack.Switches[3].On, "output set off by the scene");
        Assert.True(dimmer.Lights[0].On, "lamp on");
        Assert.Equal(70.0, dimmer.Lights[0].Brightness, "lamp level");
        var lightSet = dimmer.Calls.Last(c => c["method"]!.GetValue<string>() == "Light.Set");
        Assert.Equal(2.0, lightSet["params"]!["transition_duration"]!.GetValue<double>(), "fade sent to dimmer");
        Assert.Equal(2, result["devices"]!.AsArray().Count, "both devices returned with fresh state");

        var (_, off) = await host.SendAsync(HttpMethod.Post, $"scenes/{id}/run", new { mode = "off" });
        Assert.Equal(4, off!["succeeded"]!.GetValue<int>(), "off succeeded");
        Assert.False(rack.Switches[0].On, "rack 0 off");
        Assert.False(rack.Switches[1].On, "rack 1 off");
        Assert.False(dimmer.Lights[0].On, $"lamp off: {off.ToJsonString()}");
        Assert.Equal(70.0, dimmer.Lights[0].Brightness, "level kept for next time");
    }

    [Test]
    public static async Task RunWithoutBodyApplies()
    {
        var (rack, dimmer, host) = await StationAsync();
        await using var _ = rack; await using var __ = dimmer; await using var ___ = host;
        var (_, scene) = await host.SendAsync(HttpMethod.Post, "scenes", Operating(rack, dimmer));
        var (ran, _) = await host.SendAsync(HttpMethod.Post, $"scenes/{scene!["id"]!.GetValue<string>()}/run");
        Assert.Equal(HttpStatusCode.OK, ran, "run with empty body");
        Assert.True(rack.Switches[0].On, "applied");
    }

    [Test]
    public static async Task OneOfflineDeviceDoesNotStopTheRest()
    {
        var (rack, dimmer, host) = await StationAsync();
        await using var _ = rack; await using var ___ = host;
        var (_, scene) = await host.SendAsync(HttpMethod.Post, "scenes", Operating(rack, dimmer));
        await dimmer.DisposeAsync();

        var (ran, result) = await host.SendAsync(HttpMethod.Post, $"scenes/{scene!["id"]!.GetValue<string>()}/run");
        Assert.Equal(HttpStatusCode.OK, ran, "run returns results even with failures");
        Assert.Equal(3, result!["succeeded"]!.GetValue<int>(), "rack outputs still applied");
        Assert.Equal(1, result["failed"]!.GetValue<int>(), "lamp failed");
        Assert.Contains("Lamp:", result["results"]!.AsArray().First(r => !r!["ok"]!.GetValue<bool>())!["error"]!.GetValue<string>());
        Assert.True(rack.Switches[0].On, "rack applied");
    }

    [Test]
    public static async Task ValidatesScenes()
    {
        var (rack, dimmer, host) = await StationAsync();
        await using var _ = rack; await using var __ = dimmer; await using var ___ = host;
        async Task<HttpStatusCode> Create(object body) => (await host.SendAsync(HttpMethod.Post, "scenes", body)).Status;

        Assert.Equal(HttpStatusCode.BadRequest, await Create(new { name = "", targets = new[] { new { deviceId = rack.DeviceId, kind = "Switch", index = 0, on = true } } }), "no name");
        Assert.Equal(HttpStatusCode.BadRequest, await Create(new { name = "Empty", targets = Array.Empty<object>() }), "no targets");
        Assert.Equal(HttpStatusCode.BadRequest, await Create(new { name = "Ghost", targets = new[] { new { deviceId = "nope", kind = "Switch", index = 0, on = true } } }), "unknown device");
        Assert.Equal(HttpStatusCode.BadRequest, await Create(new { name = "Twice", targets = new[] {
            new { deviceId = rack.DeviceId, kind = "Switch", index = 0, on = true },
            new { deviceId = rack.DeviceId, kind = "Switch", index = 0, on = false } } }), "duplicate output");
        Assert.Equal(HttpStatusCode.BadRequest, await Create(new { name = "Bright relay", targets = new object[] {
            new { deviceId = rack.DeviceId, kind = "Switch", index = 0, on = true, brightness = 50 } } }), "level on a relay");
        Assert.Equal(HttpStatusCode.BadRequest, await Create(new { name = "Too bright", targets = new object[] {
            new { deviceId = dimmer.DeviceId, kind = "Light", index = 0, on = true, brightness = 150 } } }), "level > 100");
        Assert.Equal(HttpStatusCode.OK, await Create(Operating(rack, dimmer)), "valid");
        Assert.Equal(HttpStatusCode.Conflict, await Create(Operating(rack, dimmer)), "duplicate name");
        Assert.Equal(HttpStatusCode.NotFound, (await host.SendAsync(HttpMethod.Post, "scenes/missing/run")).Status, "unknown scene");
    }

    [Test]
    public static async Task EditDeleteAndPersist()
    {
        var (rack, dimmer, _) = (await FakeShellyGen2.StartAsync("shellypro4pm-000000000003", f => f.Switches.Add(new FakeShellyGen2.FakeSwitch())),
            await FakeShellyGen2.StartAsync("shellydimmerg3-000000000004", f => f.Lights.Add(new FakeShellyGen2.FakeLight())), 0);
        await using var r = rack; await using var d = dimmer;
        var context = new FakePluginContext();
        string id;
        await using (var host = await PluginHost.StartAsync(context))
        {
            await host.SendAsync(HttpMethod.Post, "devices", new { host = rack.Host });
            await host.SendAsync(HttpMethod.Post, "devices", new { host = dimmer.Host });
            var (_, scene) = await host.SendAsync(HttpMethod.Post, "scenes", new
            {
                name = "Evening",
                targets = new object[] { new { deviceId = dimmer.DeviceId, kind = "Light", index = 0, on = true, brightness = 30 } },
            });
            id = scene!["id"]!.GetValue<string>();
            var (updated, edited) = await host.SendAsync(HttpMethod.Put, $"scenes/{id}", new
            {
                name = "Late night",
                targets = new object[] { new { deviceId = dimmer.DeviceId, kind = "Light", index = 0, on = true, brightness = 10 } },
            });
            Assert.Equal(HttpStatusCode.OK, updated, "update");
            Assert.Equal("Late night", edited!["name"]!.GetValue<string>(), "renamed");
        }

        await using (var host = await PluginHost.StartAsync(context))
        {
            var (_, status) = await host.SendAsync(HttpMethod.Get, "status");
            var scenes = status!["scenes"]!.AsArray();
            Assert.Equal(1, scenes.Count, "scene survived restart");
            Assert.Equal(10.0, scenes[0]!["targets"]![0]!["brightness"]!.GetValue<double>(), "level survived restart");
            Assert.Equal(HttpStatusCode.OK, (await host.SendAsync(HttpMethod.Delete, $"scenes/{id}")).Status, "delete");
            var (_, after) = await host.SendAsync(HttpMethod.Get, "status");
            Assert.Equal(0, after!["scenes"]!.AsArray().Count, "deleted");
        }
    }

    [Test]
    public static async Task RemovingDeviceDropsItFromScenes()
    {
        var (rack, dimmer, host) = await StationAsync();
        await using var _ = rack; await using var __ = dimmer; await using var ___ = host;
        var (_, scene) = await host.SendAsync(HttpMethod.Post, "scenes", Operating(rack, dimmer));
        await host.SendAsync(HttpMethod.Delete, $"devices/{dimmer.DeviceId}");
        var (_, status) = await host.SendAsync(HttpMethod.Get, "status");
        var targets = status!["scenes"]![0]!["targets"]!.AsArray();
        Assert.Equal(3, targets.Count, "lamp removed from scene");
        Assert.True(targets.All(t => t!["deviceId"]!.GetValue<string>() == rack.DeviceId), "only rack left");
        Assert.Equal(scene!["id"]!.GetValue<string>(), status["scenes"]![0]!["id"]!.GetValue<string>(), "scene kept");
    }
}
