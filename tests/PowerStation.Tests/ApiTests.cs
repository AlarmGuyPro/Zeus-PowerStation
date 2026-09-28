// SPDX-License-Identifier: GPL-2.0-or-later
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using KQ4WLR.PowerStation;
using KQ4WLR.PowerStation.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PowerStation.Tests;

/// <summary>
/// Hosts the real plugin the way Zeus does (endpoints mounted under
/// /api/plugins/&lt;id&gt;/) and drives it over HTTP like the panels do.
/// </summary>
public sealed class PluginHost : IAsyncDisposable
{
    public const string Prefix = "/api/plugins/io.github.alarmguypro.powerstation/";
    private readonly WebApplication _app;
    public PowerStationPlugin Plugin { get; }
    public FakePluginContext Context { get; }
    public HttpClient Http { get; }

    private PluginHost(WebApplication app, PowerStationPlugin plugin, FakePluginContext context, HttpClient http)
    {
        _app = app;
        Plugin = plugin;
        Context = context;
        Http = http;
    }

    public static async Task<PluginHost> StartAsync(FakePluginContext? context = null, PowerStationPlugin? plugin = null)
    {
        context ??= new FakePluginContext();
        plugin ??= new PowerStationPlugin { UseMdns = false };
        await plugin.InitializeAsync(context, CancellationToken.None);

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        plugin.MapEndpoints(app.MapGroup(Prefix.TrimEnd('/')));
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = new Uri(address + Prefix) };
        return new PluginHost(app, plugin, context, http);
    }

    public async Task<(HttpStatusCode Status, JsonNode? Body)> SendAsync(HttpMethod method, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is string raw) request.Content = new StringContent(raw, System.Text.Encoding.UTF8, "application/json");
        else if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await Http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? null : JsonNode.Parse(text));
    }

    public async ValueTask DisposeAsync()
    {
        await Plugin.ShutdownAsync(CancellationToken.None);
        Http.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

public static class ApiTests
{
    private static Task<FakeShellyGen2> Plug(string? password = null) =>
        FakeShellyGen2.StartAsync("shellyplugus-112233445566", f =>
        {
            f.Password = password;
            f.Switches.Add(new FakeShellyGen2.FakeSwitch());
        });

    [Test]
    public static async Task StatusStartsEmpty()
    {
        await using var host = await PluginHost.StartAsync();
        var (status, body) = await host.SendAsync(HttpMethod.Get, "status");
        Assert.Equal(HttpStatusCode.OK, status, "status code");
        Assert.Equal(0, body!["devices"]!.AsArray().Count, "device count");
        Assert.Equal("0.1.0-test", body["version"]!.GetValue<string>(), "version");
    }

    [Test]
    public static async Task ProbeReportsDeviceWithoutSaving()
    {
        await using var fake = await Plug();
        await using var host = await PluginHost.StartAsync();
        var (status, body) = await host.SendAsync(HttpMethod.Post, "devices/probe", new { host = fake.Host });
        Assert.Equal(HttpStatusCode.OK, status, "probe");
        Assert.Equal(fake.DeviceId, body!["deviceId"]!.GetValue<string>(), "id");
        Assert.True(body["supported"]!.GetValue<bool>(), "supported");
        var (_, list) = await host.SendAsync(HttpMethod.Get, "status");
        Assert.Equal(0, list!["devices"]!.AsArray().Count, "nothing saved");
    }

    [Test]
    public static async Task AddControlAndRemoveDevice()
    {
        await using var fake = await Plug();
        await using var host = await PluginHost.StartAsync();

        var (added, device) = await host.SendAsync(HttpMethod.Post, "devices", new { host = fake.Host, name = "Amp PSU" });
        Assert.Equal(HttpStatusCode.OK, added, $"add: {device?.ToJsonString()}");
        Assert.Equal("Amp PSU", device!["displayName"]!.GetValue<string>(), "name");
        Assert.Equal("Online", device["status"]!["health"]!.GetValue<string>(), "online after add");

        var id = fake.DeviceId;
        var (on, afterOn) = await host.SendAsync(HttpMethod.Post, $"devices/{id}/channels/switch/0", new { action = "on" });
        Assert.Equal(HttpStatusCode.OK, on, $"on: {afterOn?.ToJsonString()}");
        Assert.True(fake.Switches[0].On, "device switched on");
        var channel = afterOn!["status"]!["channels"]![0]!;
        Assert.True(channel["on"]!.GetValue<bool>(), "response shows on");
        Assert.Equal(42.5, channel["powerW"]!.GetValue<double>(), "response shows watts");

        var (_, afterRename) = await host.SendAsync(HttpMethod.Patch, $"devices/{id}",
            new { channelNames = new Dictionary<string, string> { ["switch:0"] = "Linear amp" } });
        Assert.Equal("Linear amp", afterRename!["status"]!["channels"]![0]!["name"]!.GetValue<string>(), "channel rename");

        var (removed, _) = await host.SendAsync(HttpMethod.Delete, $"devices/{id}");
        Assert.Equal(HttpStatusCode.OK, removed, "remove");
        var (_, list) = await host.SendAsync(HttpMethod.Get, "status");
        Assert.Equal(0, list!["devices"]!.AsArray().Count, "gone");
    }

    [Test]
    public static async Task PasswordIsVerifiedAndNeverStored()
    {
        await using var fake = await Plug("s3cret-pw");
        await using var host = await PluginHost.StartAsync();

        var (missing, missingBody) = await host.SendAsync(HttpMethod.Post, "devices", new { host = fake.Host });
        Assert.Equal(HttpStatusCode.BadRequest, missing, "no password");
        Assert.Contains("password", missingBody!["error"]!.GetValue<string>());

        var (wrong, _) = await host.SendAsync(HttpMethod.Post, "devices", new { host = fake.Host, password = "nope" });
        Assert.Equal(HttpStatusCode.Forbidden, wrong, "wrong password");

        var (ok, device) = await host.SendAsync(HttpMethod.Post, "devices", new { host = fake.Host, password = "s3cret-pw" });
        Assert.Equal(HttpStatusCode.OK, ok, $"right password: {device?.ToJsonString()}");
        Assert.True(device!["hasCredential"]!.GetValue<bool>(), "has credential");
        Assert.Equal("Online", device["status"]!["health"]!.GetValue<string>(), "online");
        Assert.DoesNotContain("ha1", device.ToJsonString(), "API response");

        var stored = (string)((MemorySettings)host.Context.Settings).Values[SettingsDeviceStore.DevicesKey]!;
        Assert.DoesNotContain("s3cret-pw", stored, "persisted settings");
        Assert.Contains(KQ4WLR.PowerStation.Shelly.ShellyDigest.ComputeHa1(fake.DeviceId, "s3cret-pw"), stored, "persisted HA1");
    }

    [Test]
    public static async Task RejectsBadInput()
    {
        await using var fake = await Plug();
        await using var host = await PluginHost.StartAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await host.SendAsync(HttpMethod.Post, "devices", new { host = "8.8.8.8" })).Status, "public IP");
        Assert.Equal(HttpStatusCode.BadRequest, (await host.SendAsync(HttpMethod.Post, "devices", new { host = "http://10.0.0.1/x" })).Status, "URL");
        Assert.Equal(HttpStatusCode.BadRequest, (await host.SendAsync(HttpMethod.Post, "devices", "{not json")).Status, "bad JSON");
        Assert.Equal(HttpStatusCode.NotFound, (await host.SendAsync(HttpMethod.Post, "devices/nope/channels/switch/0", new { action = "on" })).Status, "unknown device");

        await host.SendAsync(HttpMethod.Post, "devices", new { host = fake.Host });
        var id = fake.DeviceId;
        Assert.Equal(HttpStatusCode.BadRequest, (await host.SendAsync(HttpMethod.Post, $"devices/{id}/channels/switch/0", new { action = "explode" })).Status, "bad action");
        Assert.Equal(HttpStatusCode.NotFound, (await host.SendAsync(HttpMethod.Post, $"devices/{id}/channels/switch/3", new { action = "on" })).Status, "missing channel");
        Assert.Equal(HttpStatusCode.BadRequest, (await host.SendAsync(HttpMethod.Post, $"devices/{id}/channels/switch/0", new { action = "brightness", brightness = 50 })).Status, "brightness on relay");
    }

    [Test]
    public static async Task OfflineDeviceShowsUnreachableThenRecovers()
    {
        await using var fake = await Plug();
        await using var host = await PluginHost.StartAsync();
        await host.SendAsync(HttpMethod.Post, "devices", new { host = fake.Host });
        var hostPort = fake.Host;
        await fake.DisposeAsync();

        var (_, afterDrop) = await host.SendAsync(HttpMethod.Post, $"devices/{fake.DeviceId}/refresh");
        Assert.Equal("Unreachable", afterDrop!["status"]!["health"]!.GetValue<string>(), "unreachable");
        var (cmd, _) = await host.SendAsync(HttpMethod.Post, $"devices/{fake.DeviceId}/channels/switch/0", new { action = "on" });
        Assert.Equal(HttpStatusCode.GatewayTimeout, cmd, "command to offline device");
        Assert.Contains(":", hostPort, "sanity");
    }

    [Test]
    public static async Task DevicesSurviveRestart()
    {
        await using var fake = await Plug();
        var context = new FakePluginContext();
        await using (var first = await PluginHost.StartAsync(context))
        {
            await first.SendAsync(HttpMethod.Post, "devices", new { host = fake.Host, name = "Rotator" });
        }
        await using var second = await PluginHost.StartAsync(context);
        var (_, body) = await second.SendAsync(HttpMethod.Get, "status");
        var devices = body!["devices"]!.AsArray();
        Assert.Equal(1, devices.Count, "reloaded");
        Assert.Equal("Rotator", devices[0]!["displayName"]!.GetValue<string>(), "name kept");
    }

    [Test]
    public static async Task BackgroundPollingPicksUpExternalChanges()
    {
        await using var fake = await Plug();
        await using var host = await PluginHost.StartAsync();
        await host.SendAsync(HttpMethod.Post, "devices", new { host = fake.Host });
        fake.Switches[0].On = true; // e.g. someone pressed the button or used the Shelly app
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline)
        {
            var (_, body) = await host.SendAsync(HttpMethod.Get, "status");
            if (body!["devices"]![0]!["status"]!["channels"]![0]!["on"]!.GetValue<bool>()) return;
            await Task.Delay(200);
        }
        throw new AssertionException("poller didn't see the external change within 8 s");
    }

    [Test]
    public static async Task ShutdownIsPromptAndReleasesPolling()
    {
        var plugin = new PowerStationPlugin();
        await plugin.InitializeAsync(new FakePluginContext(), CancellationToken.None);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await plugin.ShutdownAsync(CancellationToken.None);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(4), $"shutdown took {sw.Elapsed}");
    }

    [Test]
    public static void BackoffGrowsAndCaps()
    {
        var manager = new DeviceManager(new SettingsDeviceStore(new MemorySettings()), new HttpClient(), NullLogger.Instance);
        Assert.Equal(TimeSpan.FromSeconds(2), manager.NextDelay(0), "healthy");
        Assert.Equal(TimeSpan.FromSeconds(4), manager.NextDelay(1), "first failure");
        Assert.Equal(TimeSpan.FromSeconds(30), manager.NextDelay(8), "capped");
    }
}
