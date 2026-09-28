// SPDX-License-Identifier: GPL-2.0-or-later
using System.Collections.Concurrent;
using KQ4WLR.PowerStation.Model;
using KQ4WLR.PowerStation.Shelly;
using Microsoft.Extensions.Logging;

namespace KQ4WLR.PowerStation.Services;

public sealed record AddDeviceRequest(string? Host, string? Name, string? Password);

public sealed record UpdateDeviceRequest(
    string? Name,
    string? Host,
    string? Password,
    bool? ClearPassword,
    Dictionary<string, string?>? ChannelNames);

public sealed record ChannelCommand(
    string? Action,
    double? Brightness,
    double? TransitionSeconds,
    string? Direction);

/// <summary>A device as the UI sees it: record (minus secrets) plus live status.</summary>
public sealed record DeviceView
{
    public required string DeviceId { get; init; }
    public required string DisplayName { get; init; }
    public string? Name { get; init; }
    public required string Host { get; init; }
    public required int Generation { get; init; }
    public string? Model { get; init; }
    public string? App { get; init; }
    public string? Mac { get; init; }
    public bool AuthRequired { get; init; }
    public bool HasCredential { get; init; }
    public string? PreviousHost { get; init; }
    public DateTimeOffset? HostChangedAt { get; init; }
    public required DeviceStatus Status { get; init; }
}

/// <summary>Raised for operator-fixable request problems (maps to HTTP 400/404/409).</summary>
public sealed class PowerStationRequestException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

/// <summary>
/// Owns the device list, one client per device, live status, and the poll
/// loop. All mutations go through here so persistence and polling stay
/// consistent.
/// </summary>
public sealed class DeviceManager : IAsyncDisposable
{
    private readonly IDeviceStore _store;
    private readonly HttpClient _http;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly Func<string, CancellationToken, Task<string?>> _checkHost;
    private readonly SemaphoreSlim _mutate = new(1, 1);
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private PowerStationOptions _options = new();
    private CancellationTokenSource? _loopCts;
    private Task? _loop;

    public DeviceManager(
        IDeviceStore store,
        HttpClient http,
        ILogger logger,
        TimeProvider? time = null,
        Func<string, CancellationToken, Task<string?>>? checkHost = null)
    {
        _store = store;
        _http = http;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _checkHost = checkHost ?? HostValidator.CheckLocalAsync;
    }

    public PowerStationOptions Options => _options;

    /// <summary>Saved scenes; available after <see cref="LoadAsync"/>.</summary>
    public SceneManager Scenes { get; private set; } = null!;

    public bool Contains(string deviceId) => _entries.ContainsKey(deviceId);

    private Layout? _layout;

    /// <summary>The operator's Status-tab grid, without devices that no longer exist.</summary>
    public Layout? Layout => _layout is null ? null : _layout with
    {
        Order = _layout.Order.Select(col => (IReadOnlyList<string>)col.Where(_entries.ContainsKey).ToArray()).ToArray(),
    };

