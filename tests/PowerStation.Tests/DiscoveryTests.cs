// SPDX-License-Identifier: GPL-2.0-or-later
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using KQ4WLR.PowerStation;
using KQ4WLR.PowerStation.Discovery;

namespace PowerStation.Tests;

/// <summary>Builds mDNS responses by hand, independent of the parser under test.</summary>
internal static class MdnsPacket
{
    public static byte[] Response(params (string Name, ushort Type, byte[] Data)[] answers)
    {
        var b = new List<byte> { 0, 0, 0x84, 0x00, 0, 0, 0, (byte)answers.Length, 0, 0, 0, 0 };
        foreach (var (name, type, data) in answers)
        {
            b.AddRange(Name(name));
            b.AddRange([(byte)(type >> 8), (byte)type, 0x80, 0x01, 0, 0, 0x00, 0x78, (byte)(data.Length >> 8), (byte)data.Length]);
            b.AddRange(data);
        }
        return b.ToArray();
    }

    public static byte[] Name(string name)
    {
        var b = new List<byte>();
        foreach (var label in name.Split('.'))
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            b.Add((byte)bytes.Length);
            b.AddRange(bytes);
        }
        b.Add(0);
        return b.ToArray();
    }

    public static byte[] Srv(string target) => [0, 0, 0, 0, 0, 80, .. Name(target)];

    public static byte[] Txt(params string[] entries) =>
        entries.SelectMany(e => new[] { (byte)e.Length }.Concat(Encoding.ASCII.GetBytes(e))).ToArray();
}

public static class MdnsTests
{
    [Test]
    public static void QueryAsksForBothShellyServicesWithUnicastReply()
    {
        var q = ShellyMdns.BuildQuery();
        Assert.Equal(2, (int)q[5], "question count");
        Assert.Equal(0, q[2] & 0x80, "is a query");
        var expectedFirst = MdnsPacket.Name("_shelly._tcp.local");
        Assert.True(q.AsSpan(12, expectedFirst.Length).SequenceEqual(expectedFirst), "first question is _shelly._tcp.local");
        var afterName = 12 + expectedFirst.Length;
        Assert.Equal(12, q[afterName + 1], "type PTR");
        Assert.Equal(0x80, q[afterName + 2], "QU bit set");
    }

    [Test]
    public static void ParsesGen2AnswerWithSrvTxtAndA()
    {
        const string instance = "shellypro4pm-f008d1d8b8b8._shelly._tcp.local";
        var packet = MdnsPacket.Response(
            ("_shelly._tcp.local", 12, MdnsPacket.Name(instance)),
            (instance, 33, MdnsPacket.Srv("shellypro4pm-f008d1d8b8b8.local")),
            (instance, 16, MdnsPacket.Txt("gen=2", "app=Pro4PM", "ver=1.5.0")),
            ("shellypro4pm-f008d1d8b8b8.local", 1, [10, 0, 20, 14]));
        var hits = ShellyMdns.ParseResponse(packet, IPAddress.Parse("10.9.9.9"));
        Assert.Equal(1, hits.Count, "hits");
        Assert.Equal("10.0.20.14", hits[0].Address.ToString(), "address from A record, not sender");
        Assert.Equal("shellypro4pm-f008d1d8b8b8", hits[0].InstanceName, "instance");
        Assert.Equal("Pro4PM", hits[0].Txt["app"], "txt app");
    }

