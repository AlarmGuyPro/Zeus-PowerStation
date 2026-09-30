// SPDX-License-Identifier: GPL-2.0-or-later
using Zeus.Plugins.Contracts;

namespace KQ4WLR.PowerStation.Services;

/// <summary>
/// Keeps PowerStation from switching outputs while the radio transmits.
/// These relays can power amplifiers, preamps and antenna switches, so a
/// change is refused while MOX is on and for a short settle time after it
/// drops (CW break-in and VOX re-key between elements and words). The on-air
/// light rule is the only exception. Reads the radio's MOX at the moment of
/// each check rather than a cached value.
/// </summary>
public sealed class TxInterlock : IDisposable
{
    /// <summary>How long after unkey outputs stay locked.</summary>
    public static readonly TimeSpan Settle = TimeSpan.FromSeconds(3);

    private readonly IRadioStateReader? _radio;
    private readonly TimeProvider _time;
    private readonly object _lock = new();
    private DateTimeOffset _clearAt = DateTimeOffset.MinValue;

    public TxInterlock(IRadioStateReader? radio, TimeProvider? time = null)
    {
        _radio = radio;
        _time = time ?? TimeProvider.System;
        if (_radio is not null) _radio.MoxChanged += OnMox;
    }

    private void OnMox(bool keyed)
    {
        lock (_lock) _clearAt = keyed ? DateTimeOffset.MaxValue : _time.GetUtcNow() + Settle;
    }

    /// <summary>True while transmitting or within <see cref="Settle"/> of unkey.</summary>
    public bool Blocked
    {
        get
        {
            if (_radio is null) return false;
            if (_radio.Mox) return true;
            lock (_lock) return _clearAt != DateTimeOffset.MaxValue ? _time.GetUtcNow() < _clearAt : false;
        }
    }

    /// <summary>When outputs unlock, if they're locked only by the settle time.</summary>
    public DateTimeOffset? ClearsAt
    {
        get
        {
            if (_radio is null || _radio.Mox) return null;
            lock (_lock) return _clearAt != DateTimeOffset.MaxValue && _time.GetUtcNow() < _clearAt ? _clearAt : null;
        }
    }

    public const string BlockedMessage =
        "Zeus is transmitting. PowerStation doesn't switch outputs during TX or for 3 seconds after; try again then.";

    /// <summary>Refuses an operator command with HTTP 409 while locked.</summary>
    public void ThrowIfBlocked()
    {
        if (Blocked) throw new PowerStationRequestException(409, BlockedMessage);
    }

    public void Dispose()
    {
        if (_radio is not null) _radio.MoxChanged -= OnMox;
    }
}
