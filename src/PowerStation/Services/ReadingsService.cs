// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json;
using KQ4WLR.PowerStation.Model;
using Zeus.Plugins.Contracts;

namespace KQ4WLR.PowerStation.Services;

/// <summary>Normal and limit bands for the mains supply.</summary>
public sealed record MainsProfile
{
    /// <summary>"120", "230" or "custom".</summary>
    public string Preset { get; init; } = "120";
    public double NormalLowV { get; init; } = 114;
    public double NormalHighV { get; init; } = 126;
    public double LimitLowV { get; init; } = 110;
    public double LimitHighV { get; init; } = 127;

    /// <summary>ANSI C84.1 Range A (normal) and Range B (limit) for 120 V service.</summary>
    public static MainsProfile Us120 => new();

    /// <summary>EN 50160: ±10% limit; ±6% used as the normal band.</summary>
    public static MainsProfile Eu230 => new() { Preset = "230", NormalLowV = 216, NormalHighV = 244, LimitLowV = 207, LimitHighV = 253 };

    public double NominalV => Preset switch { "120" => 120, "230" => 230, _ => (NormalLowV + NormalHighV) / 2 };
}

public sealed record ReadingsSettings
{
    public MainsProfile Mains { get; init; } = MainsProfile.Us120;
    public int HoldSeconds { get; init; } = 5;
}

public sealed record ReadingsRequest(MainsProfile? Mains, int? HoldSeconds);

public sealed record ReadingEvent
{
    public required string Id { get; init; }
    public string DeviceId { get; init; } = "";
    public string ChannelKey { get; init; } = "";
    public required string Label { get; init; }
    /// <summary>voltageHigh, voltageLow, currentHigh, currentLow, or device (an error the device reported).</summary>
    public required string Kind { get; init; }
    public string Level { get; set; } = "warn";
    public string Text { get; set; } = "";
    public double? Peak { get; set; }
    public double? Threshold { get; set; }
    public required DateTimeOffset Start { get; init; }
    public DateTimeOffset? End { get; set; }
}

/// <summary>
/// Compares every metered reading with its normal range, keeps the alerts
/// shown on the cards, and logs each excursion. Mains voltage is one supply,
/// so its excursions are logged once for the station rather than per output.
/// Display and logging only: nothing here switches anything.
/// </summary>
public sealed class ReadingsService
{
    public const string SettingsKey = "readings.v1";
    public const string EventsKey = "reading-events.v1";
    public const int MaxEvents = 200;

    private readonly IPluginSettings _settings;
    private readonly TimeProvider _time;
    private readonly Func<TimeSpan> _staleAfter;
    private readonly object _lock = new();
    private ReadingsSettings _config = new();
    private List<ReadingEvent> _events = [];

    /// <summary>Out-of-range conditions being watched: key → first seen, last seen, worst value, threshold, level.</summary>
    private readonly Dictionary<string, Watch> _watch = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _errorsSeen = new(StringComparer.Ordinal);

    private sealed class Watch
    {
        public DateTimeOffset First;
        public DateTimeOffset Last;
        public double Value;
        public double Worst;
        public double Threshold;
        public string Level = "warn";
        public string Label = "";
        public string DeviceId = "";
        public string ChannelKey = "";
        public string Kind = "";
        public double Factor = 1;
        public ReadingEvent? Event;
    }

    /// <summary>Latest line-to-neutral voltage per channel, to show what the devices measure.</summary>
    private readonly Dictionary<string, (double Volts, DateTimeOffset At)> _latestVolts = new(StringComparer.Ordinal);

    public ReadingsService(IPluginSettings settings, TimeProvider? time = null, Func<TimeSpan>? staleAfter = null)
    {
        _settings = settings;
        _time = time ?? TimeProvider.System;
        _staleAfter = staleAfter ?? (() => TimeSpan.FromSeconds(60));
    }

    public ReadingsSettings Config => _config;

    public async Task LoadAsync(CancellationToken ct)
    {
        var json = await _settings.GetAsync<string>(SettingsKey, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(json))
            _config = JsonSerializer.Deserialize<ReadingsSettings>(json, Json.Options) ?? new ReadingsSettings();
        var events = await _settings.GetAsync<string>(EventsKey, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(events))
        {
            _events = JsonSerializer.Deserialize<List<ReadingEvent>>(events, Json.Options) ?? [];
            // Anything still open was interrupted by a restart; close it where it was last known.
            foreach (var e in _events.Where(e => e.End is null)) e.End = e.Start;
        }
    }

    // ------------------------------------------------------------ ratings

