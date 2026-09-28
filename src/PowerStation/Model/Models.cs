// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json.Serialization;

namespace KQ4WLR.PowerStation.Model;

/// <summary>Kind of controllable output on a Shelly device.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ChannelKind>))]
public enum ChannelKind
{
    Switch,
    Light,
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

    /// <summary>Operator-assigned names per channel key (e.g. <c>switch:0</c>).</summary>
    public Dictionary<string, string> ChannelNames { get; init; } = new();

    public DateTimeOffset AddedAt { get; init; }
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

    /// <summary>True when the device reports any metering value for this channel.</summary>
    public bool Metered => PowerW is not null || VoltageV is not null || CurrentA is not null;
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
