// SPDX-License-Identifier: GPL-2.0-or-later
using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using KQ4WLR.PowerStation.Shelly;

namespace KQ4WLR.PowerStation.Discovery;

/// <summary>An IPv4 range to sweep, written as CIDR (e.g. 192.168.50.0/24).</summary>
public readonly record struct Ipv4Network(uint Network, int Prefix)
{
    /// <summary>Largest single range PowerStation will sweep (/20 = 4,094 hosts).</summary>
    public const int SmallestPrefix = 20;

    public uint Mask => Prefix == 0 ? 0 : uint.MaxValue << (32 - Prefix);

    /// <summary>Usable host addresses (network and broadcast excluded for /30 and larger).</summary>
    public int HostCount => Prefix >= 31 ? 1 << (32 - Prefix) : (1 << (32 - Prefix)) - 2;

    public override string ToString() => $"{ToAddress(Network)}/{Prefix}";

    public IEnumerable<IPAddress> Hosts()
    {
        var size = 1u << (32 - Prefix);
        var (first, last) = Prefix >= 31 ? (Network, Network + size - 1) : (Network + 1, Network + size - 2);
        for (var a = first; a <= last && a >= first; a++) yield return ToAddress(a);
    }

    public bool Contains(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetwork && (ToUInt(address) & Mask) == Network;

    public static bool TryParse(string? text, out Ipv4Network network, out string? error)
    {
        network = default;
        error = null;
        var s = text?.Trim() ?? "";
        if (s.Length == 0) { error = "Enter a network such as 192.168.1.0/24."; return false; }
        var slash = s.IndexOf('/');
        var prefix = 24;
        if (slash >= 0 && !int.TryParse(s[(slash + 1)..], out prefix))
        {
            error = $"\"{s}\" isn't a valid network. Use the form 192.168.1.0/24.";
            return false;
        }
        var addrText = slash >= 0 ? s[..slash] : s;
        if (!IPAddress.TryParse(addrText, out var addr) || addr.AddressFamily != AddressFamily.InterNetwork ||
            addrText.Count(ch => ch == '.') != 3)
        {
            error = $"\"{s}\" isn't a valid IPv4 network. Use the form 192.168.1.0/24.";
            return false;
        }
        if (prefix is < SmallestPrefix or > 32)
        {
            error = $"{s} is too large to sweep. Use /{SmallestPrefix} or smaller (for example /24).";
            return false;
        }
        var mask = prefix == 0 ? 0 : uint.MaxValue << (32 - prefix);
        network = new Ipv4Network(ToUInt(addr) & mask, prefix);
        if (!HostValidator.IsLocal(ToAddress(network.Network)))
        {
            error = $"{network} isn't a local network. PowerStation only scans private LAN addresses.";
            return false;
        }
        return true;
    }

    internal static uint ToUInt(IPAddress a) => BinaryPrimitives.ReadUInt32BigEndian(a.GetAddressBytes());

    internal static IPAddress ToAddress(uint v)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, v);
        return new IPAddress(b);
    }

    /// <summary>The /24 containing an address, used to look for a device near where it was.</summary>
    public static Ipv4Network Around(IPAddress address) => new(ToUInt(address) & 0xFFFFFF00u, 24);

    /// <summary>
    /// The private IPv4 networks this computer is attached to. Networks
    /// larger than a /22 are narrowed to the /24 around this computer's
    /// address so a default scan stays quick.
    /// </summary>
    public static IReadOnlyList<Ipv4Network> LocalNetworks()
    {
        var list = new List<Ipv4Network>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork || !HostValidator.IsLocal(ua.Address)) continue;
                    if (IPAddress.IsLoopback(ua.Address)) continue;
                    var b = ua.Address.GetAddressBytes();
                    if (b[0] == 169 && b[1] == 254) continue; // no DHCP lease; not a real LAN
                    var prefix = ua.PrefixLength is >= 22 and <= 30 ? ua.PrefixLength : 24;
                    var mask = uint.MaxValue << (32 - prefix);
                    var net = new Ipv4Network(ToUInt(ua.Address) & mask, prefix);
                    if (!list.Contains(net)) list.Add(net);
                }
            }
        }
        catch (NetworkInformationException) { }
        return list;
    }
}

/// <summary>A Shelly found by a scan, confirmed through its /shelly endpoint.</summary>
public sealed record FoundDevice
{
    public required string Host { get; init; }
    public required string DeviceId { get; init; }
    public required int Generation { get; init; }
    public string? Model { get; init; }
    public string? App { get; init; }
    public string? Name { get; init; }
    public bool AuthRequired { get; init; }
    public bool Supported => Generation >= 2;
    /// <summary>"mdns", "sweep" or both.</summary>
    public required IReadOnlyList<string> FoundBy { get; init; }
}

/// <summary>Probes addresses with the unauthenticated /shelly endpoint.</summary>
public sealed class NetworkScanner
{
    public const int MaxHostsPerScan = 4096;
    private readonly HttpClient _http;
    private readonly int _port;
    private readonly int _concurrency;

    /// <param name="port">HTTP port to probe; 80 for real devices, overridden in tests.</param>
    public NetworkScanner(HttpClient http, int port = 80, int concurrency = 48)
    {
        _http = http;
        _port = port;
        _concurrency = concurrency;
    }

    public string HostFor(IPAddress address) => _port == 80 ? address.ToString() : $"{address}:{_port}";

    /// <summary>
    /// Sweeps <paramref name="addresses"/>, calling <paramref name="progress"/>
    /// after each probe and <paramref name="found"/> for each Shelly.
    /// </summary>
    public async Task SweepAsync(
        IReadOnlyList<IPAddress> addresses,
        Func<FoundDevice, Task> found,
        Action<int> progress,
        CancellationToken ct)
    {
        var done = 0;
        await Parallel.ForEachAsync(addresses, new ParallelOptions { MaxDegreeOfParallelism = _concurrency, CancellationToken = ct },
            async (address, token) =>
            {
                var device = await ProbeAsync(HostFor(address), "sweep", token).ConfigureAwait(false);
                if (device is not null) await found(device).ConfigureAwait(false);
                progress(Interlocked.Increment(ref done));
            }).ConfigureAwait(false);
    }

    /// <summary>Identifies one host; null when nothing Shelly-like answers.</summary>
    public async Task<FoundDevice?> ProbeAsync(string host, string how, CancellationToken ct)
    {
        try
        {
            var id = await Gen2Client.IdentifyAsync(_http, host, ct).ConfigureAwait(false);
            return new FoundDevice
            {
                Host = host,
                DeviceId = id.DeviceId,
                Generation = id.Generation,
                Model = id.Model,
                App = id.App,
                Name = id.DefaultName,
                AuthRequired = id.AuthRequired,
                FoundBy = [how],
            };
        }
        catch (ShellyException)
        {
            return null;
        }
    }

    /// <summary>Short timeouts: most addresses in a sweep have nothing listening.</summary>
    public static HttpClient CreateScanHttpClient() =>
        new(new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromMilliseconds(900),
            PooledConnectionLifetime = TimeSpan.FromSeconds(10),
            MaxConnectionsPerServer = 1,
        })
        {
            Timeout = TimeSpan.FromMilliseconds(1800),
        };
}