    [Test]
    public static void DropsAnswersPointingOffTheLocalNetwork()
    {
        const string instance = "shelly1g4-7c2c6771eea0._shelly._tcp.local";
        var packet = MdnsPacket.Response(
            ("_shelly._tcp.local", 12, MdnsPacket.Name(instance)),
            (instance, 33, MdnsPacket.Srv("shelly1g4-7c2c6771eea0.local")),
            ("shelly1g4-7c2c6771eea0.local", 1, [8, 8, 8, 8]));
        Assert.Equal(0, ShellyMdns.ParseResponse(packet, IPAddress.Parse("192.168.1.30")).Count, "A record on the Internet dropped");
        KQ4WLR.PowerStation.Shelly.HostValidator.AllowLoopback = false;
        try
        {
            var loop = MdnsPacket.Response(
                ("_shelly._tcp.local", 12, MdnsPacket.Name(instance)),
                (instance, 33, MdnsPacket.Srv("shelly1g4-7c2c6771eea0.local")),
                ("shelly1g4-7c2c6771eea0.local", 1, [127, 0, 0, 1]));
            Assert.Equal(0, ShellyMdns.ParseResponse(loop, IPAddress.Parse("192.168.1.30")).Count, "A record on loopback dropped");
        }
        finally { KQ4WLR.PowerStation.Shelly.HostValidator.AllowLoopback = true; }
    }

    [Test]
    public static void FollowsNameCompressionPointers()
    {
        // PTR data points back at the "_shelly._tcp.local" name in the answer header (offset 12).
        var owner = MdnsPacket.Name("_shelly._tcp.local");
        var ptrData = new List<byte>();
        ptrData.AddRange([(byte)"shelly1g4-7c2c6771eea0".Length]);
        ptrData.AddRange(Encoding.ASCII.GetBytes("shelly1g4-7c2c6771eea0"));
        ptrData.AddRange([0xC0, 12]);
        var packet = MdnsPacket.Response(("_shelly._tcp.local", 12, ptrData.ToArray()));
        Assert.Equal(12 + owner.Length + 10, packet.Length - ptrData.Count, "fixture layout");
        var hits = ShellyMdns.ParseResponse(packet, IPAddress.Parse("192.168.50.116"));
        Assert.Equal(1, hits.Count, "hits");
        Assert.Equal("shelly1g4-7c2c6771eea0", hits[0].InstanceName, "instance via pointer");
        Assert.Equal("192.168.50.116", hits[0].Address.ToString(), "falls back to sender address");
    }

    [Test]
    public static void IgnoresPrintersAndJunk()
    {
        var printer = MdnsPacket.Response(("_http._tcp.local", 12, MdnsPacket.Name("Office Printer._http._tcp.local")));
        Assert.Equal(0, ShellyMdns.ParseResponse(printer, IPAddress.Loopback).Count, "printer on _http._tcp");
        var gen1 = MdnsPacket.Response(("_http._tcp.local", 12, MdnsPacket.Name("shellyplug-s-C0FFEE._http._tcp.local")));
        Assert.Equal(1, ShellyMdns.ParseResponse(gen1, IPAddress.Loopback).Count, "Gen1 shelly on _http._tcp");
        Assert.Equal(0, ShellyMdns.ParseResponse([1, 2, 3], IPAddress.Loopback).Count, "short packet");
        var truncated = MdnsPacket.Response(("_shelly._tcp.local", 12, MdnsPacket.Name("shelly1-abc._shelly._tcp.local")))[..20];
        truncated[2] = 0x84;
        Assert.Equal(0, ShellyMdns.ParseResponse(truncated, IPAddress.Loopback).Count, "truncated packet");
        var loop = new byte[] { 0, 0, 0x84, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0xC0, 12 };
        Assert.Equal(0, ShellyMdns.ParseResponse(loop, IPAddress.Loopback).Count, "pointer loop");
    }

