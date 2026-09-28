// SPDX-License-Identifier: GPL-2.0-or-later
using KQ4WLR.PowerStation.Api;
using KQ4WLR.PowerStation.Services;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Zeus.Plugins.Contracts;
using Zeus.Plugins.Contracts.Extensions;

namespace KQ4WLR.PowerStation;

/// <summary>
/// PowerStation entry point. Loads saved Shelly devices, polls them in the
/// background, and exposes the HTTP API the panels use. It never touches the
/// radio: no ControlRadio capability, no MOX, no PureSignal.
/// </summary>
public sealed class PowerStationPlugin : IZeusPlugin, IBackendPlugin
{
    private IPluginContext? _context;
    private HttpClient? _http;
    private DeviceManager? _manager;

    public async Task InitializeAsync(IPluginContext context, CancellationToken ct)
    {
        _context = context;
        _http = CreateLanHttpClient();
        var manager = new DeviceManager(new SettingsDeviceStore(context.Settings), _http, context.Logger);
        await manager.LoadAsync(ct).ConfigureAwait(false);
        manager.StartPolling();
        _manager = manager;
        context.Logger.LogInformation("PowerStation {Version} started", context.Manifest.Version);
    }

    public async Task ShutdownAsync(CancellationToken ct)
    {
        var manager = _manager;
        _manager = null;
        if (manager is not null) await manager.DisposeAsync().ConfigureAwait(false);
        _http?.Dispose();
        _http = null;
        _context?.Logger.LogInformation("PowerStation stopped");
        _context = null;
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints) =>
        PowerStationEndpoints.Map(endpoints, () => _manager, _context?.Manifest.Version ?? "0.0.0");

    /// <summary>
    /// Shelly devices live on the LAN: never route them through a system
    /// proxy, never follow redirects, and fail fast when a device is offline.
    /// </summary>
    internal static HttpClient CreateLanHttpClient() =>
        new(new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(2),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
            MaxConnectionsPerServer = 2,
        })
        {
            Timeout = TimeSpan.FromSeconds(4),
        };
}