    /// <summary>
    /// Per-output current rating from the manufacturer's spec, where known.
    /// Unknown models get no default; the operator can set their own.
    /// </summary>
    public static double? RatedAmps(string? app, ChannelKind kind) => (app, kind) switch
    {
        ("ShellyEM", ChannelKind.Meter) => 50, // the standard 50 A clamp; 120 A clamps exist, so it's editable
        (_, ChannelKind.Meter) => null,
        ("Pro4PM", _) => 16,
        ("Pro1PM", _) => 16,
        ("Pro2PM", _) => 16,
        ("PlugUS", _) => 15,
        ("Ogemray25A", _) => 25,
        ("Shelly1PM", _) => 16,
        ("Shelly2.5", _) => 10,
        _ => null,
    };

    /// <summary>Devices powered from a low-voltage DC supply.</summary>
    public static bool IsDcDevice(string? app) =>
        app is not null && app.Contains("RGBW", StringComparison.OrdinalIgnoreCase);

    internal CurrentLimits? LimitsFor(DeviceRecord record, ChannelState ch)
    {
        if (!ch.Metered) return null;
        var rated = RatedAmps(record.App, ch.Kind);
        record.Limits.TryGetValue(ch.Key, out var custom);
        return new CurrentLimits
        {
            RatedA = rated,
            WarnA = custom?.WarnA ?? (rated is { } r ? Math.Round(r * 0.8, 1) : null),
            MaxA = custom?.MaxA ?? rated,
            MinOnA = custom?.MinOnA,
            Custom = custom is not null,
        };
    }

    // ------------------------------------------------------------ observe

    /// <summary>Called after each successful poll of a device.</summary>
    public void Observe(DeviceRecord record, IReadOnlyList<ChannelState> channels)
    {
        var now = _time.GetUtcNow();
        var changed = false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        lock (_lock)
        {
            var mains = _config.Mains;
            // A device wired across two legs (240 V on a US split-phase panel) sees twice the mains voltage.
            double f = record.LineToLine ? 2 : 1;
            foreach (var ch in channels)
            {
                changed |= NoteDeviceErrors(record, ch, now);
                if (!ch.Metered) continue;
                var label = ch.Name ?? $"{(ch.Kind == ChannelKind.Meter ? "Meter" : "Output")} {ch.Index + 1}";

                // Colour LED controllers (and the RGBW PM in light mode) report their DC supply, not mains.
                var dc = ch.Kind.IsColor() || IsDcDevice(record.App);
                if (ch.VoltageV is { } v and > 1 && !dc)
                {
                    _latestVolts[$"{record.DeviceId}|{ch.Key}"] = (v / f, now);
                    if (v < mains.LimitLowV * f) See($"{record.DeviceId}|{ch.Key}|voltageLow", "voltageLow", "limit", v, mains.LimitLowV * f);
                    else if (v < mains.NormalLowV * f) See($"{record.DeviceId}|{ch.Key}|voltageLow", "voltageLow", "warn", v, mains.NormalLowV * f);
                    if (v > mains.LimitHighV * f) See($"{record.DeviceId}|{ch.Key}|voltageHigh", "voltageHigh", "limit", v, mains.LimitHighV * f);
                    else if (v > mains.NormalHighV * f) See($"{record.DeviceId}|{ch.Key}|voltageHigh", "voltageHigh", "warn", v, mains.NormalHighV * f);
                }

                var limits = LimitsFor(record, ch);
                // Plugs without a voltage reading: estimate current from power at the nominal voltage.
                var amps = ch.CurrentA ?? (ch.PowerW is { } p && !dc ? Math.Round(p / (mains.NominalV * f), 2) : null);
                if (limits is not null && amps is { } a)
                {
                    if (limits.MaxA is { } max && a > max) See($"{record.DeviceId}|{ch.Key}|currentHigh", "currentHigh", "limit", a, max);
                    else if (limits.WarnA is { } warn && a > warn) See($"{record.DeviceId}|{ch.Key}|currentHigh", "currentHigh", "warn", a, warn);
                    if (limits.MinOnA is { } min && ch.On && ch.Kind != ChannelKind.Meter && a < min)
                        See($"{record.DeviceId}|{ch.Key}|currentLow", "currentLow", "warn", a, min);
                }

                void See(string key, string kind, string level, double value, double threshold)
                {
                    if (!_watch.TryGetValue(key, out var w))
                        _watch[key] = w = new Watch { First = now, Worst = value, DeviceId = record.DeviceId, ChannelKey = ch.Key, Kind = kind, Label = label };
                    w.Last = now;
                    w.Factor = f;
                    seen.Add(key);
                    w.Value = value;
                    w.Threshold = threshold;
                    w.Level = level;
                    w.Worst = kind.EndsWith("Low", StringComparison.Ordinal) ? Math.Min(w.Worst, value) : Math.Max(w.Worst, value);
                }
            }

            // Anything this device was watching that it didn't report this time is back in range.
            var prefix = record.DeviceId + "|";
            foreach (var key in _watch.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToArray())
                if (!seen.Contains(key)) changed |= Close(key, now);

            changed |= UpdateEvents(now);
        }
        if (changed) _ = SaveEventsAsync();
    }

