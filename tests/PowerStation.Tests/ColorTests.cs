// SPDX-License-Identifier: GPL-2.0-or-later
using System.Net;
using System.Text.Json.Nodes;
using KQ4WLR.PowerStation.Model;
using KQ4WLR.PowerStation.Services;

namespace PowerStation.Tests;

public static class ColorTests
{
    private static Task<FakeShellyGen2> Rgbw(bool white = true) =>
        FakeShellyGen2.StartAsync(white ? "shellyplusrgbwpm-a8032ab1c2d3" : "shellyplusrgbwpm-a8032ab1c2d4", f =>
        {
            f.ColorHasWhite = white;
            f.Colors.Add(new FakeShellyGen2.FakeColor { On = false, Rgb = [255, 70, 0], Brightness = 60, Name = "Accent strip" });
        });

    private static JsonNode First(JsonNode device) => device["status"]!["channels"]![0]!;

    [Test]
    public static async Task RgbwStripShowsItsColourAndTakesCommands()
    {
        await using var fake = await Rgbw();
        await using var host = await PluginHost.StartAsync();
        var (_, device) = await host.SendAsync(HttpMethod.Post, "devices", new { host = fake.Host });
        var ch = First(device!);
        Assert.Equal("Rgbw", ch["kind"]!.GetValue<string>(), "kind");
        Assert.Equal("rgbw:0", ch["key"]!.GetValue<string>(), "key");
        Assert.Equal("Accent strip", ch["name"]!.GetValue<string>(), "name");
        Assert.Equal("[255,70,0]", ch["rgb"]!.ToJsonString(), "colour");
        Assert.Equal(0.0, ch["white"]!.GetValue<double>(), "white");

        var id = device!["deviceId"]!.GetValue<string>();
        var (s1, afterColor) = await host.SendAsync(HttpMethod.Post, $"devices/{id}/channels/rgbw/0",
            new { action = "color", rgb = new[] { 255, 0, 0 }, white = 64 });
        Assert.Equal(HttpStatusCode.OK, s1, "colour command");
        Assert.True(First(afterColor!)["on"]!.GetValue<bool>(), "a colour turns it on");
        Assert.Equal("[255,0,0]", First(afterColor!)["rgb"]!.ToJsonString(), "red");
        Assert.Equal(64.0, fake.Colors[0].White, "white sent");

        var (s2, dimmed) = await host.SendAsync(HttpMethod.Post, $"devices/{id}/channels/rgbw/0", new { action = "brightness", brightness = 25 });
        Assert.Equal(HttpStatusCode.OK, s2, "level");
        Assert.Equal(25.0, First(dimmed!)["brightness"]!.GetValue<double>(), "level set");

        var (s3, off) = await host.SendAsync(HttpMethod.Post, $"devices/{id}/channels/rgbw/0", new { action = "off" });
        Assert.Equal(HttpStatusCode.OK, s3, "off");
        Assert.False(First(off!)["on"]!.GetValue<bool>(), "off");
        await host.SendAsync(HttpMethod.Post, $"devices/{id}/channels/rgbw/0", new { action = "toggle" });
        Assert.True(fake.Colors[0].On, "toggle uses RGBW.Toggle");

        var (bad, _) = await host.SendAsync(HttpMethod.Post, $"devices/{id}/channels/rgbw/0", new { action = "color", rgb = new[] { 300, 0, 0 } });
        Assert.Equal(HttpStatusCode.BadRequest, bad, "out of range");
    }

    [Test]
    public static async Task RgbProfileHasNoWhiteChannel()
    {
        await using var fake = await Rgbw(white: false);
        await using var host = await PluginHost.StartAsync();
        var (_, device) = await host.SendAsync(HttpMethod.Post, "devices", new { host = fake.Host });
        Assert.Equal("Rgb", First(device!)["kind"]!.GetValue<string>(), "kind");
        var id = device!["deviceId"]!.GetValue<string>();
        var (ok, _) = await host.SendAsync(HttpMethod.Post, $"devices/{id}/channels/rgb/0", new { action = "color", rgb = new[] { 0, 0, 255 } });
        Assert.Equal(HttpStatusCode.OK, ok, "RGB.Set");
        Assert.Equal("0,0,255", string.Join(",", fake.Colors[0].Rgb), "blue");
        var (bad, _) = await host.SendAsync(HttpMethod.Post, $"devices/{id}/channels/rgb/0", new { action = "color", rgb = new[] { 0, 0, 255 }, white = 10 });
        Assert.Equal(HttpStatusCode.BadRequest, bad, "no white on RGB");
    }

