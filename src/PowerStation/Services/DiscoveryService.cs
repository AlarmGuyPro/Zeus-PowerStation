// SPDX-License-Identifier: GPL-2.0-or-later
using System.Collections.Concurrent;
using System.Net;
using KQ4WLR.PowerStation.Discovery;
using Microsoft.Extensions.Logging;

namespace KQ4WLR.PowerStation.Services;

public sealed record DiscoverySettings
{
    /// <summary>Networks the operator saved for scans and re-finding (CIDR).</summary>
    public IReadOnlyList<string> Networks { get; init; } = [];

    /// <summary>Look for a device automatically when it stops answering at its address.</summary>
    public bool AutoRefind { get; init; } = true;
}

public sealed record DiscoverySettingsRequest(List<string>? Networks, bool? AutoRefind);

public sealed record ScanRequest(List<string>? Networks, bool? Mdns);

public sealed record FoundView
{
    public required FoundDevice Device { get; init; }
    public bool Added { get; init; }
    public string? AddressUpdatedFrom { get; init; }
}

public sealed record ScanState
{
    public bool Running { get; init; }
    public string Phase { get; init; } = "idle";
    public int Probed { get; init; }
    public int Total { get; init; }
    public IReadOnlyList<string> Networks { get; init; } = [];
    public bool UsedMdns { get; init; }
    public IReadOnlyList<FoundView> Found { get; init; } = [];
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
    public bool Cancelled { get; init; }
    public string? Error { get; init; }
}

public sealed record DiscoveryView
{
    public required DiscoverySettings Settings { get; init; }
    public required IReadOnlyList<string> Suggested { get; init; }
    public required ScanState Scan { get; init; }
}

/// <summary>
/// Finds Shelly devices (mDNS on this computer's networks, plus an HTTP sweep
/// of saved networks for other VLANs) and keeps added devices reachable when
/// DHCP gives them a new address. Devices are always matched by their Shelly
/// device ID, never by address.
/// </summary>
public sealed class DiscoveryService : IAsyncDisposable
{
    internal const string SettingsKey = "discovery.v1";

    private readonly IDeviceStore _store;
    private readonly DeviceManager _devices;
    private readonly NetworkScanner _scanner;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly bool _useMdns;
    private readonly object _lock = new();
    private readonly ConcurrentDictionary<string, RefindState> _refind = new(StringComparer.Ordinal);
    private DiscoverySettings _settings = new();
    private ScanState _scan = new();
    private readonly Dictionary<string, FoundView> _found = new(StringComparer.Ordinal);
    private CancellationTokenSource? _scanCts;
    private Task? _scanTask;
    private Task? _refindTask;
    private readonly CancellationTokenSource _life = new();

    private sealed class RefindState
    {
        public string LastHost = "";
        public int Attempts;
        public DateTimeOffset NextAllowed = DateTimeOffset.MinValue;
        public bool Wanted;
    }

    /// <param name="useMdns">False in tests, where multicast isn't meaningful.</param>
    public DiscoveryService(
        IDeviceStore store, DeviceManager devices, NetworkScanner scanner, ILogger logger,
        TimeProvider? time = null, bool useMdns = true)
    {
        _store = store;
        _devices = devices;
        _scanner = scanner;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _useMdns = useMdns;
        _devices.DeviceUnreachable += OnDeviceUnreachable;
    }

    public async Task LoadAsync(CancellationToken ct) =>
        _settings = await _store.LoadDiscoverySettingsAsync(ct).ConfigureAwait(false);

    public DiscoveryView View()
    {
        lock (_lock)
        {
            return new DiscoveryView
            {
                Settings = _settings,
                Suggested = Ipv4Network.LocalNetworks().Select(n => n.ToString()).ToArray(),
                Scan = _scan with { Found = SortedFound() },
            };
        }
    }

    public async Task<DiscoveryView> UpdateSettingsAsync(DiscoverySettingsRequest request, CancellationToken ct)
    {
        var next = _settings;
        if (request.Networks is not null)
        {
            if (request.Networks.Count > 16) throw new PowerStationRequestException(400, "You can save up to 16 networks.");
            var parsed = ParseNetworks(request.Networks);
            next = next with { Networks = parsed.Select(n => n.ToString()).ToArray() };
        }
        if (request.AutoRefind is not null) next = next with { AutoRefind = request.AutoRefind.Value };
        await _store.SaveDiscoverySettingsAsync(next, ct).ConfigureAwait(false);
        lock (_lock) _settings = next;
        return View();
    }

    // ------------------------------------------------------------ operator scan