    private bool NoteDeviceErrors(DeviceRecord record, ChannelState ch, DateTimeOffset now)
    {
        var key = $"{record.DeviceId}|{ch.Key}";
        var current = ch.Errors.ToHashSet(StringComparer.Ordinal);
        var had = _errorsSeen.TryGetValue(key, out var set) ? set : [];
        _errorsSeen[key] = current;
        var added = current.Except(had).ToArray();
        foreach (var error in added)
            AddEvent(new ReadingEvent
            {
                Id = $"{key}|{error}|{now.ToUnixTimeMilliseconds()}",
                DeviceId = record.DeviceId,
                ChannelKey = ch.Key,
                Label = ch.Name ?? record.Name ?? record.DeviceId,
                Kind = "device",
                Level = "limit",
                Text = $"device reported {error.Replace('_', ' ')}",
                Start = now,
                End = now,
            });
        return added.Length > 0;
    }

    /// <summary>
    /// Opens events for conditions that have lasted the hold time, keeps
    /// their worst values current, and closes events for devices that have
    /// gone quiet (offline devices can't say they're back in range).
    /// </summary>
    private bool UpdateEvents(DateTimeOffset now)
    {
        var changed = false;
        var hold = TimeSpan.FromSeconds(_config.HoldSeconds);
        foreach (var key in _watch.Keys.ToArray())
        {
            var w = _watch[key];
            if (now - w.Last > _staleAfter()) { changed |= Close(key, now); continue; }
            if (now - w.First < hold) continue;
            var volt = w.Kind.StartsWith("voltage", StringComparison.Ordinal);
            if (volt)
            {
                // One mains event per kind, shared by every output that sees it.
                var ll = w.Factor > 1;
                var mainsKey = $"{(ll ? "mains-ll" : "mains")}|{w.Kind}";
                var open = _events.FirstOrDefault(e => e.End is null && e.Id.StartsWith(mainsKey + "|", StringComparison.Ordinal));
                if (open is null)
                {
                    open = new ReadingEvent { Id = $"{mainsKey}|{w.First.ToUnixTimeMilliseconds()}", Label = ll ? "Mains (line to line)" : "Mains", Kind = w.Kind, Start = w.First, Peak = w.Worst };
                    AddEvent(open);
                    changed = true;
                }
                w.Event = open;
            }
            else if (w.Event is null)
            {
                w.Event = new ReadingEvent { Id = $"{key}|{w.First.ToUnixTimeMilliseconds()}", DeviceId = w.DeviceId, ChannelKey = w.ChannelKey, Label = w.Label, Kind = w.Kind, Start = w.First, Peak = w.Worst };
                AddEvent(w.Event);
                changed = true;
            }
            var e = w.Event;
            var low = w.Kind.EndsWith("Low", StringComparison.Ordinal);
            e.Peak = e.Peak is { } p ? (low ? Math.Min(p, w.Worst) : Math.Max(p, w.Worst)) : w.Worst;
            if (w.Level == "limit") e.Level = "limit";
            e.Threshold = w.Threshold;
            e.Text = Describe(w.Kind, e.Level, e.Peak.Value, w.Factor);
        }
        return changed;
    }

