// SPDX-License-Identifier: GPL-2.0-or-later
using System.Collections.Concurrent;
using KQ4WLR.PowerStation.Model;
using KQ4WLR.PowerStation.Shelly;
using Microsoft.Extensions.Logging;

namespace KQ4WLR.PowerStation.Services;

public sealed record AddDeviceRequest(string? Host, string? Name, string? Password, string? Username = null);

public sealed record UpdateDeviceRequest(
    string? Name,
    string? Host,
    string? Password,
    bool? ClearPassword,
    Dictionary<string, string?>? ChannelNames,
    Dictionary<string, int?>? SafetyMinutes = null,
    Dictionary<string, LimitOverride?>? Limits = null,
    string? Username = null,
    bool? LineToLine = null);

public sealed record ChannelCommand(
    string? Action,
    double? Brightness,
    double? TransitionSeconds,
    string? Direction,
    int[]? Rgb = null,
    double? White = null);

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
    public bool LineToLine { get; init; }
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
        Traffic = new TrafficLog(_time);
    }

    public PowerStationOptions Options => _options;

    /// <summary>Traffic with the devices, for the Debug section.</summary>
    public TrafficLog Traffic { get; }

    internal Task SaveDebugEnabledAsync(bool enabled, CancellationToken ct) => _store.SaveDebugEnabledAsync(enabled, ct);

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

    /// <summary>Raised after each successful poll with the device's record and fresh channels.</summary>
    public event Action<DeviceRecord, IReadOnlyList<ChannelState>>? Polled;

    /// <summary>Adds computed fields (limits, alerts) to channels as the API returns them.</summary>
    public Func<DeviceRecord, IReadOnlyList<ChannelState>, IReadOnlyList<ChannelState>>? Decorate { get; set; }

    /// <summary>Raised when the operator changes an output from PowerStation (counts as activity).</summary>
    public event Action? OperatorAction;

    internal void NoteOperatorAction()
    {
        try { OperatorAction?.Invoke(); }
        catch (Exception ex) { _logger.LogError(ex, "PowerStation: activity hook failed"); }
    }

    /// <summary>
    /// Safety timers set by a scene for the outputs it turned on, until they
    /// next go off. Keyed by "deviceId/channelKey". Not persisted: after a
    /// restart the device-side timers simply run out.
    /// </summary>
    private readonly ConcurrentDictionary<string, int> _sceneSafety = new(StringComparer.Ordinal);

    /// <summary>Renew the device-side timer once this fraction of it has passed.</summary>
    private const double RenewFraction = 1.0 / 3.0;

    private sealed class Entry(DeviceRecord record, IShellyClient client)
    {
        public DeviceRecord Record { get; set; } = record;
        public IShellyClient Client { get; set; } = client;
        public DeviceStatus Status { get; set; } = new();
        public DateTimeOffset NextPoll { get; set; } = DateTimeOffset.MinValue;
        public int Failures { get; set; }
        public SemaphoreSlim PollGate { get; } = new(1, 1);
        /// <summary>When each channel's safety timer was last set on the device.</summary>
        public Dictionary<string, DateTimeOffset> SafetyRenewedAt { get; } = new(StringComparer.Ordinal);
    }

    // ------------------------------------------------------------ lifecycle

    public async Task LoadAsync(CancellationToken ct)
    {
        _options = await _store.LoadOptionsAsync(ct).ConfigureAwait(false);
        Traffic.Enabled = await _store.LoadDebugEnabledAsync(ct).ConfigureAwait(false);
        foreach (var record in await _store.LoadDevicesAsync(ct).ConfigureAwait(false))
            _entries[record.DeviceId] = new Entry(record, CreateClient(record));
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
                var named = ApplyNames(entry.Record, channels);
                entry.Status = new DeviceStatus
                {
                    Health = DeviceHealth.Online,
                    LastSeen = now,
                    LastPolled = now,
                    Channels = named,
                };
                await RenewSafetyTimersAsync(entry, named, ct).ConfigureAwait(false);
                try { Polled?.Invoke(entry.Record, named); }
                catch (Exception hookError) { _logger.LogError(hookError, "PowerStation: readings hook failed"); }
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
            // Something PowerStation didn't expect (a status it can't read, say). Show it on the
            // card instead of leaving the device stuck on "Connecting…", and log the detail.
            _logger.LogError(ex, "PowerStation: unexpected error polling {Device}", entry.Record.DeviceId);
            Traffic.Event(entry.Record.DeviceId, "Reading status", $"{ex.GetType().Name}: {ex.Message}");
            entry.Failures++;
            entry.Status = entry.Status with
            {
                Health = DeviceHealth.Error,
                Message = $"PowerStation couldn't read this device's status ({ex.GetType().Name}: {ex.Message}). " +
                          "Setup › Debug shows the device's reply.",
                LastPolled = _time.GetUtcNow(),
            };
            entry.NextPoll = _time.GetUtcNow() + NextDelay(entry.Failures);
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

    private IReadOnlyList<ChannelState> ApplyNames(DeviceRecord record, IReadOnlyList<ChannelState> channels) =>
        channels.Select(c =>
        {
            var named = record.ChannelNames.TryGetValue(c.Key, out var name) ? c with { Name = name } : c;
            var safety = EffectiveSafetyMinutes(record, c.Key);
            return safety is null ? named : named with { SafetyMinutes = safety };
        }).ToArray();

    // ------------------------------------------------------------ safety timers

    private int? EffectiveSafetyMinutes(DeviceRecord record, string channelKey)
    {
        int? own = record.SafetyMinutes.TryGetValue(channelKey, out var m) && m > 0 ? m : null;
        int? scene = _sceneSafety.TryGetValue($"{record.DeviceId}/{channelKey}", out var s) ? s : null;
        return own is null ? scene : scene is null ? own : Math.Max(own.Value, scene.Value);
    }

    /// <summary>
    /// Keeps each protected output's device-side timer running while
    /// PowerStation is alive. Renewing means "stay on, and switch off in N
    /// minutes unless told again", so it's only ever sent for an output this
    /// very poll saw on; if Zeus stops, the device switches the output off.
    /// </summary>
    private async Task RenewSafetyTimersAsync(Entry entry, IReadOnlyList<ChannelState> channels, CancellationToken ct)
    {
        foreach (var ch in channels)
        {
            if (ch.Kind == ChannelKind.Meter) continue;
            var key = ch.Key;
            if (!ch.On || ch.SafetyMinutes is not { } minutes)
            {
                entry.SafetyRenewedAt.Remove(key);
                if (!ch.On) _sceneSafety.TryRemove($"{entry.Record.DeviceId}/{key}", out _);
                continue;
            }
            var now = _time.GetUtcNow();
            var period = TimeSpan.FromMinutes(minutes);
            if (entry.SafetyRenewedAt.TryGetValue(key, out var last) && now - last < period * RenewFraction) continue;
            try
            {
                await entry.Client.SetSafetyTimerAsync(ch.Kind, ch.Index, minutes * 60, ct).ConfigureAwait(false);
                entry.SafetyRenewedAt[key] = now;
            }
            catch (ShellyException ex)
            {
                _logger.LogWarning("PowerStation: couldn't renew the safety timer on {Device} {Channel}: {Message}",
                    entry.Record.DeviceId, key, ex.Message);
            }
        }
    }

    /// <summary>Called when a scene turns outputs on with its own safety timer.</summary>
    internal void SetSceneSafety(IEnumerable<SceneTarget> targetsTurnedOn, int minutes)
    {
        foreach (var t in targetsTurnedOn) _sceneSafety[$"{t.DeviceId}/{t.ChannelKey}"] = minutes;
    }

    // ------------------------------------------------------------ queries

    public IReadOnlyList<DeviceView> List() =>
        _entries.Values
            .Select(ToView)
            .OrderBy(v => v.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public DeviceView Get(string deviceId) => ToView(Find(deviceId));

    private DeviceView ToView(Entry e) => new()
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
        HasCredential = e.Record.Ha1 is not null || e.Record.Gen1Password is not null,
        PreviousHost = e.Record.PreviousHost,
        HostChangedAt = e.Record.HostChangedAt,
        LineToLine = e.Record.LineToLine,
        Status = DecorateSafely(e),
    };

    private DeviceStatus DecorateSafely(Entry e)
    {
        if (Decorate is not { } decorate) return e.Status;
        try { return e.Status with { Channels = decorate(e.Record, e.Status.Channels) }; }
        catch (Exception ex)
        {
            // Readings are extras; never let them hide the device.
            _logger.LogError(ex, "PowerStation: couldn't add readings for {Device}", e.Record.DeviceId);
            Traffic.Event(e.Record.DeviceId, "Checking readings", $"{ex.GetType().Name}: {ex.Message}");
            return e.Status;
        }
    }

    private Entry Find(string deviceId) =>
        _entries.TryGetValue(deviceId, out var e)
            ? e
            : throw new PowerStationRequestException(404, "That device isn't in PowerStation.");

    // ------------------------------------------------------------ mutations

    /// <summary>Checks an address and reports what's there, without saving.</summary>
    public async Task<DeviceIdentity> ProbeAsync(string? host, CancellationToken ct)
    {
        var normalized = await ValidateHostAsync(host, ct).ConfigureAwait(false);
        return await Gen2Client.IdentifyAsync(_http, normalized, ct, Traffic).ConfigureAwait(false);
    }

    public async Task<DeviceView> AddAsync(AddDeviceRequest request, CancellationToken ct)
    {
        var host = await ValidateHostAsync(request.Host, ct).ConfigureAwait(false);
        var identity = await Gen2Client.IdentifyAsync(_http, host, ct, Traffic).ConfigureAwait(false);

        string? ha1 = null, gen1User = null, gen1Password = null;
        if (identity.AuthRequired)
        {
            if (string.IsNullOrEmpty(request.Password))
                throw new PowerStationRequestException(400,
                    "This device has a password set. Enter it to add the device.");
            if (identity.Generation == 1)
            {
                gen1User = CleanUser(request.Username);
                await Gen1Client.VerifyAsync(_http, host, gen1User, request.Password, ct, Traffic).ConfigureAwait(false);
                gen1Password = request.Password;
            }
            else
            {
                ha1 = await Gen2Client.DeriveHa1Async(_http, host, request.Password, ct, Traffic).ConfigureAwait(false);
            }
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
                    Gen1User = gen1Password is not null ? gen1User : identity.AuthRequired ? existing.Record.Gen1User : null,
                    Gen1Password = gen1Password ?? (identity.AuthRequired ? existing.Record.Gen1Password : null),
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
                    Gen1User = gen1User,
                    Gen1Password = gen1Password,
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
            var identity = await Gen2Client.IdentifyAsync(_http, host, ct, Traffic).ConfigureAwait(false);
            if (!string.Equals(identity.DeviceId, current.DeviceId, StringComparison.Ordinal))
                throw new PowerStationRequestException(409,
                    $"{host} is a different device ({identity.DeviceId}). Add it as a new device instead.");
            if (!string.Equals(host, current.Host, StringComparison.OrdinalIgnoreCase))
                updated = updated with { Host = host, AuthRequired = identity.AuthRequired, PreviousHost = null, HostChangedAt = null };
        }
        if (request.ClearPassword == true) updated = updated with { Ha1 = null, Gen1User = null, Gen1Password = null };
        if (!string.IsNullOrEmpty(request.Password))
        {
            if (updated.Generation == 1)
            {
                var user = CleanUser(request.Username ?? updated.Gen1User);
                await Gen1Client.VerifyAsync(_http, updated.Host, user, request.Password, ct, Traffic).ConfigureAwait(false);
                updated = updated with { Gen1User = user, Gen1Password = request.Password, AuthRequired = true };
            }
            else
            {
                var ha1 = await Gen2Client.DeriveHa1Async(_http, updated.Host, request.Password, ct, Traffic).ConfigureAwait(false);
                updated = updated with { Ha1 = ha1, AuthRequired = true };
            }
        }
        if (request.SafetyMinutes is not null)
        {
            var safety = new Dictionary<string, int>(updated.SafetyMinutes, StringComparer.Ordinal);
            foreach (var (key, minutes) in request.SafetyMinutes)
            {
                if (!IsChannelKey(key) || key.StartsWith("emeter:", StringComparison.Ordinal))
                    throw new PowerStationRequestException(400, $"Unknown output \"{key}\".");
                if (minutes is null or 0) safety.Remove(key);
                else if (minutes is < 1 or > 1440)
                    throw new PowerStationRequestException(400, "Safety timers must be between 1 and 1,440 minutes.");
                else safety[key] = minutes.Value;
            }
            updated = updated with { SafetyMinutes = safety };
        }
        if (request.LineToLine is { } ll) updated = updated with { LineToLine = ll };
        if (request.Limits is not null)
        {
            var limits = new Dictionary<string, LimitOverride>(updated.Limits, StringComparer.Ordinal);
            foreach (var (key, value) in request.Limits)
            {
                if (!IsChannelKey(key)) throw new PowerStationRequestException(400, $"Unknown channel \"{key}\".");
                // The operator's row replaces any earlier one; an empty row goes back to the rating.
                if (value is null || value is { WarnA: null, MaxA: null, MinOnA: null }) { limits.Remove(key); continue; }
                var merged = value;
                if (merged.WarnA is < 0 or > 500 || merged.MaxA is < 0 or > 500 || merged.MinOnA is < 0 or > 500)
                    throw new PowerStationRequestException(400, "Current limits must be between 0 and 500 A.");
                if (merged.WarnA is { } w && merged.MaxA is { } mx && w > mx)
                    throw new PowerStationRequestException(400, "The warning level can't be above the limit.");
                limits[key] = merged;
            }
            updated = updated with { Limits = limits };
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
                    if (t.Kind.IsColor())
                        await entry.Client.SetColorAsync(t.Kind, t.Index, t.On, t.On ? t.Brightness : null,
                            t.On ? t.Rgb : null, t.On ? t.White : null, fadeSeconds, ct).ConfigureAwait(false);
                    else if (t.Kind == ChannelKind.Light)
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
        if (!Enum.TryParse<ChannelKind>(kindText, ignoreCase: true, out var kind) || kind == ChannelKind.Meter)
            throw new PowerStationRequestException(400, $"Unknown channel type \"{kindText}\".");
        if (index is < 0 or > 15)
            throw new PowerStationRequestException(400, "Channel number out of range.");
        var known = entry.Status.Channels.FirstOrDefault(c => c.Kind == kind && c.Index == index);
        if (entry.Status.Health == DeviceHealth.Online && known is null)
            throw new PowerStationRequestException(404, $"The device has no {kind.ToString().ToLowerInvariant()} {index}.");

        var client = entry.Client;
        switch (command.Action?.Trim().ToLowerInvariant())
        {
            case "on" or "off" when kind.IsColor():
                await client.SetColorAsync(kind, index, command.Action.Trim().Equals("on", StringComparison.OrdinalIgnoreCase),
                    null, null, null, command.TransitionSeconds, ct).ConfigureAwait(false);
                break;
            case "brightness" when kind.IsColor():
                if (command.Brightness is not (>= 1 and <= 100))
                    throw new PowerStationRequestException(400, "Level must be between 1 and 100.");
                await client.SetColorAsync(kind, index, true, command.Brightness, null, null, command.TransitionSeconds, ct).ConfigureAwait(false);
                break;
            case "color":
            {
                if (!kind.IsColor()) throw new PowerStationRequestException(400, "Only colour lights have a colour.");
                if (command.Rgb is null) throw new PowerStationRequestException(400, "Give a colour.");
                var (rgb, white) = SceneManager.ValidateColor(kind, true, command.Rgb, command.White);
                await client.SetColorAsync(kind, index, true, command.Brightness, rgb, white, command.TransitionSeconds, ct).ConfigureAwait(false);
                break;
            }
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
                throw new PowerStationRequestException(400, "Action must be on, off, toggle, brightness, dim or color.");
        }

        NoteOperatorAction();
        await PollEntryAsync(entry, ct).ConfigureAwait(false);
        return ToView(entry);
    }

    /// <summary>Current state of one output, if the device has been polled.</summary>
    internal ChannelState? GetChannel(string deviceId, string channelKey) =>
        _entries.TryGetValue(deviceId, out var e) ? e.Status.Channels.FirstOrDefault(c => c.Key == channelKey) : null;

    internal bool HasChannel(string deviceId, ChannelKind kind, int index) =>
        _entries.TryGetValue(deviceId, out var e) &&
        (e.Status.Channels.Count == 0 || e.Status.Channels.Any(c => c.Kind == kind && c.Index == index));

    internal string DisplayName(string deviceId) =>
        _entries.TryGetValue(deviceId, out var e) ? e.Record.Name ?? e.Record.DeviceId : deviceId;

    // ------------------------------------------------------------ helpers

    private async Task ReplaceAsync(DeviceRecord record, CancellationToken ct)
    {
        if (_entries.TryGetValue(record.DeviceId, out var existing))
        {
            var clientChanged = existing.Record.Host != record.Host || existing.Record.Ha1 != record.Ha1 ||
                                existing.Record.Gen1User != record.Gen1User || existing.Record.Gen1Password != record.Gen1Password;
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

    private IShellyClient CreateClient(DeviceRecord record) =>
        record.Generation == 1
            ? new Gen1Client(_http, record.Host, record.Gen1User, record.Gen1Password, Traffic, record.DeviceId)
            : new Gen2Client(_http, record.Host, record.Ha1, Traffic, record.DeviceId);

    private static string CleanUser(string? user)
    {
        var u = user?.Trim();
        if (string.IsNullOrEmpty(u)) return "admin";
        if (u.Length > 64 || u.Contains(':')) throw new PowerStationRequestException(400, "That user name isn't valid.");
        return u;
    }

    private async Task<string> ValidateHostAsync(string? host, CancellationToken ct)
    {
        if (!HostValidator.TryNormalize(host, out var normalized, out var error))
            throw new PowerStationRequestException(400, error!);
        var problem = await _checkHost(normalized, ct).ConfigureAwait(false);
        if (problem is not null) throw new PowerStationRequestException(400, problem);
        return normalized;
    }

    private static bool IsChannelKey(string key) =>
        (key.StartsWith("switch:", StringComparison.Ordinal) || key.StartsWith("light:", StringComparison.Ordinal) ||
         key.StartsWith("emeter:", StringComparison.Ordinal) || key.StartsWith("rgb:", StringComparison.Ordinal) ||
         key.StartsWith("rgbw:", StringComparison.Ordinal)) &&
        int.TryParse(key[(key.IndexOf(':') + 1)..], out var i) && i is >= 0 and <= 15;

    private static string? Clean(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        return trimmed.Length > 64 ? trimmed[..64] : trimmed;
    }
}