    public DiscoveryView StartScan(ScanRequest request)
    {
        var networks = request.Networks is { Count: > 0 }
            ? ParseNetworks(request.Networks)
            : _settings.Networks.Count > 0
                ? ParseNetworks(_settings.Networks)
                : Ipv4Network.LocalNetworks();
        var mdns = (request.Mdns ?? true) && _useMdns;
        if (networks.Count == 0 && !mdns)
            throw new PowerStationRequestException(400, "Add a network to scan, for example 192.168.1.0/24.");
        var total = networks.Sum(n => n.HostCount);
        if (total > NetworkScanner.MaxHostsPerScan)
            throw new PowerStationRequestException(400,
                $"That's {total:N0} addresses. Scan up to {NetworkScanner.MaxHostsPerScan:N0} at a time (for example, a few /24 networks).");

        lock (_lock)
        {
            if (_scan.Running) throw new PowerStationRequestException(409, "A scan is already running.");
            _found.Clear();
            _scanCts = CancellationTokenSource.CreateLinkedTokenSource(_life.Token);
            _scan = new ScanState
            {
                Running = true,
                Phase = mdns ? "mdns" : "sweep",
                Total = total,
                Networks = networks.Select(n => n.ToString()).ToArray(),
                UsedMdns = mdns,
                StartedAt = _time.GetUtcNow(),
            };
            var token = _scanCts.Token;
            _scanTask = Task.Run(() => RunScanAsync(networks, mdns, token), CancellationToken.None);
        }
        return View();
    }

    public DiscoveryView CancelScan()
    {
        lock (_lock) _scanCts?.Cancel();
        return View();
    }

    private async Task RunScanAsync(IReadOnlyList<Ipv4Network> networks, bool mdns, CancellationToken ct)
    {
        try
        {
            if (mdns)
            {
                var hits = await ShellyMdns.DiscoverAsync(TimeSpan.FromMilliseconds(1500), ct).ConfigureAwait(false);
                await Task.WhenAll(hits.Select(async h =>
                {
                    var d = await _scanner.ProbeAsync(_scanner.HostFor(h.Address), "mdns", ct).ConfigureAwait(false);
                    if (d is not null) await RecordFoundAsync(d, ct).ConfigureAwait(false);
                })).ConfigureAwait(false);
            }

            lock (_lock) _scan = _scan with { Phase = "sweep" };
            var addresses = networks.SelectMany(n => n.Hosts()).Distinct().ToArray();
            await _scanner.SweepAsync(addresses,
                d => RecordFoundAsync(d, ct),
                n => { lock (_lock) _scan = _scan with { Probed = n }; },
                ct).ConfigureAwait(false);

            lock (_lock) _scan = _scan with { Running = false, Phase = "done", FinishedAt = _time.GetUtcNow() };
        }
        catch (OperationCanceledException)
        {
            lock (_lock) _scan = _scan with { Running = false, Phase = "done", Cancelled = true, FinishedAt = _time.GetUtcNow() };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PowerStation: scan failed");
            lock (_lock) _scan = _scan with { Running = false, Phase = "done", Error = "The scan stopped unexpectedly.", FinishedAt = _time.GetUtcNow() };
        }
    }

    private async Task RecordFoundAsync(FoundDevice device, CancellationToken ct)
    {
        string? movedFrom = null;
        if (_devices.Contains(device.DeviceId))
            movedFrom = await _devices.RelocateIfMovedAsync(device.DeviceId, device.Host, "scan", ct).ConfigureAwait(false);
        lock (_lock)
        {
            if (_found.TryGetValue(device.DeviceId, out var existing))
            {
                var by = existing.Device.FoundBy.Union(device.FoundBy).ToArray();
                _found[device.DeviceId] = existing with
                {
                    Device = existing.Device with { FoundBy = by },
                    AddressUpdatedFrom = existing.AddressUpdatedFrom ?? movedFrom,
                };
            }
            else
            {
                _found[device.DeviceId] = new FoundView
                {
                    Device = device,
                    Added = _devices.Contains(device.DeviceId),
                    AddressUpdatedFrom = movedFrom,
                };
            }
            _scan = _scan with { Found = SortedFound() };
        }
    }