    [Test]
    public static async Task QueriesAResponderAndCollectsTheReply()
    {
        using var responder = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)responder.Client.LocalEndPoint!).Port;
        var answer = MdnsPacket.Response(
            ("_shelly._tcp.local", 12, MdnsPacket.Name("shellyplugus-c049ef8a2b10._shelly._tcp.local")),
            ("shellyplugus-c049ef8a2b10._shelly._tcp.local", 16, MdnsPacket.Txt("gen=2")));
        var serve = Task.Run(async () =>
        {
            var q = await responder.ReceiveAsync();
            Assert.Equal(ShellyMdns.BuildQuery().Length, q.Buffer.Length, "query received");
            await responder.SendAsync(answer, q.RemoteEndPoint);
        });
        var hits = await ShellyMdns.QueryAsync(new IPEndPoint(IPAddress.Loopback, port), IPAddress.Loopback, TimeSpan.FromMilliseconds(800), CancellationToken.None);
        await serve;
        Assert.Equal(1, hits.Count, "hit");
        Assert.Equal("shellyplugus-c049ef8a2b10", hits[0].InstanceName, "instance");
        Assert.Equal("127.0.0.1", hits[0].Address.ToString(), "sender address");
    }

    [Test]
    public static async Task RealMulticastDiscoveryDoesNotThrow()
    {
        var hits = await ShellyMdns.DiscoverAsync(TimeSpan.FromMilliseconds(300), CancellationToken.None);
        Assert.True(hits.Count >= 0, "completes");
    }
}

public static class NetworkTests
{
    [Test]
    public static void ParsesNetworks()
    {
        Assert.True(Ipv4Network.TryParse("192.168.50.116/24", out var n, out _), "host bits cleared");
        Assert.Equal("192.168.50.0/24", n.ToString(), "normalized");
        Assert.Equal(254, n.HostCount, "hosts in /24");
        Assert.Equal(254, n.Hosts().Count(), "enumerated hosts");
        Assert.Equal("192.168.50.1", n.Hosts().First().ToString(), "first host");
        Assert.Equal("192.168.50.254", n.Hosts().Last().ToString(), "last host");
        Assert.True(Ipv4Network.TryParse("10.0.20.5", out var bare, out _), "bare IP means its /24");
        Assert.Equal("10.0.20.0/24", bare.ToString(), "bare");
        Assert.True(Ipv4Network.TryParse("172.16.0.0/20", out var big, out _), "/20 allowed");
        Assert.Equal(4094, big.HostCount, "/20 hosts");
        Assert.True(Ipv4Network.TryParse("192.168.1.7/32", out var one, out _), "/32");
        Assert.Equal(1, one.Hosts().Count(), "/32 hosts");
    }

    [Test]
    public static void RejectsUnsafeNetworks()
    {
        Assert.False(Ipv4Network.TryParse("10.0.0.0/16", out _, out var e1), "too big");
        Assert.Contains("too large", e1);
        Assert.False(Ipv4Network.TryParse("8.8.8.0/24", out _, out var e2), "public");
        Assert.Contains("isn't a local network", e2);
        Assert.False(Ipv4Network.TryParse("192.168.1/24", out _, out _), "short form");
        Assert.False(Ipv4Network.TryParse("fd00::/64", out _, out _), "IPv6");
        Assert.False(Ipv4Network.TryParse("192.168.1.0/abc", out _, out _), "bad prefix");
    }

    [Test]
    public static void LocalNetworksAreScannable()
    {
        foreach (var n in Ipv4Network.LocalNetworks())
        {
            Assert.True(Ipv4Network.TryParse(n.ToString(), out _, out var error), $"{n}: {error}");
            Assert.True(n.Prefix >= 22, $"{n} kept small");
        }
    }
}

/// <summary>
/// Scan and re-find tests put simulated devices on 127.0.0.2, 127.0.0.3...
/// sharing one port, like real Shellies sharing port 80. macOS doesn't alias
/// those loopback addresses by default, so there the tests report SKIP.
/// </summary>
public static class DiscoveryTests
{
    private static int FreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    private static async Task<FakeShellyGen2?> Try(string ip, int port, string id)
    {
        try
        {
            return await FakeShellyGen2.StartAsync(id, f => f.Switches.Add(new FakeShellyGen2.FakeSwitch()), url: $"http://{ip}:{port}");
        }
        catch (Exception ex) when (ex is IOException or SocketException or InvalidOperationException)
        {
            Console.WriteLine($"  SKIP  can't bind {ip} on this OS ({ex.GetType().Name})");
            return null;
        }
    }