    private string Describe(string kind, string level, double peak, double f = 1)
    {
        var m = _config.Mains;
        return kind switch
        {
            "voltageLow" => $"low voltage, lowest {peak:0.0} V ({(level == "limit" ? $"below the {m.LimitLowV * f:0.#} V limit" : $"normal from {m.NormalLowV * f:0.#} V")})",
            "voltageHigh" => $"high voltage, highest {peak:0.0} V ({(level == "limit" ? $"above the {m.LimitHighV * f:0.#} V limit" : $"normal to {m.NormalHighV * f:0.#} V")})",
            "currentHigh" => $"high current, peak {peak:0.00} A",
            _ => $"low current while on, lowest {peak:0.00} A",
        };
    }

    private bool Close(string key, DateTimeOffset now)
    {
        if (!_watch.Remove(key, out var w) || w.Event is null) return false;
        if (w.Kind.StartsWith("voltage", StringComparison.Ordinal))
        {
            // The mains event stays open while any other output still sees it.
            if (_watch.Values.Any(o => o.Event == w.Event)) return false;
        }
        w.Event.End ??= now;
        return true;
    }

    private void AddEvent(ReadingEvent e)
    {
        _events.Insert(0, e);
        if (_events.Count > MaxEvents) _events.RemoveRange(MaxEvents, _events.Count - MaxEvents);
    }

    private async Task SaveEventsAsync()
    {
        string json;
        lock (_lock) json = JsonSerializer.Serialize(_events, Json.Options);
        try { await _settings.SetAsync(EventsKey, json).ConfigureAwait(false); }
        catch (Exception) { /* best effort; the log is informational */ }
    }

    // ------------------------------------------------------------ views

    /// <summary>Adds limits and live alerts to a device's channels for the API.</summary>
    public IReadOnlyList<ChannelState> Decorate(DeviceRecord record, IReadOnlyList<ChannelState> channels)
    {
        var now = _time.GetUtcNow();
        var hold = TimeSpan.FromSeconds(_config.HoldSeconds);
        lock (_lock)
        {
            return channels.Select(ch =>
            {
                if (!ch.Metered) return ch;
                var prefix = $"{record.DeviceId}|{ch.Key}|";
                var alerts = _watch
                    .Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal) && now - kv.Value.First >= hold && now - kv.Value.Last <= _staleAfter())
                    .Select(kv => new ReadingAlert { Kind = kv.Value.Kind, Level = kv.Value.Level, Value = kv.Value.Value, Threshold = kv.Value.Threshold, Since = kv.Value.First })
                    .ToArray();
                return ch with { Limits = LimitsFor(record, ch), Alerts = alerts };
            }).ToArray();
        }
    }

    public object View()
    {
        var now = _time.GetUtcNow();
        var hold = TimeSpan.FromSeconds(_config.HoldSeconds);
        lock (_lock)
        {
            UpdateEvents(now);
            var volts = _watch.Values
                .Where(w => w.Kind.StartsWith("voltage", StringComparison.Ordinal) && now - w.First >= hold && now - w.Last <= _staleAfter())
                .ToArray();
            var worst = volts.OrderByDescending(w => w.Level == "limit").ThenByDescending(w => Math.Abs(w.Value - w.Threshold)).FirstOrDefault();
            var fresh = _latestVolts.Values.Where(x => now - x.At <= _staleAfter()).Select(x => x.Volts).ToArray();
            return new
            {
                mains = _config.Mains,
                holdSeconds = _config.HoldSeconds,
                // What the devices measure now (line to neutral), to help pick the right mains setting.
                measured = fresh.Length == 0 ? null : new { minV = Math.Round(fresh.Min(), 1), maxV = Math.Round(fresh.Max(), 1) },
                mainsNow = worst is null ? null : new
                {
                    voltageV = worst.Value,
                    alert = new ReadingAlert { Kind = worst.Kind, Level = worst.Level, Value = worst.Value, Threshold = worst.Threshold, Since = volts.Min(w => w.First) },
                    outputs = volts.Select(w => $"{w.DeviceId}|{w.ChannelKey}").Distinct().Count(),
                },
                events = _events.Take(100).ToArray(),
            };
        }
    }

    public async Task<object> UpdateAsync(ReadingsRequest request, CancellationToken ct)
    {
        var next = _config;
        if (request.Mains is { } m)
        {
            if (m.Preset is not ("120" or "230" or "custom"))
                throw new PowerStationRequestException(400, "Mains preset must be 120, 230 or custom.");
            if (!(m.LimitLowV > 0 && m.LimitLowV <= m.NormalLowV && m.NormalLowV < m.NormalHighV && m.NormalHighV <= m.LimitHighV && m.LimitHighV < 500))
                throw new PowerStationRequestException(400, "Voltages must go limit low ≤ normal from < normal to ≤ limit high.");
            next = next with { Mains = m };
        }
        if (request.HoldSeconds is { } h)
        {
            if (h is < 0 or > 120) throw new PowerStationRequestException(400, "Must last has to be between 0 and 120 seconds.");
            next = next with { HoldSeconds = h };
        }
        await _settings.SetAsync(SettingsKey, JsonSerializer.Serialize(next, Json.Options), ct).ConfigureAwait(false);
        lock (_lock)
        {
            _config = next;
            // Re-judge from scratch against the new range.
            foreach (var w in _watch.Values) if (w.Event is not null) w.Event.End ??= _time.GetUtcNow();
            _watch.Clear();
        }
        return View();
    }

    public async Task<object> ClearAsync(CancellationToken ct)
    {
        lock (_lock) _events.RemoveAll(e => e.End is not null);
        await SaveEventsAsync().ConfigureAwait(false);
        return View();
    }
}
