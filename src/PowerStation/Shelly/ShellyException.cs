// SPDX-License-Identifier: GPL-2.0-or-later
namespace KQ4WLR.PowerStation.Shelly;

public enum ShellyErrorKind
{
    /// <summary>Timeout, refused connection, DNS failure.</summary>
    Unreachable,
    /// <summary>Device requires credentials we don't have or rejected them.</summary>
    Unauthorized,
    /// <summary>Device rate-limited us (HTTP 429, brute-force protection).</summary>
    Throttled,
    /// <summary>Device returned an RPC error (bad argument, unsupported method...).</summary>
    DeviceError,
    /// <summary>Response wasn't what a Shelly should send.</summary>
    Protocol,
    /// <summary>The device is a generation this version can't drive yet.</summary>
    Unsupported,
}

public sealed class ShellyException : Exception
{
    public ShellyException(ShellyErrorKind kind, string message, int? rpcCode = null, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
        RpcCode = rpcCode;
    }

    public ShellyErrorKind Kind { get; }
    public int? RpcCode { get; }
}
