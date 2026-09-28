// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json.Serialization;

namespace KQ4WLR.PowerStation.Model;

/// <summary>Kind of channel on a Shelly device.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ChannelKind>))]
public enum ChannelKind
{
    Switch,
    Light,
    /// <summary>Read-only energy meter (ShellyEM clamps). Can't be switched.</summary>
    Meter,
    /// <summary>Colour LED controller in RGB mode (e.g. Plus RGBW PM, <c>rgb:N</c>).</summary>
    Rgb,
    /// <summary>Colour LED controller with a white channel (<c>rgbw:N</c>).</summary>
    Rgbw,
}

public static class ChannelKinds
{
    /// <summary>Has a brightness level: dimmers and colour lights.</summary>
    public static bool IsDimmable(this ChannelKind kind) => kind is ChannelKind.Light or ChannelKind.Rgb or ChannelKind.Rgbw;
    public static bool IsColor(this ChannelKind kind) => kind is ChannelKind.Rgb or ChannelKind.Rgbw;

    /// <summary>The component prefix in channel keys and RPC method names' lower-case form.</summary>
    public static string Prefix(this ChannelKind kind) => kind switch
    {
        ChannelKind.Light => "light",
        ChannelKind.Meter => "emeter",
        ChannelKind.Rgb => "rgb",
        ChannelKind.Rgbw => "rgbw",
        _ => "switch",
    };

    /// <summary>Gen2 RPC component name: Switch, Light, RGB, RGBW.</summary>
    public static string RpcName(this ChannelKind kind) => kind switch
    {
        ChannelKind.Light => "Light",
        ChannelKind.Rgb => "RGB",
        ChannelKind.Rgbw => "RGBW",
        _ => "Switch",
    };
}

/// <summary>What PowerStation knows about a device before it is saved.</summary>
public sealed record DeviceIdentity
{
    /// <summary>Shelly device id, e.g. <c>shellypro4pm-f008d1d8b8b8</c>. Also the digest realm.</summary>
    public required string DeviceId { get; init; }
    public required int Generation { get; init; }
    public string? Model { get; init; }
    public string? App { get; init; }
    public string? Mac { get; init; }
    public string? Firmware { get; init; }
    public bool AuthRequired { get; init; }
    public string? DefaultName { get; init; }
}

/// <summary>
/// A device the operator has added. Persisted in the plugin's settings store.
/// Secrets are never returned by the HTTP API.
/// </summary>
public sealed record DeviceRecord
{
    public required string DeviceId { get; init; }
    public required string Host { get; init; }
    public required int Generation { get; init; }
    public string? Name { get; init; }
    public string? Model { get; init; }
    public string? App { get; init; }
    public string? Mac { get; init; }
    public bool AuthRequired { get; init; }

    /// <summary>Gen2+: SHA256(admin:realm:password). Never the password.</summary>
    public string? Ha1 { get; init; }

    /// <summary>
    /// Gen1 only: HTTP Basic credentials. Gen1 firmware has no hashed form,
    /// so the password is stored as entered; the UI says so.
    /// </summary>
    public string? Gen1User { get; init; }
    public string? Gen1Password { get; init; }

    /// <summary>Device-side safety timer per channel key, in minutes.</summary>
    public Dictionary<string, int> SafetyMinutes { get; init; } = new();

    /// <summary>
    /// Wired across two legs (e.g. 240 V between L1 and L2 on a US split-phase
    /// panel), so it measures twice the line-to-neutral mains voltage.
    /// </summary>
    public bool LineToLine { get; init; }

    /// <summary>Operator's current limits per channel key, replacing the model rating defaults.</summary>
    public Dictionary<string, LimitOverride> Limits { get; init; } = new();

    /// <summary>Operator-assigned names per channel key (e.g. <c>switch:0</c>).</summary>
    public Dictionary<string, string> ChannelNames { get; init; } = new();

    public DateTimeOffset AddedAt { get; init; }

    /// <summary>Address before PowerStation last found the device somewhere new.</summary>
    public string? PreviousHost { get; init; }
    public DateTimeOffset? HostChangedAt { get; init; }
}

/// <summary>Live reading and state for one output channel.</summary>
public sealed record ChannelState
{
    public required string Key { get; init; }
    public required ChannelKind Kind { get; init; }
    public required int Index { get; init; }
    public string? Name { get; init; }
    public bool On { get; init; }
    public double? Brightness { get; init; }
    /// <summary>Colour lights: [r, g, b], 0-255 each.</summary>
    public int[]? Rgb { get; init; }
    /// <summary>RGBW: white channel, 0-255.</summary>
    public double? White { get; init; }
    public double? PowerW { get; init; }
    public double? VoltageV { get; init; }
    public double? CurrentA { get; init; }
    public double? PowerFactor { get; init; }
    public double? FrequencyHz { get; init; }
    public double? EnergyWh { get; init; }
    public double? TemperatureC { get; init; }
    public string? Source { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = [];
    public IReadOnlyList<string> Flags { get; init; } = [];

    /// <summary>When a flip-back timer on the device will change this output, if one is running.</summary>
    public DateTimeOffset? TimerEndsAt { get; init; }

    /// <summary>Safety timer in effect for this output, in minutes (null = none).</summary>
    public int? SafetyMinutes { get; init; }

    /// <summary>Current limits in effect (model rating or the operator's own).</summary>
    public CurrentLimits? Limits { get; init; }

    /// <summary>Readings outside their normal range right now.</summary>
    public IReadOnlyList<ReadingAlert> Alerts { get; init; } = [];

    /// <summary>True when the device reports any metering value for this channel.</summary>
    public bool Metered => PowerW is not null || VoltageV is not null || CurrentA is not null;
}

public sealed record LimitOverride
{
    public double? WarnA { get; init; }
    public double? MaxA { get; init; }
    public double? MinOnA { get; init; }
}

public sealed record CurrentLimits
{
    public double? RatedA { get; init; }
    public double? WarnA { get; init; }
    public double? MaxA { get; init; }
    public double? MinOnA { get; init; }
    public bool Custom { get; init; }
}

public sealed record ReadingAlert
{
    /// <summary>voltageHigh, voltageLow, currentHigh or currentLow.</summary>
    public required string Kind { get; init; }
    /// <summary>warn (outside normal) or limit (outside the hard limit).</summary>
    public required string Level { get; init; }
    public required double Value { get; init; }
    public required double Threshold { get; init; }
    public required DateTimeOffset Since { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<DeviceHealth>))]
public enum DeviceHealth
{
    /// <summary>Not polled yet.</summary>
    Pending,
    Online,
    /// <summary>No answer (timeout, refused, DNS).</summary>
    Unreachable,
    /// <summary>Device answered but rejected our credentials.</summary>
    Unauthorized,
    /// <summary>Device answered with something we could not understand.</summary>
    Error,
}

public sealed record DeviceStatus
{
    public DeviceHealth Health { get; init; } = DeviceHealth.Pending;
    public string? Message { get; init; }
    public DateTimeOffset? LastSeen { get; init; }
    public DateTimeOffset? LastPolled { get; init; }
    public IReadOnlyList<ChannelState> Channels { get; init; } = [];
}
