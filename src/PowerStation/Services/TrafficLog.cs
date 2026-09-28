// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.RegularExpressions;

namespace KQ4WLR.PowerStation.Services;

/// <summary>One request to a device, or one notable event.</summary>
public sealed record TrafficEntry
{
    public required long Seq { get; init; }
    public required DateTimeOffset At { get; init; }
    /// <summary>rpc (Gen2+), http (Gen1 and identification) or event.</summary>
    public required string Kind { get; init; }
    public string? DeviceId { get; init; }
    public string? Host { get; init; }
    /// <summary>RPC method (Shelly.GetStatus) or path (/relay/0).</summary>
    public required string Method { get; init; }
    public string? Request { get; init; }
    public int? Status { get; init; }
    public string? Response { get; init; }
    public string? Error { get; init; }
    public double? Ms { get; init; }
    /// <summary>Routine reads (status polls) that are only kept when "record everything" is on.</summary>
    public bool Routine { get; init; }
    public bool Ok => Error is null;
}

public sealed record TrafficQuery(long? Since, string? DeviceId, bool ErrorsOnly, bool HideRoutine, string? Text, int Max = 300);

/// <summary>
/// A ring buffer of the traffic between PowerStation and the Shelly devices,
/// for the Debug section in setup. Memory only, never written to disk, and
/// bounded three ways: at most 500 entries, 2,000 characters per request or
/// reply, and 24 hours of age. Errors and commands are kept; routine status
/// polls only while "record every poll" is on (it turns itself off after 30
/// minutes). The whole log can be switched off, which also clears it.
/// Secrets never get here: auth headers aren't logged and request bodies
/// carry no passwords.
/// </summary>
public sealed class TrafficLog
{
    /// <summary>Most entries kept; the oldest drop off first.</summary>
    public const int Capacity = 500;
    /// <summary>Longest request or reply kept per entry.</summary>
    public const int MaxBodyChars = 2000;
    /// <summary>Entries older than this are dropped.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    private static readonly HashSet<string> RoutineMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "Shelly.GetStatus", "Shelly.GetConfig", "Sys.GetStatus", "/status", "/settings",
    };

    private readonly object _lock = new();
    private readonly LinkedList<TrafficEntry> _entries = new();
    private readonly TimeProvider _time;
    private long _seq;

    public TrafficLog(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    /// <summary>
    /// The log is on unless the operator turns it off. Off records nothing.
    /// It lives in memory only and is never written to disk.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Keep every poll, not just errors and commands.</summary>
    public bool RecordAll { get; set; }

    /// <summary>Everything recorded until: record-all switches itself off after 30 minutes.</summary>
    public DateTimeOffset? RecordAllUntil { get; private set; }

    public void SetRecordAll(bool on)
    {
        RecordAll = on;
        RecordAllUntil = on ? _time.GetUtcNow().AddMinutes(30) : null;
    }

    public static bool IsRoutine(string method) =>
        RoutineMethods.Contains(method.Split('?')[0]);

    public void Add(string kind, string? deviceId, string? host, string method, string? request, int? status,
        string? response, string? error, double? ms)
    {
        if (!Enabled) return;
        if (RecordAll && RecordAllUntil is { } until && _time.GetUtcNow() > until) SetRecordAll(false);
        var routine = IsRoutine(method);
        if (routine && error is null && !RecordAll) return;
        lock (_lock)
        {
            _entries.AddLast(new TrafficEntry
            {
                Seq = ++_seq,
                At = _time.GetUtcNow(),
                Kind = kind,
                DeviceId = deviceId,
                Host = host,
                Method = method,
                Request = Trim(request),
                Status = status,
                Response = Trim(response),
                Error = error,
                Ms = ms is null ? null : Math.Round(ms.Value, 1),
                Routine = routine,
            });
            while (_entries.Count > Capacity) _entries.RemoveFirst();
            Prune();
        }
    }

    /// <summary>Drops entries past <see cref="MaxAge"/>. Call with the lock held.</summary>
    private void Prune()
    {
        var oldest = _time.GetUtcNow() - MaxAge;
        while (_entries.First is { } first && first.Value.At < oldest) _entries.RemoveFirst();
    }

    public void SetEnabled(bool on)
    {
        Enabled = on;
        if (!on)
        {
            SetRecordAll(false);
            Clear();
        }
    }

    /// <summary>Something PowerStation noticed that isn't a single request (a crash while reading a device, say).</summary>
    public void Event(string? deviceId, string what, string? error = null) =>
        Add("event", deviceId, null, what, null, null, null, error, null);

    public void Clear()
    {
        lock (_lock) _entries.Clear();
    }

    public object Query(TrafficQuery q, Func<string?, string?> deviceName)
    {
        Regex? text = null;
        if (!string.IsNullOrWhiteSpace(q.Text))
            text = new Regex(Regex.Escape(q.Text.Trim()), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        TrafficEntry[] all;
        lock (_lock)
        {
            Prune();
            all = _entries.ToArray();
        }
        var matches = all
            .Where(e => q.Since is null || e.Seq > q.Since)
            .Where(e => q.DeviceId is null || e.DeviceId == q.DeviceId || (q.DeviceId == "" && e.DeviceId is null))
            .Where(e => !q.ErrorsOnly || !e.Ok)
            .Where(e => !q.HideRoutine || !e.Routine || !e.Ok)
            .Where(e => text is null || text.IsMatch($"{e.Method} {e.Host} {e.Request} {e.Response} {e.Error} {deviceName(e.DeviceId)}"))
            .TakeLast(Math.Clamp(q.Max, 1, Capacity))
            .ToArray();
        return new
        {
            enabled = Enabled,
            recordAll = RecordAll,
            recordAllUntil = RecordAllUntil,
            latest = all.Length == 0 ? 0 : all[^1].Seq,
            total = all.Length,
            entries = matches.Select(e => new
            {
                e.Seq, e.At, e.Kind, e.DeviceId, deviceName = deviceName(e.DeviceId), e.Host, e.Method, e.Request,
                e.Status, e.Response, e.Error, e.Ms, e.Routine, e.Ok,
            }),
        };
    }

    private static string? Trim(string? s) =>
        s is null ? null : s.Length <= MaxBodyChars ? s : s[..MaxBodyChars] + $"… ({s.Length - MaxBodyChars} more characters)";
}