    private FoundView[] SortedFound() =>
        _found.Values
            .Select(f => f with { Added = _devices.Contains(f.Device.DeviceId) })
            .OrderBy(f => f.Added)
            .ThenBy(f => f.Device.Name ?? f.Device.DeviceId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    // ------------------------------------------------------------ automatic re-find

    private void OnDeviceUnreachable(string deviceId, string host, int failures)
    {
        if (!_settings.AutoRefind || failures < _devices.Options.RefindAfterFailures) return;
        var state = _refind.GetOrAdd(deviceId, _ => new RefindState());
        state.LastHost = host;
        if (failures == _devices.Options.RefindAfterFailures)
        {
            // A fresh outage (the device answered in between): start over.
            state.Attempts = 0;
            state.NextAllowed = DateTimeOffset.MinValue;
        }
        if (_time.GetUtcNow() < state.NextAllowed) return;
        state.Wanted = true;
        lock (_lock)
        {
            if (_refindTask is { IsCompleted: false }) return;
            _refindTask = Task.Run(() => RefindAsync(_life.Token), CancellationToken.None);
        }
    }

    private async Task RefindAsync(CancellationToken ct)
    {
        var wanted = _refind.Where(kv => kv.Value.Wanted).ToDictionary(kv => kv.Key, kv => kv.Value);
        if (wanted.Count == 0) return;
        foreach (var s in wanted.Values) s.Wanted = false;
        var missing = new HashSet<string>(wanted.Keys, StringComparer.Ordinal);
        _logger.LogInformation("PowerStation: looking for {Count} device(s) that stopped answering", missing.Count);

        try
        {
            if (_useMdns)
            {
                var hits = await ShellyMdns.DiscoverAsync(TimeSpan.FromMilliseconds(1500), ct).ConfigureAwait(false);
                foreach (var h in hits)
                {
                    var d = await _scanner.ProbeAsync(_scanner.HostFor(h.Address), "mdns", ct).ConfigureAwait(false);
                    if (d is not null && missing.Contains(d.DeviceId) &&
                        await _devices.RelocateIfMovedAsync(d.DeviceId, d.Host, "mDNS", ct).ConfigureAwait(false) is not null)
                        missing.Remove(d.DeviceId);
                }
            }

            // Sweep each missing device's old /24 plus the saved networks, on
            // the port the device used (80 for real devices).
            var groups = missing
                .Select(id => (Id: id, Endpoint: SplitHost(wanted[id].LastHost)))
                .Where(x => x.Endpoint.Address is not null)
                .GroupBy(x => x.Endpoint.Port);
            foreach (var group in groups)
            {
                if (missing.Count == 0) break;
                var nets = group.Select(x => Ipv4Network.Around(x.Endpoint.Address!))
                    .Concat(ParseNetworks(_settings.Networks, quiet: true))
                    .Distinct()
                    .ToArray();
                var addresses = nets.SelectMany(n => n.Hosts()).Distinct().Take(NetworkScanner.MaxHostsPerScan).ToArray();
                var scanner = new NetworkScanner(_scannerHttp, group.Key);
                using var early = CancellationTokenSource.CreateLinkedTokenSource(ct);
                await scanner.SweepAsync(addresses, async d =>
                {
                    lock (missing) if (!missing.Contains(d.DeviceId)) return;
                    var moved = await _devices.RelocateIfMovedAsync(d.DeviceId, d.Host, "network sweep", ct).ConfigureAwait(false);
                    if (moved is null) return;
                    lock (missing)
                    {
                        missing.Remove(d.DeviceId);
                        if (missing.Count == 0) early.Cancel();
                    }
                }, _ => { }, early.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PowerStation: automatic re-find failed");
        }

        var now = _time.GetUtcNow();
        foreach (var id in wanted.Keys)
        {
            if (!missing.Contains(id)) { _refind.TryRemove(id, out _); continue; }
            var s = wanted[id];
            s.Attempts++;
            // 1, 2, 4, 8 ... minutes (by default), capped at 30 minutes, so a
            // device that is really gone doesn't cause constant sweeps.
            var delay = Math.Min(_devices.Options.RefindCooldownMs * Math.Pow(2, s.Attempts - 1), 30 * 60_000);
            s.NextAllowed = now + TimeSpan.FromMilliseconds(delay);
        }
        if (missing.Count > 0)
            _logger.LogInformation("PowerStation: {Count} device(s) still not found; will look again later", missing.Count);
    }

    private HttpClient _scannerHttp => _sharedScanHttp ??= NetworkScanner.CreateScanHttpClient();
    private HttpClient? _sharedScanHttp;

    private static (IPAddress? Address, int Port) SplitHost(string host)
    {
        if (!Uri.TryCreate("http://" + host, UriKind.Absolute, out var uri)) return (null, 80);
        return IPAddress.TryParse(uri.Host.Trim('[', ']'), out var a) && a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
            ? (a, uri.Port)
            : (null, uri.Port);
    }

    private static IReadOnlyList<Ipv4Network> ParseNetworks(IEnumerable<string> texts, bool quiet = false)
    {
        var list = new List<Ipv4Network>();
        foreach (var t in texts)
        {
            if (Ipv4Network.TryParse(t, out var n, out var error)) { if (!list.Contains(n)) list.Add(n); }
            else if (!quiet) throw new PowerStationRequestException(400, error!);
        }
        return list;
    }

    public async ValueTask DisposeAsync()
    {
        _devices.DeviceUnreachable -= OnDeviceUnreachable;
        await _life.CancelAsync().ConfigureAwait(false);
        foreach (var t in new[] { _scanTask, _refindTask })
        {
            if (t is null) continue;
            try { await t.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException) { }
        }
        _sharedScanHttp?.Dispose();
        _life.Dispose();
    }
}
