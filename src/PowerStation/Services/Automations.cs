// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using KQ4WLR.PowerStation.Model;
using Microsoft.Extensions.Logging;
using Zeus.Plugins.Contracts;

namespace KQ4WLR.PowerStation.Services;

/// <summary>When a rule acts. Fields beyond <see cref="Type"/> depend on the type.</summary>
public sealed record RuleTrigger
{
    /// <summary>zeusStart, zeusStop, tx, band, frequency, idle or time.</summary>
    public required string Type { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<string>? Bands { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? FromMHz { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? ToMHz { get; init; }
    /// <summary>Idle: minutes without activity.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? Minutes { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? WarnMinutes { get; init; }
    /// <summary>Idle: what the extend button adds. Time: how long to wait before checking again.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? ExtendMinutes { get; init; }
    /// <summary>Time: local time of day, HH:mm.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? At { get; init; }
    /// <summary>Time: act only if the station has been idle this long.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? IdleMinutes { get; init; }
}

/// <summary>What a rule does. End actions may also be restore, off or none.</summary>
public sealed record RuleAction
{
    /// <summary>scene or output; for end actions also restore, off or none.</summary>
    public required string Type { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? SceneId { get; init; }
    /// <summary>Scene: apply or off.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Mode { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? DeviceId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public ChannelKind? Kind { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? Index { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? On { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? Brightness { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? RampSeconds { get; init; }
    /// <summary>Colour lights: [r, g, b] 0-255.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int[]? Rgb { get; init; }
    /// <summary>RGBW: white 0-255.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? White { get; init; }
}

public sealed record Rule
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public bool Enabled { get; init; } = true;
    public required RuleTrigger Trigger { get; init; }
    public required RuleAction Action { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public RuleAction? EndAction { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? DebounceSeconds { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? DelaySeconds { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? EndDelaySeconds { get; init; }
}

public sealed record RuleRequest(
    string? Name, bool? Enabled, RuleTrigger? Trigger, RuleAction? Action, RuleAction? EndAction,
    double? DebounceSeconds, double? DelaySeconds, double? EndDelaySeconds);

public sealed record RuleRun(DateTimeOffset At, bool Ok, string Text);

public sealed record AutomationRequest(bool? Paused);

/// <summary>
/// Runs the operator's rules. Watches Zeus's read-only radio state (tuning,
/// mode, TX), PowerStation activity and the clock. Never touches the radio.
/// Everything except the on-air light waits while the radio is transmitting.
/// </summary>
public sealed class AutomationService : IAsyncDisposable
{
    public const string RulesKey = "rules.v1";
    public const string SettingsKey = "automation.v1";
    public const int MaxRules = 100;

    public static readonly IReadOnlyDictionary<string, (double From, double To)> BandEdges = new Dictionary<string, (double, double)>
    {
        ["160m"] = (1.8, 2.0), ["80m"] = (3.5, 4.0), ["60m"] = (5.25, 5.45), ["40m"] = (7.0, 7.3), ["30m"] = (10.1, 10.15),
        ["20m"] = (14.0, 14.35), ["17m"] = (18.068, 18.168), ["15m"] = (21.0, 21.45), ["12m"] = (24.89, 24.99),
        ["10m"] = (28.0, 29.7), ["6m"] = (50.0, 54.0), ["4m"] = (70.0, 71.0), ["2m"] = (144.0, 148.0),
    };

    private static readonly string[] TriggerTypes = ["zeusStart", "zeusStop", "tx", "band", "frequency", "idle", "time"];
    private static readonly string[] Lasting = ["tx", "band", "frequency"];

    private readonly IPluginSettings _settings;
    private readonly DeviceManager _devices;
    private readonly IRadioStateReader? _radio;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly object _lock = new();
    private readonly SemaphoreSlim _mutate = new(1, 1);
    private readonly SemaphoreSlim _run = new(1, 1);
    private List<Rule> _rules = [];
    private bool _paused;

    private long _frequencyHz;
    private string? _mode;
    private bool _mox;
    private DateTimeOffset _lastActivity;
    private TimeSpan _idleExtension;

    private readonly Dictionary<string, RuleState> _state = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RuleRun> _lastRun = new(StringComparer.Ordinal);
    private readonly List<(string RuleId, string Text, Func<Task> Run)> _txQueue = [];
    private readonly List<(DateTimeOffset At, string Text, bool Ok)> _log = [];
    private readonly List<Task> _inFlight = [];

    private CancellationTokenSource? _loopCts;
    private Task? _loop;

    private sealed class RuleState
    {
        public bool Active;
        public DateTimeOffset? MatchSince;
        public DateTimeOffset? EndSince;
        public DateTimeOffset? StartDue;
        public bool IdleFired;
        public bool Warned;
        public DateTimeOffset? NextTimeCheck;
        public bool Retrying;
        public Snapshot? Snapshot;
    }

    /// <summary>Output states before a rule acted, and what the rule set them to.</summary>
    private sealed record Snapshot(IReadOnlyList<(SceneTarget Before, SceneTarget Set)> Outputs);

    public AutomationService(IPluginSettings settings, DeviceManager devices, IRadioStateReader? radio, ILogger logger, TimeProvider? time = null)
    {
        _settings = settings;
        _devices = devices;
        _radio = radio;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _lastActivity = _time.GetUtcNow();
    }

    public IReadOnlyList<Rule> Rules { get { lock (_lock) return _rules.ToArray(); } }

    // ------------------------------------------------------------ lifecycle

    public async Task LoadAsync(CancellationToken ct)
    {
        var json = await _settings.GetAsync<string>(RulesKey, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(json)) _rules = JsonSerializer.Deserialize<List<Rule>>(json, Json.Options) ?? [];
        var s = await _settings.GetAsync<string>(SettingsKey, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(s))
            _paused = JsonSerializer.Deserialize<AutomationRequest>(s, Json.Options)?.Paused ?? false;
    }

    /// <summary>Hooks up Zeus and PowerStation activity, queues the Zeus-start rules and starts the clock.</summary>
    public void Start(bool runLoop = true)
    {
        var now = _time.GetUtcNow();
        if (_radio is not null)
        {
            _frequencyHz = _radio.FrequencyHz;
            _mode = _radio.Mode;
            _mox = _radio.Mox;
            _radio.FrequencyChanged += OnFrequency;
            _radio.ModeChanged += OnMode;
            _radio.MoxChanged += OnMox;
        }
        _devices.OperatorAction += NoteActivity;
        lock (_lock)
        {
            _lastActivity = now;
            Log(now, "Zeus started", true);
            foreach (var r in _rules.Where(r => r.Enabled && r.Trigger.Type == "zeusStart"))
                StateOf(r.Id).StartDue = now + Seconds(r.DelaySeconds);
        }
        Tick();
        if (!runLoop) return;
        _loopCts = new CancellationTokenSource();
        var token = _loopCts.Token;
        _loop = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromMilliseconds(250), token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                try { Tick(); }
                catch (Exception ex) { _logger.LogError(ex, "PowerStation: automation tick failed"); }
            }
        }, CancellationToken.None);
    }

    /// <summary>
    /// Zeus is closing cleanly: run the "Zeus closes" rules, within the time
    /// Zeus allows for shutdown. A crash never gets here; safety timers cover that.
    /// </summary>
    public async Task StopAsync(TimeSpan budget)
    {
        if (_radio is not null)
        {
            _radio.FrequencyChanged -= OnFrequency;
            _radio.ModeChanged -= OnMode;
            _radio.MoxChanged -= OnMox;
        }
        _devices.OperatorAction -= NoteActivity;
        if (_loopCts is not null) await _loopCts.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            try { await _loop.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException) { }
        }
        Rule[] stops;
        lock (_lock) stops = _paused ? [] : _rules.Where(r => r.Enabled && r.Trigger.Type == "zeusStop").ToArray();
        if (stops.Length == 0) return;
        // Zeus normally unkeys before closing. If it's still inside the TX
        // settle time, wait for it within the budget; if it's still keyed,
        // leave the outputs alone (safety timers cover them).
        var waitUntil = _time.GetUtcNow() + budget - TimeSpan.FromSeconds(1);
        while (_devices.Tx.Blocked && _time.GetUtcNow() < waitUntil)
            await Task.Delay(TimeSpan.FromMilliseconds(100), _time).ConfigureAwait(false);
        if (_devices.Tx.Blocked)
        {
            _logger.LogWarning("PowerStation: Zeus closed while transmitting; the Zeus-close rules didn't switch anything");
            return;
        }
        using var cts = new CancellationTokenSource(budget);
        try
        {
            await Task.WhenAll(stops.Select(r => ExecuteAsync(r, r.Action, "Zeus closing", isEnd: false, cts.Token)))
                .WaitAsync(budget).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
            _logger.LogWarning("PowerStation: Zeus-close rules didn't finish in time");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_loopCts is not null)
        {
            await _loopCts.CancelAsync().ConfigureAwait(false);
            _loopCts.Dispose();
            _loopCts = null;
        }
    }

    // ------------------------------------------------------------ inputs

    private void OnFrequency(long hz)
    {
        lock (_lock) _frequencyHz = hz;
        NoteActivity();
    }

    private void OnMode(string mode)
    {
        lock (_lock) _mode = mode;
        NoteActivity();
    }

    private void OnMox(bool keyed)
    {
        lock (_lock) _mox = keyed;
        NoteActivity();
        // Rules that waited for TX are released by Tick once the interlock's
        // settle time has passed, not the moment MOX drops.
    }

    /// <summary>Starts rules that waited for TX, if the radio has been quiet long enough.</summary>
    private void ReleaseTxQueue()
    {
        List<(string RuleId, string Text, Func<Task> Run)> flush;
        lock (_lock)
        {
            if (_txQueue.Count == 0 || _devices.Tx.Blocked) return;
            flush = [.. _txQueue];
            _txQueue.Clear();
        }
        foreach (var item in flush) Track(item.Run());
    }

    /// <summary>
    /// Someone is using the station: tuning, TX, a PowerStation button, or
    /// "I'm here". Resets the idle countdown; if an idle rule had run, its
    /// "when you're back" action runs now.
    /// </summary>
    public void NoteActivity()
    {
        var returned = new List<Rule>();
        lock (_lock)
        {
            _lastActivity = _time.GetUtcNow();
            _idleExtension = TimeSpan.Zero;
            foreach (var r in _rules.Where(r => r.Trigger.Type == "idle"))
            {
                var st = StateOf(r.Id);
                st.Warned = false;
                if (!st.IdleFired) continue;
                st.IdleFired = false;
                if (r.Enabled && r.EndAction is not null) returned.Add(r);
            }
            if (returned.Count > 0) Log(_lastActivity, "Activity: welcome back", true);
        }
        foreach (var r in returned) Fire(r, r.EndAction!, "back from idle", isEnd: true);
        Tick();
    }

    /// <summary>The idle countdown's extend button.</summary>
    public void ExtendIdle()
    {
        lock (_lock)
        {
            var rule = _rules.FirstOrDefault(r => r.Enabled && r.Trigger.Type == "idle");
            if (rule is null) return;
            var extend = TimeSpan.FromMinutes(rule.Trigger.ExtendMinutes ?? 30);
            var now = _time.GetUtcNow();
            var fires = IdleFiresAt(rule);
            // Extend from now if the warning was already counting down past the deadline.
            _idleExtension += fires < now ? now - fires + extend : extend;
            foreach (var r in _rules.Where(r => r.Trigger.Type == "idle")) StateOf(r.Id).Warned = false;
            Log(now, $"Idle pushed back {extend.TotalMinutes:0} min", true);
        }
    }

    public async Task SetPausedAsync(bool paused, CancellationToken ct)
    {
        lock (_lock)
        {
            if (_paused == paused) return;
            _paused = paused;
            Log(_time.GetUtcNow(), paused ? "Automations paused" : "Automations running", true);
            if (!paused)
            {
                // Start fresh: conditions are judged from now on.
                foreach (var st in _state.Values) { st.MatchSince = null; st.EndSince = null; }
            }
        }
        await _settings.SetAsync(SettingsKey, JsonSerializer.Serialize(new AutomationRequest(paused), Json.Options), ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------ the clock

    internal void Tick()
    {
        ReleaseTxQueue();
        var fire = new List<(Rule Rule, RuleAction Action, string Why, bool IsEnd)>();
        lock (_lock)
        {
            if (_paused) return;
            var now = _time.GetUtcNow();
            var mhz = _frequencyHz / 1e6;
            var band = BandOf(mhz);

            foreach (var r in _rules)
            {
                if (!r.Enabled) continue;
                var st = StateOf(r.Id);
                var t = r.Trigger;
                switch (t.Type)
                {
                    case "zeusStart":
                        if (st.StartDue is { } due && now >= due)
                        {
                            st.StartDue = null;
                            fire.Add((r, r.Action, "Zeus started", false));
                        }
                        break;

                    case "tx" or "band" or "frequency":
                    {
                        var connected = _radio is not null;
                        var match = connected && t.Type switch
                        {
                            "tx" => _mox,
                            "band" => band is not null && (t.Bands ?? []).Contains(band),
                            _ => mhz >= t.FromMHz && mhz <= t.ToMHz,
                        };
                        if (match)
                        {
                            st.EndSince = null;
                            if (st.Active) break;
                            st.MatchSince ??= now;
                            if (now - st.MatchSince >= Seconds(r.DebounceSeconds) + Seconds(r.DelaySeconds))
                            {
                                st.Active = true;
                                st.MatchSince = null;
                                fire.Add((r, r.Action, Describe(t), false));
                            }
                        }
                        else
                        {
                            st.MatchSince = null;
                            if (!st.Active) break;
                            st.EndSince ??= now;
                            if (now - st.EndSince >= Seconds(r.EndDelaySeconds))
                            {
                                st.Active = false;
                                st.EndSince = null;
                                if (r.EndAction is not null) fire.Add((r, r.EndAction, "ended", true));
                            }
                        }
                        break;
                    }

                    case "idle":
                    {
                        if (st.IdleFired) break;
                        var fires = IdleFiresAt(r);
                        var warn = TimeSpan.FromMinutes(t.WarnMinutes ?? 0);
                        if (!st.Warned && warn > TimeSpan.Zero && now >= fires - warn && now < fires)
                        {
                            st.Warned = true;
                            Log(now, $"{r.Name}: countdown shown", true);
                        }
                        if (now >= fires)
                        {
                            st.IdleFired = true;
                            fire.Add((r, r.Action, $"idle {t.Minutes} min", false));
                        }
                        break;
                    }

                    case "time":
                    {
                        st.NextTimeCheck ??= NextOccurrence(t.At!, now);
                        if (now < st.NextTimeCheck) break;
                        var idleFor = now - _lastActivity;
                        var stationIdle = _rules.Any(x => x.Trigger.Type == "idle" && StateOf(x.Id).IdleFired);
                        var nextDay = NextOccurrence(t.At!, now + TimeSpan.FromMinutes(1));
                        if (stationIdle || idleFor >= TimeSpan.FromMinutes(t.IdleMinutes ?? 0))
                        {
                            st.Retrying = false;
                            st.NextTimeCheck = nextDay;
                            fire.Add((r, r.Action, $"{t.At}", false));
                        }
                        else
                        {
                            var next = now + TimeSpan.FromMinutes(t.ExtendMinutes ?? 30);
                            st.Retrying = next < nextDay;
                            st.NextTimeCheck = next < nextDay ? next : nextDay;
                            _lastRun[r.Id] = new RuleRun(now, true, st.Retrying
                                ? $"Station active; checking again at {Local(st.NextTimeCheck.Value):HH:mm}"
                                : "Station stayed active; skipped until tomorrow");
                            Log(now, $"{r.Name}: you're active, checking again in {t.ExtendMinutes ?? 30} min", true);
                        }
                        break;
                    }
                }
            }
        }
        foreach (var (rule, action, why, isEnd) in fire) Fire(rule, action, why, isEnd);
    }

    private DateTimeOffset IdleFiresAt(Rule r) => _lastActivity + TimeSpan.FromMinutes(r.Trigger.Minutes ?? 60) + _idleExtension;

    private DateTimeOffset NextOccurrence(string at, DateTimeOffset after)
    {
        var parts = at.Split(':');
        var h = int.Parse(parts[0], CultureInfo.InvariantCulture);
        var m = int.Parse(parts[1], CultureInfo.InvariantCulture);
        var local = Local(after);
        var candidate = new DateTimeOffset(local.Year, local.Month, local.Day, h, m, 0, local.Offset);
        if (candidate <= local) candidate = candidate.AddDays(1);
        // Re-resolve the offset in case the date crosses a daylight-saving change.
        var tz = _time.LocalTimeZone;
        return new DateTimeOffset(candidate.DateTime, tz.GetUtcOffset(candidate.DateTime)).ToUniversalTime();
    }

    private DateTimeOffset Local(DateTimeOffset t) => TimeZoneInfo.ConvertTime(t, _time.LocalTimeZone);

    public static string? BandOf(double mhz) =>
        BandEdges.FirstOrDefault(kv => mhz >= kv.Value.From && mhz <= kv.Value.To).Key;

    // ------------------------------------------------------------ acting

    /// <summary>Runs an action now, or when TX ends (everything except the on-air light waits for RX).</summary>
    private void Fire(Rule rule, RuleAction action, string why, bool isEnd)
    {
        if (rule.Trigger.Type != "tx" && _devices.Tx.Blocked)
        {
            Defer(rule, action, why, isEnd);
            return;
        }
        Track(ExecuteAsync(rule, action, why, isEnd, CancellationToken.None));
    }

    /// <summary>Holds a rule's action until TX has ended and the radio has settled.</summary>
    private void Defer(Rule rule, RuleAction action, string why, bool isEnd)
    {
        lock (_lock)
        {
            _txQueue.RemoveAll(q => q.RuleId == rule.Id);
            _txQueue.Add((rule.Id, $"{rule.Name} ({why})", () => ExecuteAsync(rule, action, why, isEnd, CancellationToken.None)));
            Log(_time.GetUtcNow(), $"{rule.Name}: waiting for TX to end", true);
        }
    }

    private void Track(Task task)
    {
        lock (_inFlight)
        {
            _inFlight.RemoveAll(t => t.IsCompleted);
            _inFlight.Add(task);
        }
    }

    /// <summary>Waits for actions already started (tests, and shutdown).</summary>
    internal async Task DrainAsync()
    {
        Task[] pending;
        lock (_inFlight) pending = _inFlight.ToArray();
        await Task.WhenAll(pending).ConfigureAwait(false);
    }

    private async Task ExecuteAsync(Rule rule, RuleAction action, string why, bool isEnd, CancellationToken ct)
    {
        await _run.WaitAsync(ct).ConfigureAwait(false);
        string text;
        bool ok;
        try
        {
            // Checked again here, after waiting for any earlier rule: the
            // radio may have keyed since the rule was started or released.
            if (rule.Trigger.Type != "tx" && _devices.Tx.Blocked)
            {
                Defer(rule, action, why, isEnd);
                return;
            }
            (ok, text) = await PerformAsync(rule, action, isEnd, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is PowerStationRequestException or Shelly.ShellyException)
        {
            (ok, text) = (false, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PowerStation: rule {Rule} failed", rule.Name);
            (ok, text) = (false, "Something went wrong running this rule. See the Zeus log.");
        }
        finally
        {
            _run.Release();
        }
        var now = _time.GetUtcNow();
        lock (_lock)
        {
            _lastRun[rule.Id] = new RuleRun(now, ok, text);
            Log(now, $"{rule.Name}: {text}", ok);
        }
    }

    private async Task<(bool Ok, string Text)> PerformAsync(Rule rule, RuleAction action, bool isEnd, CancellationToken ct)
    {
        switch (action.Type)
        {
            case "none":
                return (true, "left as it was");

            case "restore":
            {
                Snapshot? snap;
                lock (_lock) { snap = StateOf(rule.Id).Snapshot; StateOf(rule.Id).Snapshot = null; }
                if (snap is null) return (true, "nothing to put back");
                // Only outputs still the way this rule left them go back; anything
                // someone changed in the meantime is left alone.
                var back = new List<SceneTarget>();
                var skipped = 0;
                foreach (var (before, set) in snap.Outputs)
                {
                    var now = _devices.GetChannel(before.DeviceId, before.ChannelKey);
                    if (now is null) { skipped++; continue; }
                    var untouched = now.On == set.On &&
                                    (!set.Kind.IsDimmable() || !set.On || set.Brightness is null ||
                                     Math.Abs((now.Brightness ?? 0) - set.Brightness.Value) <= 2);
                    if (untouched) back.Add(before);
                    else skipped++;
                }
                if (back.Count == 0) return (true, skipped > 0 ? "left alone: changed since" : "nothing to put back");
                var (results, _) = await _devices.ApplyTargetsAsync(back, null, ct).ConfigureAwait(false);
                var failed = results.Count(x => !x.Ok);
                var text = $"put back {back.Count - failed} output{(back.Count - failed == 1 ? "" : "s")}" +
                           (skipped > 0 ? $", left {skipped} someone changed" : "");
                return failed == 0 ? (true, text) : (false, $"{text}. {results.First(x => !x.Ok).Error}");
            }

            case "off":
                return await PerformAsync(rule, rule.Action.Type == "scene"
                    ? rule.Action with { Mode = "off" }
                    : rule.Action with { On = false, Brightness = null, Rgb = null, White = null }, isEnd: true, ct).ConfigureAwait(false);

            case "scene":
            {
                var scene = _devices.Scenes.Find(action.SceneId ?? "")
                            ?? throw new PowerStationRequestException(404, "The scene this rule uses was deleted.");
                var mode = action.Mode == "off" ? "off" : "apply";
                if (!isEnd) Remember(rule, mode == "off"
                    ? scene.Targets.Select(t => t with { On = false, Brightness = null, Rgb = null, White = null })
                    : scene.Targets);
                var result = await _devices.Scenes.RunAsync(scene.Id, new SceneRunRequest(mode), ct).ConfigureAwait(false);
                var verb = mode == "off" ? "all off" : "applied";
                if (result.Failed == 0) return (true, $"{scene.Name} {verb}");
                var why = result.Results.First(x => !x.Ok).Error;
                return (false, $"{scene.Name}: {result.Succeeded} of {result.Succeeded + result.Failed} outputs. {why}");
            }

            case "output":
            {
                var target = new SceneTarget
                {
                    DeviceId = action.DeviceId!,
                    Kind = action.Kind ?? ChannelKind.Switch,
                    Index = action.Index ?? 0,
                    On = action.On ?? true,
                    Brightness = (action.Kind ?? ChannelKind.Switch).IsDimmable() && action.On == true ? action.Brightness : null,
                    Rgb = action.On == true ? action.Rgb : null,
                    White = action.On == true ? action.White : null,
                };
                if (!isEnd) Remember(rule, [target]);
                var (results, _) = await _devices.ApplyTargetsAsync([target], action.RampSeconds, ct,
                    allowDuringTx: rule.Trigger.Type == "tx").ConfigureAwait(false);
                var name = _devices.GetChannel(target.DeviceId, target.ChannelKey)?.Name ?? _devices.DisplayName(target.DeviceId);
                var r = results[0];
                if (!r.Ok) return (false, r.Error ?? "failed");
                var colour = target.Rgb is { } c ? $" #{c[0]:x2}{c[1]:x2}{c[2]:x2}" : "";
                return (true, $"{name} {(target.On ? "on" : "off")}{colour}{(target.Brightness is { } b ? $" at {b:0}%" : "")}");
            }

            default:
                return (false, $"Unknown action \"{action.Type}\".");
        }
    }

    /// <summary>Records how the outputs were before a rule changes them, for "put back how it was".</summary>
    private void Remember(Rule rule, IEnumerable<SceneTarget> set)
    {
        if (rule.EndAction?.Type != "restore") return;
        var outputs = new List<(SceneTarget, SceneTarget)>();
        foreach (var t in set)
        {
            var now = _devices.GetChannel(t.DeviceId, t.ChannelKey);
            if (now is null) continue;
            outputs.Add((t with
            {
                On = now.On,
                Brightness = t.Kind.IsDimmable() && now.On ? now.Brightness : null,
                Rgb = t.Kind.IsColor() && now.On ? now.Rgb : null,
                White = t.Kind == ChannelKind.Rgbw && now.On ? now.White : null,
            }, t));
        }
        lock (_lock) StateOf(rule.Id).Snapshot = new Snapshot(outputs);
    }

    // ------------------------------------------------------------ rules CRUD

    public async Task<Rule> CreateAsync(RuleRequest request, CancellationToken ct)
    {
        await _mutate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_rules.Count >= MaxRules) throw new PowerStationRequestException(400, $"You can have up to {MaxRules} rules.");
            var rule = Validate(Guid.NewGuid().ToString("N")[..12], request);
            lock (_lock) _rules = [.. _rules, rule];
            await SaveAsync(ct).ConfigureAwait(false);
            return rule;
        }
        finally { _mutate.Release(); }
    }

    public async Task<Rule> UpdateAsync(string id, RuleRequest request, CancellationToken ct)
    {
        await _mutate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var index = IndexOf(id);
            var rule = Validate(id, request);
            lock (_lock)
            {
                var list = _rules.ToList();
                list[index] = rule;
                _rules = list;
                // Changed rules start fresh.
                _state.Remove(id);
            }
            await SaveAsync(ct).ConfigureAwait(false);
            return rule;
        }
        finally { _mutate.Release(); }
    }

    public async Task DeleteAsync(string id, CancellationToken ct)
    {
        await _mutate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var index = IndexOf(id);
            lock (_lock)
            {
                var list = _rules.ToList();
                list.RemoveAt(index);
                _rules = list;
                _state.Remove(id);
                _txQueue.RemoveAll(q => q.RuleId == id);
            }
            await SaveAsync(ct).ConfigureAwait(false);
        }
        finally { _mutate.Release(); }
    }

    /// <summary>Runs a rule's action now (still waits for TX to end, like a real trigger).</summary>
    public void Test(string id)
    {
        Rule rule;
        lock (_lock) rule = _rules[IndexOf(id)];
        Fire(rule, rule.Action, "test", isEnd: false);
    }

    private Task SaveAsync(CancellationToken ct)
    {
        string json;
        lock (_lock) json = JsonSerializer.Serialize(_rules, Json.Options);
        return _settings.SetAsync(RulesKey, json, ct);
    }

    private int IndexOf(string id)
    {
        lock (_lock)
        {
            var index = _rules.FindIndex(r => r.Id == id);
            return index >= 0 ? index : throw new PowerStationRequestException(404, "That rule doesn't exist.");
        }
    }

    private Rule Validate(string id, RuleRequest req)
    {
        static PowerStationRequestException Bad(string m) => new(400, m);
        var t = req.Trigger ?? throw Bad("Choose when the rule should act.");
        if (!TriggerTypes.Contains(t.Type)) throw Bad($"Unknown trigger \"{t.Type}\".");
        RuleTrigger trigger = t.Type switch
        {
            "band" => new RuleTrigger
            {
                Type = "band",
                Bands = (t.Bands ?? []).Distinct().ToList() is { Count: > 0 } bands && bands.All(BandEdges.ContainsKey)
                    ? bands : throw Bad("Pick at least one band."),
            },
            "frequency" => t is { FromMHz: >= 0.1 and <= 3000, ToMHz: >= 0.1 and <= 3000 } && t.FromMHz < t.ToMHz
                ? new RuleTrigger { Type = "frequency", FromMHz = t.FromMHz, ToMHz = t.ToMHz }
                : throw Bad("The range's From must be below To, between 0.1 and 3000 MHz."),
            "idle" => new RuleTrigger
            {
                Type = "idle",
                Minutes = t.Minutes is >= 5 and <= 1440 ? t.Minutes : throw Bad("Idle time must be 5 to 1,440 minutes."),
                WarnMinutes = t.WarnMinutes is null or (>= 0 and <= 60) ? t.WarnMinutes ?? 5 : throw Bad("The warning must be 0 to 60 minutes."),
                ExtendMinutes = t.ExtendMinutes is null or (>= 5 and <= 480) ? t.ExtendMinutes ?? 30 : throw Bad("Extend must be 5 to 480 minutes."),
            },
            "time" => new RuleTrigger
            {
                Type = "time",
                At = TimeOnly.TryParseExact(t.At, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)
                    ? at.ToString("HH:mm", CultureInfo.InvariantCulture) : throw Bad("Give the time as HH:MM."),
                IdleMinutes = t.IdleMinutes is null or (>= 0 and <= 240) ? t.IdleMinutes ?? 0 : throw Bad("Idle time must be 0 to 240 minutes."),
                ExtendMinutes = t.ExtendMinutes is null or (>= 5 and <= 240) ? t.ExtendMinutes ?? 30 : throw Bad("Check again must be 5 to 240 minutes."),
            },
            _ => new RuleTrigger { Type = t.Type },
        };
        if (trigger.Type == "idle" && trigger.WarnMinutes >= trigger.Minutes) throw Bad("The warning must be shorter than the idle time.");

        var action = ValidateAction(req.Action ?? throw Bad("Choose what the rule should do."), end: false);
        if (trigger.Type == "tx" && (action.Type != "output"))
            throw Bad("A TX rule can only switch one light (an on-air sign). Don't use it for amplifiers or antennas.");

        RuleAction? end = null;
        if ((Lasting.Contains(trigger.Type) || trigger.Type == "idle") && req.EndAction is not null)
        {
            end = ValidateAction(req.EndAction, end: true);
            if (trigger.Type == "tx" && end.Type is not ("off" or "none")) throw Bad("A TX rule can only turn its light off when TX ends.");
        }

        double? Range(double? v, double max, string what) =>
            v is null or 0 ? null : v is > 0 && v <= max ? v : throw Bad($"{what} must be between 0 and {max} seconds.");
        var name = (req.Name ?? "").Trim();
        if (name.Length == 0) throw Bad("Give the rule a name.");
        if (name.Length > 60) name = name[..60];

        return new Rule
        {
            Id = id,
            Name = name,
            Enabled = req.Enabled ?? true,
            Trigger = trigger,
            Action = action,
            EndAction = end,
            DebounceSeconds = trigger.Type is "tx" or "band" or "frequency" ? Range(req.DebounceSeconds, 600, "Debounce") : null,
            DelaySeconds = trigger.Type is "zeusStart" or "tx" or "band" or "frequency" ? Range(req.DelaySeconds, 3600, "Delay") : null,
            EndDelaySeconds = Lasting.Contains(trigger.Type) ? Range(req.EndDelaySeconds, 600, "The end wait") : null,
        };
    }

    private RuleAction ValidateAction(RuleAction a, bool end)
    {
        static PowerStationRequestException Bad(string m) => new(400, m);
        switch (a.Type)
        {
            case "restore" or "off" or "none" when end:
                return new RuleAction { Type = a.Type };
            case "scene":
                if (_devices.Scenes.Find(a.SceneId ?? "") is null) throw Bad("Pick a scene.");
                return new RuleAction { Type = "scene", SceneId = a.SceneId, Mode = a.Mode == "off" ? "off" : "apply" };
            case "output":
                if (a.DeviceId is null || a.Kind is null or ChannelKind.Meter || a.Index is null or < 0 or > 15 ||
                    !_devices.HasChannel(a.DeviceId, a.Kind.Value, a.Index.Value))
                    throw Bad("Pick an output.");
                if (a.Brightness is not null and (< 1 or > 100)) throw Bad("Levels must be between 1 and 100%.");
                if (a.RampSeconds is not null and (< 0 or > 600)) throw Bad("Ramp must be between 0 and 600 seconds.");
                var color = SceneManager.ValidateColor(a.Kind.Value, a.On ?? true, a.Rgb, a.White);
                return new RuleAction
                {
                    Type = "output", DeviceId = a.DeviceId, Kind = a.Kind, Index = a.Index, On = a.On ?? true,
                    Brightness = a.Kind.Value.IsDimmable() && a.On != false ? a.Brightness : null,
                    RampSeconds = a.Kind.Value.IsDimmable() && a.RampSeconds is > 0 ? a.RampSeconds : null,
                    Rgb = color.Rgb,
                    White = color.White,
                };
            default:
                throw Bad($"Unknown action \"{a.Type}\".");
        }
    }

    /// <summary>
    /// Rules that point at a removed device or scene are kept; running them
    /// reports the problem in the rule's last-run line.
    /// </summary>
    private RuleState StateOf(string id)
    {
        if (!_state.TryGetValue(id, out var s)) _state[id] = s = new RuleState();
        return s;
    }

    private static TimeSpan Seconds(double? s) => TimeSpan.FromSeconds(s ?? 0);

    private static string Describe(RuleTrigger t) => t.Type switch
    {
        "tx" => "TX",
        "band" => "band",
        _ => "in range",
    };

    private void Log(DateTimeOffset at, string text, bool ok)
    {
        _log.Insert(0, (at, text, ok));
        if (_log.Count > 50) _log.RemoveRange(50, _log.Count - 50);
    }

    // ------------------------------------------------------------ views

    public object RulesView()
    {
        lock (_lock)
            return _rules.Select(r => new
            {
                r.Id, r.Name, r.Enabled, r.Trigger, r.Action, r.EndAction, r.DebounceSeconds, r.DelaySeconds, r.EndDelaySeconds,
                lastRun = _lastRun.TryGetValue(r.Id, out var run) ? new { at = run.At, ok = run.Ok, text = run.Text } : null,
            }).ToArray();
    }

    public object View()
    {
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            var idleRules = _rules.Where(r => r.Enabled && r.Trigger.Type == "idle").ToArray();
            string idleState;
            DateTimeOffset? firesAt = null;
            Rule? idleRule = null;
            if (idleRules.Length == 0) idleState = "off";
            else if (idleRules.Any(r => StateOf(r.Id).IdleFired) && idleRules.All(r => StateOf(r.Id).IdleFired)) idleState = "idle";
            else
            {
                idleRule = idleRules.Where(r => !StateOf(r.Id).IdleFired).MinBy(IdleFiresAt)!;
                firesAt = IdleFiresAt(idleRule);
                var warn = TimeSpan.FromMinutes(idleRule.Trigger.WarnMinutes ?? 0);
                idleState = warn > TimeSpan.Zero && now >= firesAt - warn ? "warning" : "active";
            }
            idleRule ??= idleRules.FirstOrDefault();

            var pending = new List<object>();
            foreach (var r in _rules.Where(r => r.Enabled))
            {
                var st = StateOf(r.Id);
                if (st.StartDue is { } due) pending.Add(new { ruleId = r.Id, text = $"{r.Name}: after Zeus start", at = due, waitingForTx = false });
                if (st.MatchSince is { } since)
                    pending.Add(new { ruleId = r.Id, text = $"{r.Name}: starts if it holds", at = since + Seconds(r.DebounceSeconds) + Seconds(r.DelaySeconds), waitingForTx = false });
                if (st.EndSince is { } ended)
                    pending.Add(new { ruleId = r.Id, text = $"{r.Name}: ends", at = ended + Seconds(r.EndDelaySeconds), waitingForTx = false });
                if (st.Retrying && st.NextTimeCheck is { } next)
                    pending.Add(new { ruleId = r.Id, text = $"{r.Name}: next check", at = next, waitingForTx = false });
            }
            foreach (var q in _txQueue) pending.Add(new { ruleId = q.RuleId, text = q.Text, at = (DateTimeOffset?)null, waitingForTx = true });

            return new
            {
                paused = _paused,
                radio = new
                {
                    connected = _radio is not null,
                    frequencyHz = _radio is null ? (long?)null : _frequencyHz,
                    band = _radio is null ? null : BandOf(_frequencyHz / 1e6),
                    mode = _mode,
                    mox = _mox,
                },
                idle = new
                {
                    state = idleState,
                    ruleId = idleRule?.Id,
                    lastActivity = _lastActivity,
                    firesAt,
                    extendMinutes = idleRule?.Trigger.ExtendMinutes,
                },
                pending,
                log = _log.Select(l => new { at = l.At, text = l.Text, ok = l.Ok }).ToArray(),
            };
        }
    }
}