    [Test]
    public static async Task ScenesSetColourAndFade()
    {
        await using var fake = await Rgbw();
        await using var host = await PluginHost.StartAsync();
        await host.SendAsync(HttpMethod.Post, "devices", new { host = fake.Host });
        var (created, scene) = await host.SendAsync(HttpMethod.Post, "scenes", new
        {
            name = "Evening", fadeSeconds = 3,
            targets = new object[] { new { deviceId = fake.DeviceId, kind = "Rgbw", index = 0, on = true, brightness = 30, rgb = new[] { 255, 140, 0 }, white = 60 } },
        });
        Assert.Equal(HttpStatusCode.OK, created, $"create: {scene}");
        Assert.Equal("[255,140,0]", scene!["targets"]![0]!["rgb"]!.ToJsonString(), "stored colour");
        var (_, run) = await host.SendAsync(HttpMethod.Post, $"scenes/{scene["id"]}/run", new { mode = "apply" });
        Assert.Equal(1, run!["succeeded"]!.GetValue<int>(), "applied");
        var c = fake.Colors[0];
        Assert.True(c.On, "on");
        Assert.Equal("255,140,0", string.Join(",", c.Rgb), "amber");
        Assert.Equal(60.0, c.White, "white");
        Assert.Equal(30.0, c.Brightness, "level");
        Assert.Equal(3.0, c.LastTransition ?? 0, "fade");

        await host.SendAsync(HttpMethod.Post, $"scenes/{scene["id"]}/run", new { mode = "off" });
        Assert.False(c.On, "all off");

        var (bad, _) = await host.SendAsync(HttpMethod.Post, "scenes", new
        {
            name = "Bad", targets = new object[] { new { deviceId = fake.DeviceId, kind = "Rgbw", index = 0, on = true, rgb = new[] { 1, 2 } } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, bad, "two-value colour");
    }

    [Test]
    public static async Task OnAirRuleTurnsTheStripRedAndSafetyTimerUsesRgbSet()
    {
        await using var fake = await Rgbw(white: false);
        var radio = new FakeRadio();
        await using var host = await PluginHost.StartAsync(new FakePluginContext { Radio = radio });
        await host.SendAsync(HttpMethod.Post, "devices", new { host = fake.Host });
        var (created, rule) = await host.SendAsync(HttpMethod.Post, "rules", new
        {
            name = "On air", trigger = new { type = "tx" },
            action = new { type = "output", deviceId = fake.DeviceId, kind = "Rgb", index = 0, on = true, brightness = 100, rgb = new[] { 255, 0, 0 } },
            endAction = new { type = "off" },
        });
        Assert.Equal(HttpStatusCode.OK, created, $"rule: {rule}");
        radio.Key(true);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!fake.Colors[0].On && DateTime.UtcNow < deadline) await Task.Delay(100);
        Assert.True(fake.Colors[0].On, "on while transmitting");
        Assert.Equal("255,0,0", string.Join(",", fake.Colors[0].Rgb), "red");
        radio.Key(false);
        deadline = DateTime.UtcNow.AddSeconds(5);
        while (fake.Colors[0].On && DateTime.UtcNow < deadline) await Task.Delay(100);
        Assert.False(fake.Colors[0].On, "off after TX");

        fake.Colors[0].On = true;
        await host.SendAsync(HttpMethod.Patch, $"devices/{fake.DeviceId}", new { safetyMinutes = new Dictionary<string, int> { ["rgb:0"] = 5 } });
        var timer = fake.Calls.LastOrDefault(c => c["method"]!.GetValue<string>() == "RGB.Set" && c["params"]?["toggle_after"] is not null);
        Assert.NotNull(timer, "RGB.Set with toggle_after");
        Assert.Equal(300, timer!["params"]!["toggle_after"]!.GetValue<int>(), "5 minutes");
    }

    [Test]
    public static async Task ConfigWithARepeatedKeyStillConnects()
    {
        // Seen on a real Plus RGBW PM: button_fade_rate appears twice in rgbw:0's config.
        await using var fake = await Rgbw();
        fake.RawConfigResult = """
            {"rgbw:0":{"id":0,"name":"Shack Desk Accent Lights","button_fade_rate":3,"night_mode":{"enable":false},
             "button_fade_rate":3,"button_presets":{"button_doublepush":{"brightness":100}}}}
            """;
        await using var host = await PluginHost.StartAsync();
        var (status, device) = await host.SendAsync(HttpMethod.Post, "devices", new { host = fake.Host });
        Assert.Equal(HttpStatusCode.OK, status, "add");
        Assert.Equal("Online", device!["status"]!["health"]!.GetValue<string>(), $"online: {device["status"]!["message"]}");
        Assert.Equal("Shack Desk Accent Lights", First(device)["name"]!.GetValue<string>(), "name read despite the repeat");

        var lenient = KQ4WLR.PowerStation.Shelly.LenientJson.Parse("""{"a":1,"a":2,"b":{"c":[1,{"d":true,"d":false}]}}""")!;
        Assert.Equal(2, lenient["a"]!.GetValue<long>(), "last one wins");
        Assert.False(lenient["b"]!["c"]![1]!["d"]!.GetValue<bool>(), "nested");
    }

    [Test]
    public static void DcSupplyIsNotJudgedAsMains()
    {
        var time = new ManualTime(DateTimeOffset.UnixEpoch);
        var r = new ReadingsService(new MemorySettings(), time);
        var strip = new DeviceRecord { DeviceId = "strip", Host = "10.0.0.9", Generation = 2, App = "PlusRGBWPM" };
        ChannelState Ch() => new() { Key = "rgbw:0", Kind = ChannelKind.Rgbw, Index = 0, On = true, VoltageV = 24.1, CurrentA = 0.4, PowerW = 9.6 };
        r.Observe(strip, [Ch()]);
        time.Advance(TimeSpan.FromSeconds(10));
        r.Observe(strip, [Ch()]);
        Assert.Equal(0, r.Decorate(strip, [Ch()])[0].Alerts.Count, "24 V DC is not low mains");
    }
}