    public async Task<Layout> SaveLayoutAsync(Layout request, CancellationToken ct)
    {
        if (request.Columns is < 0 or > 4)
            throw new PowerStationRequestException(400, "Columns must be Auto or 1 to 4.");
        var max = Math.Max(1, request.Columns);
        var order = request.Order ?? [];
        if (order.Count > max)
            throw new PowerStationRequestException(400, $"The layout has {order.Count} columns but {max} were chosen.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var clean = new List<IReadOnlyList<string>>();
        foreach (var col in order)
        {
            var ids = new List<string>();
            foreach (var id in col ?? [])
            {
                if (string.IsNullOrWhiteSpace(id) || id.Length > 128 || !seen.Add(id))
                    throw new PowerStationRequestException(400, "Each device can appear only once in the layout.");
                if (_entries.ContainsKey(id)) ids.Add(id); // quietly drop devices removed meanwhile
            }
            clean.Add(ids);
        }
        var layout = new Layout { Columns = request.Columns, Order = clean };
        await _store.SaveLayoutAsync(layout, ct).ConfigureAwait(false);
        _layout = layout;
        return Layout!;
    }

    /// <summary>Raised after each poll that couldn't reach a device: (device id, address, consecutive failures).</summary>
    public event Action<string, string, int>? DeviceUnreachable;

    private sealed class Entry(DeviceRecord record, IShellyClient client)
    {
        public DeviceRecord Record { get; set; } = record;
        public IShellyClient Client { get; set; } = client;
        public DeviceStatus Status { get; set; } = new();
        public DateTimeOffset NextPoll { get; set; } = DateTimeOffset.MinValue;
        public int Failures { get; set; }
        public SemaphoreSlim PollGate { get; } = new(1, 1);
    }

    // ------------------------------------------------------------ lifecycle

    public async Task LoadAsync(CancellationToken ct)
    {
        _options = await _store.LoadOptionsAsync(ct).ConfigureAwait(false);
        foreach (var record in await _store.LoadDevicesAsync(ct).ConfigureAwait(false))
        {
            if (record.Generation < 2)
            {
                _logger.LogWarning("Skipping Gen1 device {DeviceId}: Gen1 support is not in this version", record.DeviceId);
                continue;
            }
            _entries[record.DeviceId] = new Entry(record, CreateClient(record));
        }
        _layout = await _store.LoadLayoutAsync(ct).ConfigureAwait(false);
        Scenes = new SceneManager(_store, this);
        await Scenes.LoadAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("PowerStation loaded {Count} device(s) and {Scenes} scene(s)",
            _entries.Count, Scenes.List().Count);
    }

    public void StartPolling()
    {
        if (_loop is not null) return;
        _loopCts = new CancellationTokenSource();
        var token = _loopCts.Token;
        _loop = Task.Run(() => PollLoopAsync(token), CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        if (_loopCts is not null)
        {
            await _loopCts.CancelAsync().ConfigureAwait(false);
            if (_loop is not null)
            {
                try { await _loop.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
                catch (Exception ex) when (ex is OperationCanceledException or TimeoutException) { }
            }
            _loopCts.Dispose();
        }
        _loopCts = null;
        _loop = null;
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        var tick = TimeSpan.FromMilliseconds(250);
        while (!ct.IsCancellationRequested)
        {
            var now = _time.GetUtcNow();
            var due = _entries.Values.Where(e => e.NextPoll <= now).ToList();
            if (due.Count > 0)
                await Task.WhenAll(due.Select(e => PollEntryAsync(e, ct, waitForTurn: false))).ConfigureAwait(false);
            try { await Task.Delay(tick, _time, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <param name="waitForTurn">
    /// The background loop skips a device that is already being polled.
    /// Operator actions (add, command, refresh) wait instead, so the state
    /// they return reflects the change they just made.
    /// </param>
    private async Task PollEntryAsync(Entry entry, CancellationToken ct, bool waitForTurn = true)
    {
        if (waitForTurn) await entry.PollGate.WaitAsync(ct).ConfigureAwait(false);
        else if (!await entry.PollGate.WaitAsync(0, ct).ConfigureAwait(false)) return;
        try
        {
            var now = _time.GetUtcNow();
            try
            {
                var channels = await entry.Client.GetStatusAsync(ct).ConfigureAwait(false);
                entry.Failures = 0;
                entry.Status = new DeviceStatus
                {
                    Health = DeviceHealth.Online,
                    LastSeen = now,
                    LastPolled = now,
                    Channels = ApplyNames(entry.Record, channels),
                };
            }
            catch (ShellyException ex)
            {
                entry.Failures++;
                if (ex.Kind == ShellyErrorKind.Unreachable)
                {
                    try { DeviceUnreachable?.Invoke(entry.Record.DeviceId, entry.Record.Host, entry.Failures); }
                    catch (Exception hookError) { _logger.LogError(hookError, "PowerStation: re-find hook failed"); }
                }
                if (entry.Failures == 1)
                    _logger.LogWarning("PowerStation: {Device} {Kind}: {Message}", entry.Record.DeviceId, ex.Kind, ex.Message);
                entry.Status = entry.Status with
                {
                    Health = ex.Kind switch
                    {
                        ShellyErrorKind.Unreachable => DeviceHealth.Unreachable,
                        ShellyErrorKind.Unauthorized => DeviceHealth.Unauthorized,
                        _ => DeviceHealth.Error,
                    },
                    Message = ex.Message,
                    LastPolled = now,
                };
            }
            entry.NextPoll = _time.GetUtcNow() + NextDelay(entry.Failures);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PowerStation: unexpected error polling {Device}", entry.Record.DeviceId);
            entry.NextPoll = _time.GetUtcNow() + TimeSpan.FromMilliseconds(_options.MaxBackoffMs);
        }
        finally
        {
            entry.PollGate.Release();
        }
    }

    internal TimeSpan NextDelay(int failures)
    {
        if (failures <= 0) return TimeSpan.FromMilliseconds(_options.PollIntervalMs);
        var ms = _options.PollIntervalMs * Math.Pow(2, Math.Min(failures, 10));
        return TimeSpan.FromMilliseconds(Math.Min(ms, _options.MaxBackoffMs));
    }

    private static IReadOnlyList<ChannelState> ApplyNames(DeviceRecord record, IReadOnlyList<ChannelState> channels) =>
        channels.Select(c => record.ChannelNames.TryGetValue(c.Key, out var name) ? c with { Name = name } : c).ToArray();

    // ------------------------------------------------------------ queries

    public IReadOnlyList<DeviceView> List() =>
        _entries.Values
            .Select(ToView)
            .OrderBy(v => v.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public DeviceView Get(string deviceId) => ToView(Find(deviceId));

    private static DeviceView ToView(Entry e) => new()
    {
        DeviceId = e.Record.DeviceId,
        DisplayName = e.Record.Name ?? e.Record.DeviceId,
        Name = e.Record.Name,
        Host = e.Record.Host,
        Generation = e.Record.Generation,
        Model = e.Record.Model,
        App = e.Record.App,
        Mac = e.Record.Mac,
        AuthRequired = e.Record.AuthRequired,
        HasCredential = e.Record.Ha1 is not null,
        PreviousHost = e.Record.PreviousHost,
        HostChangedAt = e.Record.HostChangedAt,
        Status = e.Status,
    };

    private Entry Find(string deviceId) =>
        _entries.TryGetValue(deviceId, out var e)
            ? e
            : throw new PowerStationRequestException(404, "That device isn't in PowerStation.");

    // ------------------------------------------------------------ mutations

    /// <summary>Checks an address and reports what's there, without saving.</summary>
    public async Task<DeviceIdentity> ProbeAsync(string? host, CancellationToken ct)
    {
        var normalized = await ValidateHostAsync(host, ct).ConfigureAwait(false);
        return await Gen2Client.IdentifyAsync(_http, normalized, ct).ConfigureAwait(false);
    }

    public async Task<DeviceView> AddAsync(AddDeviceRequest request, CancellationToken ct)
    {
        var host = await ValidateHostAsync(request.Host, ct).ConfigureAwait(false);
        var identity = await Gen2Client.IdentifyAsync(_http, host, ct).ConfigureAwait(false);
        if (identity.Generation < 2)
            throw new ShellyException(ShellyErrorKind.Unsupported,
                $"{identity.Model ?? "This"} is a Gen1 Shelly. Gen1 support is coming in the next PowerStation update.");

        string? ha1 = null;
        if (identity.AuthRequired)
        {
            if (string.IsNullOrEmpty(request.Password))
                throw new PowerStationRequestException(400,
                    "This device has a password set. Enter it to add the device.");
            ha1 = await Gen2Client.DeriveHa1Async(_http, host, request.Password, ct).ConfigureAwait(false);
        }

        await _mutate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var name = Clean(request.Name) ?? Clean(identity.DefaultName);
            DeviceRecord record;
            if (_entries.TryGetValue(identity.DeviceId, out var existing))
            {
                // Same physical device (matched by Shelly id): treat as a re-find.
                record = existing.Record with
                {
                    Host = host,
                    Name = name ?? existing.Record.Name,
                    AuthRequired = identity.AuthRequired,
                    Ha1 = ha1 ?? (identity.AuthRequired ? existing.Record.Ha1 : null),
                    Model = identity.Model,
                    App = identity.App,
                };
            }
            else
            {
                record = new DeviceRecord
                {
                    DeviceId = identity.DeviceId,
                    Host = host,
                    Generation = identity.Generation,
                    Name = name,
                    Model = identity.Model,
                    App = identity.App,
                    Mac = identity.Mac,
                    AuthRequired = identity.AuthRequired,
                    Ha1 = ha1,
                    AddedAt = _time.GetUtcNow(),
                };
            }
            await ReplaceAsync(record, ct).ConfigureAwait(false);
        }
        finally
        {
            _mutate.Release();
        }

        var entry = Find(identity.DeviceId);
        await PollEntryAsync(entry, ct).ConfigureAwait(false);
        return ToView(entry);
    }

    public async Task<DeviceView> UpdateAsync(string deviceId, UpdateDeviceRequest request, CancellationToken ct)
    {
        var current = Find(deviceId).Record;
        var updated = current;

        if (request.Name is not null) updated = updated with { Name = Clean(request.Name) };
        if (request.Host is not null)
        {
            var host = await ValidateHostAsync(request.Host, ct).ConfigureAwait(false);
            var identity = await Gen2Client.IdentifyAsync(_http, host, ct).ConfigureAwait(false);
            if (!string.Equals(identity.DeviceId, current.DeviceId, StringComparison.Ordinal))
                throw new PowerStationRequestException(409,
                    $"{host} is a different device ({identity.DeviceId}). Add it as a new device instead.");
            if (!string.Equals(host, current.Host, StringComparison.OrdinalIgnoreCase))
                updated = updated with { Host = host, AuthRequired = identity.AuthRequired, PreviousHost = null, HostChangedAt = null };
        }
        if (request.ClearPassword == true) updated = updated with { Ha1 = null };
        if (!string.IsNullOrEmpty(request.Password))
        {
            var ha1 = await Gen2Client.DeriveHa1Async(_http, updated.Host, request.Password, ct).ConfigureAwait(false);
            updated = updated with { Ha1 = ha1, AuthRequired = true };
        }
        if (request.ChannelNames is not null)
        {
            var names = new Dictionary<string, string>(updated.ChannelNames, StringComparer.Ordinal);
            foreach (var (key, value) in request.ChannelNames)
            {
                if (!IsChannelKey(key)) throw new PowerStationRequestException(400, $"Unknown channel \"{key}\".");
                if (Clean(value) is { } clean) names[key] = clean;
                else names.Remove(key);
            }
            updated = updated with { ChannelNames = names };
        }

        await _mutate.WaitAsync(ct).ConfigureAwait(false);
        try { await ReplaceAsync(updated, ct).ConfigureAwait(false); }
        finally { _mutate.Release(); }

        var entry = Find(deviceId);
        await PollEntryAsync(entry, ct).ConfigureAwait(false);
        return ToView(entry);
    }

    public async Task RemoveAsync(string deviceId, CancellationToken ct)
    {
        await _mutate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_entries.TryRemove(deviceId, out _))
                throw new PowerStationRequestException(404, "That device isn't in PowerStation.");
            await PersistAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _mutate.Release();
        }
        await Scenes.ForgetDeviceAsync(deviceId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Applies many channel settings at once (scenes). Devices are driven in
    /// parallel; outputs on the same device go one after another. Each device
    /// touched is polled once afterwards so the returned views are current.
    /// A failure on one output never stops the others.
    /// </summary>
    internal async Task<(IReadOnlyList<TargetResult> Results, IReadOnlyList<DeviceView> Devices)> ApplyTargetsAsync(
        IReadOnlyList<SceneTarget> targets, double? fadeSeconds, CancellationToken ct)
    {
        var results = new System.Collections.Concurrent.ConcurrentBag<TargetResult>();
        var touched = new List<Entry>();
        var perDevice = targets.GroupBy(t => t.DeviceId);
        await Task.WhenAll(perDevice.Select(async group =>
        {
            if (!_entries.TryGetValue(group.Key, out var entry))
            {
                foreach (var t in group) results.Add(new TargetResult(t.DeviceId, t.ChannelKey, false, "Device was removed from PowerStation."));
                return;
            }
            lock (touched) touched.Add(entry);
            var name = entry.Record.Name ?? entry.Record.DeviceId;
            foreach (var t in group)
            {
                try
                {
                    if (t.Kind == ChannelKind.Light)
                        await entry.Client.SetLightAsync(t.Index, t.On, t.On ? t.Brightness : null, fadeSeconds, ct).ConfigureAwait(false);
                    else
                        await entry.Client.SetSwitchAsync(t.Index, t.On, ct).ConfigureAwait(false);
                    results.Add(new TargetResult(t.DeviceId, t.ChannelKey, true, null));
                }
                catch (ShellyException ex)
                {
                    results.Add(new TargetResult(t.DeviceId, t.ChannelKey, false, $"{name}: {ex.Message}"));
                    if (ex.Kind is ShellyErrorKind.Unreachable or ShellyErrorKind.Unauthorized)
                    {
                        // The rest of this device will fail the same way; don't wait on each.
                        foreach (var rest in group.SkipWhile(x => x != t).Skip(1))
                            results.Add(new TargetResult(rest.DeviceId, rest.ChannelKey, false, $"{name}: {ex.Message}"));
                        break;
                    }
                }
            }
        })).ConfigureAwait(false);

        await Task.WhenAll(touched.Select(e => PollEntryAsync(e, ct))).ConfigureAwait(false);
        var order = targets.Select((t, i) => (Key: $"{t.DeviceId}/{t.ChannelKey}", i)).ToDictionary(x => x.Key, x => x.i);
        return (
            results.OrderBy(r => order.GetValueOrDefault($"{r.DeviceId}/{r.ChannelKey}")).ToArray(),
            touched.Select(ToView).ToArray());
    }

    /// <summary>
    /// Moves a device to an address where its own ID was just seen, unless it
    /// is still answering at its current address (a Pro on both Wi-Fi and
    /// Ethernet has two addresses; don't flip between them). Returns the old
    /// address when the device was moved, otherwise null.
    /// </summary>
    internal async Task<string?> RelocateIfMovedAsync(string deviceId, string newHost, string how, CancellationToken ct)
    {
        if (!_entries.TryGetValue(deviceId, out var entry)) return null;
        if (string.Equals(entry.Record.Host, newHost, StringComparison.OrdinalIgnoreCase)) return null;
        if (entry.Status.Health == DeviceHealth.Online) return null;

        string oldHost;
        await _mutate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_entries.TryGetValue(deviceId, out entry)) return null;
            oldHost = entry.Record.Host;
            if (string.Equals(oldHost, newHost, StringComparison.OrdinalIgnoreCase)) return null;
            await ReplaceAsync(entry.Record with
            {
                Host = newHost,
                PreviousHost = oldHost,
                HostChangedAt = _time.GetUtcNow(),
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            _mutate.Release();
        }
        _logger.LogInformation("PowerStation: {Device} moved from {Old} to {New} (found by {How})", deviceId, oldHost, newHost, how);
        await PollEntryAsync(entry, ct).ConfigureAwait(false);
        return oldHost;
    }

    public async Task<DeviceView> RefreshAsync(string deviceId, CancellationToken ct)
    {
        var entry = Find(deviceId);
        await PollEntryAsync(entry, ct).ConfigureAwait(false);
        return ToView(entry);
    }

    public async Task<DeviceView> CommandAsync(
        string deviceId, string kindText, int index, ChannelCommand command, CancellationToken ct)
    {
        var entry = Find(deviceId);
        if (!Enum.TryParse<ChannelKind>(kindText, ignoreCase: true, out var kind))
            throw new PowerStationRequestException(400, $"Unknown channel type \"{kindText}\".");
        if (index is < 0 or > 15)
            throw new PowerStationRequestException(400, "Channel number out of range.");
        var known = entry.Status.Channels.FirstOrDefault(c => c.Kind == kind && c.Index == index);
        if (entry.Status.Health == DeviceHealth.Online && known is null)
            throw new PowerStationRequestException(404, $"The device has no {kind.ToString().ToLowerInvariant()} {index}.");

        var client = entry.Client;
        switch (command.Action?.Trim().ToLowerInvariant())
        {
            case "on":
                if (kind == ChannelKind.Light) await client.SetLightAsync(index, true, null, command.TransitionSeconds, ct).ConfigureAwait(false);
                else await client.SetSwitchAsync(index, true, ct).ConfigureAwait(false);
                break;
            case "off":
                if (kind == ChannelKind.Light) await client.SetLightAsync(index, false, null, command.TransitionSeconds, ct).ConfigureAwait(false);
                else await client.SetSwitchAsync(index, false, ct).ConfigureAwait(false);
                break;
            case "toggle":
                await client.ToggleAsync(kind, index, ct).ConfigureAwait(false);
                break;
            case "brightness":
                if (kind != ChannelKind.Light)
                    throw new PowerStationRequestException(400, "Only dimmer channels have brightness.");
                if (command.Brightness is not (>= 0 and <= 100))
                    throw new PowerStationRequestException(400, "Brightness must be between 0 and 100.");
                await client.SetLightAsync(index, command.Brightness > 0 ? true : null, command.Brightness,
                    command.TransitionSeconds, ct).ConfigureAwait(false);
                break;
            case "dim":
                if (kind != ChannelKind.Light)
                    throw new PowerStationRequestException(400, "Only dimmer channels can be dimmed.");
                if (!Enum.TryParse<DimDirection>(command.Direction, ignoreCase: true, out var direction))
                    throw new PowerStationRequestException(400, "Dim direction must be up, down or stop.");
                await client.DimAsync(index, direction, ct).ConfigureAwait(false);
                break;
            default:
                throw new PowerStationRequestException(400, "Action must be on, off, toggle, brightness or dim.");
        }

        await PollEntryAsync(entry, ct).ConfigureAwait(false);
        return ToView(entry);
    }

    // ------------------------------------------------------------ helpers

    private async Task ReplaceAsync(DeviceRecord record, CancellationToken ct)
    {
        if (_entries.TryGetValue(record.DeviceId, out var existing))
        {
            var clientChanged = existing.Record.Host != record.Host || existing.Record.Ha1 != record.Ha1;
            existing.Record = record;
            if (clientChanged)
            {
                existing.Client = CreateClient(record);
                existing.Failures = 0;
                existing.NextPoll = DateTimeOffset.MinValue;
            }
        }
        else
        {
            _entries[record.DeviceId] = new Entry(record, CreateClient(record));
        }
        await PersistAsync(ct).ConfigureAwait(false);
    }

    private Task PersistAsync(CancellationToken ct) =>
        _store.SaveDevicesAsync(
            _entries.Values.Select(e => e.Record).OrderBy(r => r.AddedAt).ToArray(), ct);

    private IShellyClient CreateClient(DeviceRecord record) => new Gen2Client(_http, record.Host, record.Ha1);

    private async Task<string> ValidateHostAsync(string? host, CancellationToken ct)
    {
        if (!HostValidator.TryNormalize(host, out var normalized, out var error))
            throw new PowerStationRequestException(400, error!);
        var problem = await _checkHost(normalized, ct).ConfigureAwait(false);
        if (problem is not null) throw new PowerStationRequestException(400, problem);
        return normalized;
    }

    private static bool IsChannelKey(string key) =>
        (key.StartsWith("switch:", StringComparison.Ordinal) || key.StartsWith("light:", StringComparison.Ordinal)) &&
        int.TryParse(key[(key.IndexOf(':') + 1)..], out var i) && i is >= 0 and <= 15;

    private static string? Clean(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        return trimmed.Length > 64 ? trimmed[..64] : trimmed;
    }
}
