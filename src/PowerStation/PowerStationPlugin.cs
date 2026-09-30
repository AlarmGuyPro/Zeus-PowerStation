// SPDX-License-Identifier: GPL-2.0-or-later
using KQ4WLR.PowerStation.Api;
using KQ4WLR.PowerStation.Discovery;
using KQ4WLR.PowerStation.Services;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Zeus.Plugins.Contracts;
using Zeus.Plugins.Contracts.Extensions;

namespace KQ4WLR.PowerStation;

/// <summary>
/// PowerStation entry point. Loads saved Shelly devices, polls them in the
/// background, runs the operator's rules, and exposes the HTTP API the
/// panels use. It reads radio state (ReadRadioState) but never changes it:
/// no ControlRadio capability, no MOX, no PureSignal.
/// </summary>
public sealed class PowerStationPlugin : IZeusPlugin, IBackendPlugin
{
    private IPluginContext? _context;
    private HttpClient? _http;
    private DeviceManager? _manager;
    private DiscoveryService? _discovery;
    private AutomationService? _automations;
    private ReadingsService? _readings;
    private HttpClient? _scanHttp;

    /// <summary>Time source; tests substitute their own.</summary>
    internal TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>Tests drive the automation clock themselves.</summary>
    internal bool RunAutomationClock { get; init; } = true;

    /// <summary>HTTP port probed by scans. 80 for real devices; tests use a simulator port.</summary>
    internal int ScanPort { get; init; } = 80;

    /// <summary>mDNS is skipped in tests, where multicast isn't meaningful.</summary>
    internal bool UseMdns { get; init; } = true;

    public async Task InitializeAsync(IPluginContext context, CancellationToken ct)
    {
        _context = context;
        _http = CreateLanHttpClient();
        _scanHttp = NetworkScanner.CreateScanHttpClient();
        var store = new SettingsDeviceStore(context.Settings);
        // Every output change checks this first; only the on-air light rule may act during TX.
        _tx = new TxInterlock(context.Radio, Time);
        var manager = new DeviceManager(store, _http, context.Logger, Time, tx: _tx);
        await manager.LoadAsync(ct).ConfigureAwait(false);
        var discovery = new DiscoveryService(store, manager, new NetworkScanner(_scanHttp, ScanPort), context.Logger, useMdns: UseMdns);
        await discovery.LoadAsync(ct).ConfigureAwait(false);

        var readings = new ReadingsService(context.Settings, Time);
        await readings.LoadAsync(ct).ConfigureAwait(false);
        manager.Polled += readings.Observe;
        manager.Decorate = readings.Decorate;

        // Radio state is read-only: PowerStation never declares ControlRadio.
        var automations = new AutomationService(context.Settings, manager, context.Radio, context.Logger, Time);
        await automations.LoadAsync(ct).ConfigureAwait(false);

        manager.StartPolling();
        automations.Start(RunAutomationClock);
        _manager = manager;
        _discovery = discovery;
        _readings = readings;
        _automations = automations;
        if (context.Radio is null)
            context.Logger.LogInformation("PowerStation: no radio state available; band, frequency and TX rules will wait");
        context.Logger.LogInformation("PowerStation {Version} started", context.Manifest.Version);
    }

    public async Task ShutdownAsync(CancellationToken ct)
    {
        var manager = _manager;
        var discovery = _discovery;
        var automations = _automations;
        _manager = null;
        _discovery = null;
        _automations = null;
        _readings = null;
        if (automations is not null)
        {
            // Zeus allows 5 s for shutdown; leave room for the rest.
            await automations.StopAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            await automations.DisposeAsync().ConfigureAwait(false);
        }
        if (discovery is not null) await discovery.DisposeAsync().ConfigureAwait(false);
        if (manager is not null) await manager.DisposeAsync().ConfigureAwait(false);
        _scanHttp?.Dispose();
        _scanHttp = null;
        _http?.Dispose();
        _http = null;
        _tx?.Dispose();
        _tx = null;
        _context?.Logger.LogInformation("PowerStation stopped");
        _context = null;
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints) =>
        PowerStationEndpoints.Map(endpoints, () => _manager, () => _discovery, () => _automations, () => _readings,
            _context?.Manifest.Version ?? "0.0.0");

    internal AutomationService? Automations => _automations;

    private TxInterlock? _tx;

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