    private static async Task<JsonNode> WaitForScan(PluginHost host)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            var (_, view) = await host.SendAsync(HttpMethod.Get, "discovery");
            if (!view!["scan"]!["running"]!.GetValue<bool>()) return view;
            await Task.Delay(100);
        }
        throw new AssertionException("scan didn't finish in 20 s");
    }

    [Test]
    public static async Task ScanFindsDevicesAndMarksAddedOnes()
    {
        var port = FreePort();
        await using var a = await Try("127.0.0.2", port, "shellypro4pm-0000000000a1");
        if (a is null) return;
        await using var b = await Try("127.0.0.3", port, "shellyplugus-0000000000b2");
        await using var host = await PluginHost.StartAsync(plugin: new PowerStationPlugin { ScanPort = port, UseMdns = false });

        var (saved, view) = await host.SendAsync(HttpMethod.Put, "discovery", new { networks = new[] { "127.0.0.0/29" } });
        Assert.Equal(HttpStatusCode.OK, saved, $"save networks: {view?.ToJsonString()}");
        Assert.Equal("127.0.0.0/29", view!["settings"]!["networks"]![0]!.GetValue<string>(), "saved");

        var (started, _) = await host.SendAsync(HttpMethod.Post, "discovery/scan", new { mdns = false });
        Assert.Equal(HttpStatusCode.OK, started, "scan started");
        var done = await WaitForScan(host);
        var found = done["scan"]!["found"]!.AsArray();
        Assert.Equal(2, found.Count, $"found: {done.ToJsonString()}");
        Assert.Equal(6, done["scan"]!["probed"]!.GetValue<int>(), "probed all 6 hosts of the /29");
        Assert.True(found.All(f => !f!["added"]!.GetValue<bool>()), "nothing added yet");

        await host.SendAsync(HttpMethod.Post, "devices", new { host = $"127.0.0.2:{port}" });
        await host.SendAsync(HttpMethod.Post, "discovery/scan", new { mdns = false });
        done = await WaitForScan(host);
        var pro = done["scan"]!["found"]!.AsArray().Single(f => f!["device"]!["deviceId"]!.GetValue<string>() == "shellypro4pm-0000000000a1");
        Assert.True(pro!["added"]!.GetValue<bool>(), "added device marked");
    }

    [Test]
    public static async Task DeviceThatMovesIsFoundAutomatically()
    {
        var port = FreePort();
        const string id = "shelly1g4-7c2c6771eea0";
        var before = await Try("127.0.0.4", port, id);
        if (before is null) return;

        var context = new FakePluginContext();
        await context.Settings.SetAsync("options.v1", """{"pollIntervalMs":500,"refindAfterFailures":1,"refindCooldownMs":1000}""");
        await using var host = await PluginHost.StartAsync(context, new PowerStationPlugin { UseMdns = false });
        var (added, _) = await host.SendAsync(HttpMethod.Post, "devices", new { host = $"127.0.0.4:{port}", name = "Antenna Genius Power" });
        Assert.Equal(HttpStatusCode.OK, added, "added");

        // DHCP hands the device a new address.
        await before.DisposeAsync();
        await using var after = await Try("127.0.0.9", port, id);
        if (after is null) return;

        var deadline = DateTime.UtcNow.AddSeconds(20);
        JsonNode? device = null;
        while (DateTime.UtcNow < deadline)
        {
            var (_, status) = await host.SendAsync(HttpMethod.Get, "status");
            device = status!["devices"]![0];
            if (device!["host"]!.GetValue<string>() == $"127.0.0.9:{port}" &&
                device["status"]!["health"]!.GetValue<string>() == "Online") break;
            await Task.Delay(200);
        }
        Assert.Equal($"127.0.0.9:{port}", device!["host"]!.GetValue<string>(), "new address");
        Assert.Equal("Online", device["status"]!["health"]!.GetValue<string>(), "back online");
        Assert.Equal($"127.0.0.4:{port}", device["previousHost"]!.GetValue<string>(), "old address remembered");
        Assert.Equal("Antenna Genius Power", device["displayName"]!.GetValue<string>(), "name kept");
    }

    private static async Task<JsonNode> WaitForDevice(PluginHost host, Func<JsonNode, bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        JsonNode device = null!;
        while (DateTime.UtcNow < deadline)
        {
            var (_, status) = await host.SendAsync(HttpMethod.Get, "status");
            device = status!["devices"]![0]!;
            if (done(device)) return device;
            await Task.Delay(200);
        }
        return device;
    }

    private static async Task<PluginHost> FastRefindHost()
    {
        var context = new FakePluginContext();
        await context.Settings.SetAsync("options.v1", """{"pollIntervalMs":500,"refindAfterFailures":1,"refindCooldownMs":1000}""");
        return await PluginHost.StartAsync(context, new PowerStationPlugin { UseMdns = false });
    }

    [Test]
    public static async Task DeviceWithAPasswordWaitsForTheOperatorToConfirmANewAddress()
    {
        var port = FreePort();
        const string id = "shellyplus1-7c2c6771eea1";
        FakeShellyGen2? before;
        try { before = await FakeShellyGen2.StartAsync(id, f => { f.Password = "pw"; f.Switches.Add(new FakeShellyGen2.FakeSwitch()); }, url: $"http://127.0.0.10:{port}"); }
        catch (Exception ex) when (ex is IOException or SocketException or InvalidOperationException) { Console.WriteLine("  SKIP  can't bind 127.0.0.10"); return; }

        await using var host = await FastRefindHost();
        var (added, body) = await host.SendAsync(HttpMethod.Post, "devices", new { host = $"127.0.0.10:{port}", password = "pw" });
        Assert.Equal(HttpStatusCode.OK, added, $"added: {body?.ToJsonString()}");

        await before.DisposeAsync();
        await using var after = await FakeShellyGen2.StartAsync(id, f => { f.Password = "pw"; f.Switches.Add(new FakeShellyGen2.FakeSwitch()); }, url: $"http://127.0.0.11:{port}");

        var device = await WaitForDevice(host, d => d["pendingHost"] is not null);
        Assert.Equal($"127.0.0.11:{port}", device["pendingHost"]?.GetValue<string>(), "new address offered");
        Assert.Equal($"127.0.0.10:{port}", device["host"]!.GetValue<string>(), "not moved on its own");
        await Task.Delay(1500);
        Assert.Equal(0, after.Calls.Count, "no RPC (and so no digest login) sent to the new address before confirming");

        var (confirmed, moved) = await host.SendAsync(HttpMethod.Post, $"devices/{id}/move");
        Assert.Equal(HttpStatusCode.OK, confirmed, $"confirm: {moved?.ToJsonString()}");
        Assert.Equal($"127.0.0.11:{port}", moved!["host"]!.GetValue<string>(), "moved after confirming");
        Assert.Equal("Online", moved["status"]!["health"]!.GetValue<string>(), "online at the new address");
        Assert.True(moved["pendingHost"] is null, "nothing left to confirm");
    }

    [Test]
    public static async Task Gen1PasswordIsNeverSentToAnUnconfirmedAddressAndIgnoreSticks()
    {
        var port = FreePort();
        FakeShellyGen1? before;
        try { before = await FakeShellyGen1.StartAsync("SHSW-1", f => { f.Password = "gen1-pw"; f.Relays.Add(new FakeShellyGen1.Relay()); }, url: $"http://127.0.0.12:{port}"); }
        catch (Exception ex) when (ex is IOException or SocketException or InvalidOperationException) { Console.WriteLine("  SKIP  can't bind 127.0.0.12"); return; }
        var id = before.DeviceId;

        await using var host = await FastRefindHost();
        var (added, body) = await host.SendAsync(HttpMethod.Post, "devices", new { host = $"127.0.0.12:{port}", password = "gen1-pw" });
        Assert.Equal(HttpStatusCode.OK, added, $"added: {body?.ToJsonString()}");

        await before.DisposeAsync();
        await using var impostor = await FakeShellyGen1.StartAsync("SHSW-1", f => { f.Password = "other"; f.Relays.Add(new FakeShellyGen1.Relay()); }, url: $"http://127.0.0.13:{port}");

        var device = await WaitForDevice(host, d => d["pendingHost"] is not null);
        Assert.Equal($"127.0.0.13:{port}", device["pendingHost"]?.GetValue<string>(), "offered, not moved");
        await Task.Delay(1500);
        Assert.Equal(0, impostor.AuthHeaders.Count, "the Basic-auth password never went to the new address");
        Assert.Equal(0, impostor.Requests.Count, "only the unauthenticated /shelly identify was sent");

        var (ignored, view) = await host.SendAsync(HttpMethod.Delete, $"devices/{id}/move");
        Assert.Equal(HttpStatusCode.OK, ignored, "ignore");
        Assert.True(view!["pendingHost"] is null, "cleared");
        await Task.Delay(3000);
        var (_, status) = await host.SendAsync(HttpMethod.Get, "status");
        Assert.True(status!["devices"]![0]!["pendingHost"] is null, "an ignored address isn't offered again");
        Assert.Equal(0, impostor.AuthHeaders.Count, "still no password sent");
    }

    [Test]
    public static async Task OnlineDeviceIsNotMovedToItsSecondAddress()
    {
        // A Pro on both Ethernet and Wi-Fi answers at two addresses with one ID.
        var port = FreePort();
        const string id = "shellypro4pm-00000000dual";
        await using var eth = await Try("127.0.0.5", port, id);
        if (eth is null) return;
        await using var wifi = await Try("127.0.0.6", port, id);
        await using var host = await PluginHost.StartAsync(plugin: new PowerStationPlugin { ScanPort = port, UseMdns = false });
        await host.SendAsync(HttpMethod.Post, "devices", new { host = $"127.0.0.5:{port}" });
        await host.SendAsync(HttpMethod.Post, "discovery/scan", new { networks = new[] { "127.0.0.4/30" }, mdns = false });
        await WaitForScan(host);
        var (_, status) = await host.SendAsync(HttpMethod.Get, "status");
        Assert.Equal($"127.0.0.5:{port}", status!["devices"]![0]!["host"]!.GetValue<string>(), "kept its working address");
    }

    [Test]
    public static async Task ScanRequestsAreValidated()
    {
        await using var host = await PluginHost.StartAsync();
        async Task<(HttpStatusCode, string)> Scan(object body)
        {
            var (s, b) = await host.SendAsync(HttpMethod.Post, "discovery/scan", body);
            return (s, b?["error"]?.GetValue<string>() ?? "");
        }
        var (s1, e1) = await Scan(new { networks = new[] { "10.0.0.0/16" }, mdns = false });
        Assert.Equal(HttpStatusCode.BadRequest, s1, "too big");
        Assert.Contains("too large", e1);
        var (s2, _) = await Scan(new { networks = new[] { "8.8.8.0/24" }, mdns = false });
        Assert.Equal(HttpStatusCode.BadRequest, s2, "public");
        var (s3, e3) = await Scan(new { networks = new[] { "10.0.0.0/22", "10.0.4.0/22", "10.0.8.0/22", "10.0.12.0/22", "10.0.16.0/22" }, mdns = false });
        Assert.Equal(HttpStatusCode.BadRequest, s3, "too many addresses");
        Assert.Contains("4,096", e3);
        var (put, _) = await host.SendAsync(HttpMethod.Put, "discovery", new { networks = new[] { "not a network" } });
        Assert.Equal(HttpStatusCode.BadRequest, put, "bad saved network");
        var (_, view) = await host.SendAsync(HttpMethod.Get, "discovery");
        Assert.NotNull(view!["suggested"], "suggestions listed");
        Assert.True(view["settings"]!["autoRefind"]!.GetValue<bool>(), "auto re-find on by default");
    }
}
