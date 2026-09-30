// SPDX-License-Identifier: GPL-2.0-or-later
using System.Net;
using KQ4WLR.PowerStation;
using KQ4WLR.PowerStation.Model;
using KQ4WLR.PowerStation.Shelly;

namespace PowerStation.Tests;

public static class DigestTests
{
    [Test]
    public static void Ha1AndResponseMatchIndependentVector()
    {
        // Vector computed with Python hashlib, independent of this code base.
        var ha1 = ShellyDigest.ComputeHa1("shellypro4pm-f008d1d8b8b8", "mysecretpassword");
        Assert.Equal("d0e65e7f502827419960bf6e3ffa1f25a28aa292a41ccb7eefa57d10c7a8e4f7", ha1, "HA1");
        var response = ShellyDigest.ComputeResponse(ha1, "AAAAAABnTestNonce", "00000001", "313273957", "POST", "/rpc");
        Assert.Equal("e417ab12598a6bf604ba82893658c8215569bc52ff13a00892c62f9d667263b1", response, "response");
    }

    [Test]
    public static void ParsesShellyChallenge()
    {
        var c = ShellyDigest.ParseChallenge(
            "Digest qop=\"auth\", realm=\"shellypro4pm-f008d1d8b8b8\", nonce=\"AAAAAABn+/=\", algorithm=SHA-256, stale=true");
        Assert.NotNull(c);
        Assert.Equal("shellypro4pm-f008d1d8b8b8", c!.Realm, "realm");
        Assert.Equal("AAAAAABn+/=", c.Nonce, "nonce");
        Assert.Equal("SHA-256", c.Algorithm, "algorithm");
        Assert.True(c.Stale, "stale");
        Assert.Equal(null, ShellyDigest.ParseChallenge("Basic realm=\"x\""), "basic");
    }
}

public static class HostValidatorTests
{
    [Test]
    public static void NormalizesAcceptableAddresses()
    {
        foreach (var (input, expected) in new[]
                 {
                     ("192.168.1.20", "192.168.1.20"),
                     ("  10.0.20.5 ", "10.0.20.5"),
                     ("10.0.20.5:8080", "10.0.20.5:8080"),
                     ("shelly-plug.local", "shelly-plug.local"),
                     ("[fd00::12]", "[fd00::12]"),
                 })
        {
            Assert.True(HostValidator.TryNormalize(input, out var host, out var error), $"{input} rejected: {error}");
            Assert.Equal(expected, host, input);
        }
    }

    [Test]
    public static void RejectsUrlsPathsAndCredentials()
    {
        foreach (var input in new[] { "", "http://10.0.0.5", "10.0.0.5/rpc", "admin@10.0.0.5", "10.0.0.5?x=1", "a b" })
            Assert.False(HostValidator.TryNormalize(input, out _, out _), $"accepted \"{input}\"");
    }

    [Test]
    public static void OnlyLocalAddressesAreLocal()
    {
        foreach (var ok in new[] { "10.1.2.3", "172.16.0.1", "172.31.255.1", "192.168.0.10", "169.254.1.1", "fd12::1", "fe80::1", "100.64.0.1" })
            Assert.True(HostValidator.IsLocal(IPAddress.Parse(ok)), ok);
        foreach (var bad in new[] { "8.8.8.8", "172.32.0.1", "1.1.1.1", "2001:4860:4860::8888", "100.128.0.1" })
            Assert.False(HostValidator.IsLocal(IPAddress.Parse(bad)), bad);
    }

    [Test]
    public static async Task LoopbackIsRefusedOutsideTheTestSuite()
    {
        HostValidator.AllowLoopback = false;
        try
        {
            foreach (var loop in new[] { "127.0.0.1", "127.0.0.9", "::1", "::ffff:127.0.0.1" })
                Assert.False(HostValidator.IsLocal(IPAddress.Parse(loop)), loop);
            Assert.Contains("isn't a local-network address", await HostValidator.CheckLocalAsync("127.0.0.1:8080", CancellationToken.None));
            Assert.Contains("isn't a local-network address", await HostValidator.CheckLocalAsync("localhost", CancellationToken.None));
            Assert.False(HostValidator.IsLocalHost("127.0.0.1:80"), "found address on loopback");
            Assert.True(HostValidator.IsLocalHost("192.168.1.20"), "found LAN address");
            Assert.False(HostValidator.IsLocalHost("8.8.8.8"), "found public address");
            Assert.False(HostValidator.IsLocalHost("shelly.example.com"), "found names aren't accepted");
        }
        finally { HostValidator.AllowLoopback = true; }
    }

    [Test]
    public static async Task RejectsPublicAddressBeforeConnecting()
    {
        var problem = await HostValidator.CheckLocalAsync("8.8.8.8", CancellationToken.None);
        Assert.Contains("isn't a local-network address", problem);
        Assert.Equal(null, await HostValidator.CheckLocalAsync("192.168.1.20:8080", CancellationToken.None), "LAN with port");
    }
}

public static class Gen2ClientTests
{
    private static HttpClient Http() => PowerStationPlugin.CreateLanHttpClient();

    private static Task<FakeShellyGen2> Pro4Pm(string? password = null) =>
        FakeShellyGen2.StartAsync(configure: f =>
        {
            f.Password = password;
            f.Switches.Add(new FakeShellyGen2.FakeSwitch { Name = "Amplifier" });
            f.Switches.Add(new FakeShellyGen2.FakeSwitch());
            f.Lights.Add(new FakeShellyGen2.FakeLight { Name = "Desk lamp", On = true, Brightness = 40 });
        });

    [Test]
    public static async Task IdentifiesGen2Device()
    {
        await using var fake = await Pro4Pm();
        using var http = Http();
        var id = await Gen2Client.IdentifyAsync(http, fake.Host, CancellationToken.None);
        Assert.Equal(fake.DeviceId, id.DeviceId, "id");
        Assert.Equal(2, id.Generation, "gen");
        Assert.Equal("Pro4PM", id.App, "app");
        Assert.False(id.AuthRequired, "auth");
    }

    [Test]
    public static async Task IdentifiesGen1Device()
    {
        await using var fake = await FakeShellyGen2.StartAsync(gen: 1);
        using var http = Http();
        var id = await Gen2Client.IdentifyAsync(http, fake.Host, CancellationToken.None);
        Assert.Equal(1, id.Generation, "gen");
        Assert.Equal("SHPLG-S", id.Model, "model");
        Assert.Equal("shplg-s-aabbccddeeff", id.DeviceId, "id");
    }

    [Test]
    public static async Task ReadsSwitchMeteringAndLightBrightness()
    {
        await using var fake = await Pro4Pm();
        fake.Switches[0].On = true;
        using var http = Http();
        var client = new Gen2Client(http, fake.Host, null);
        var channels = await client.GetStatusAsync(CancellationToken.None);

        Assert.Equal(3, channels.Count, "channel count");
        var amp = channels.Single(c => c.Key == "switch:0");
        Assert.Equal(ChannelKind.Switch, amp.Kind, "kind");
        Assert.Equal("Amplifier", amp.Name, "name from device config");
        Assert.True(amp.On, "on");
        Assert.Equal(42.5, amp.PowerW, "watts");
        Assert.Equal(121.3, amp.VoltageV, "volts");
        Assert.Equal(0.35, amp.CurrentA, "amps");
        Assert.Equal(60.0, amp.FrequencyHz, "Hz");
        Assert.Equal(1234.5, amp.EnergyWh, "Wh");
        Assert.Equal(41.2, amp.TemperatureC, "temp");
        Assert.True(amp.Metered, "metered");

        var lamp = channels.Single(c => c.Key == "light:0");
        Assert.Equal(ChannelKind.Light, lamp.Kind, "light kind");
        Assert.Equal(40.0, lamp.Brightness, "brightness");
        Assert.False(lamp.Metered, "lamp not metered");
    }

    [Test]
    public static async Task SwitchAndLightCommandsReachDeviceWithZeusTag()
    {
        await using var fake = await Pro4Pm();
        using var http = Http();
        var client = new Gen2Client(http, fake.Host, null);

        await client.SetSwitchAsync(1, true, CancellationToken.None);
        Assert.True(fake.Switches[1].On, "switch 1 on");
        await client.ToggleAsync(ChannelKind.Switch, 1, CancellationToken.None);
        Assert.False(fake.Switches[1].On, "switch 1 toggled off");
        await client.SetLightAsync(0, null, 75, null, CancellationToken.None);
        Assert.Equal(75.0, fake.Lights[0].Brightness, "brightness");
        await client.SetLightAsync(0, false, null, null, CancellationToken.None);
        Assert.False(fake.Lights[0].On, "light off");
        await client.DimAsync(0, DimDirection.Up, CancellationToken.None);
        Assert.Equal(85.0, fake.Lights[0].Brightness, "dimmed up");

        var set = fake.Calls.First(c => c["method"]!.GetValue<string>() == "Switch.Set");
        Assert.Equal("zeus", set["params"]!["tag"]!.GetValue<string>(), "tag");
    }

    [Test]
    public static async Task FallsBackWhenFirmwareRejectsTag()
    {
        await using var fake = await Pro4Pm();
        fake.RejectTag = true;
        using var http = Http();
        var client = new Gen2Client(http, fake.Host, null);

        await client.SetSwitchAsync(0, true, CancellationToken.None);
        Assert.True(fake.Switches[0].On, "switched despite tag rejection");
        var before = fake.Calls.Count;
        await client.SetSwitchAsync(0, false, CancellationToken.None);
        Assert.Equal(before + 1, fake.Calls.Count, "second command goes straight through without tag");
        Assert.False(fake.Switches[0].On, "off");
    }

    [Test]
    public static async Task UnknownChannelSurfacesDeviceError()
    {
        await using var fake = await Pro4Pm();
        using var http = Http();
        var client = new Gen2Client(http, fake.Host, null);
        var ex = await Assert.ThrowsAsync<ShellyException>(() => client.SetSwitchAsync(9, true, CancellationToken.None));
        Assert.Equal(ShellyErrorKind.DeviceError, ex.Kind, "kind");
        Assert.Equal(-105, ex.RpcCode, "code");
    }

    [Test]
    public static async Task PasswordDeviceWithoutCredentialIsUnauthorized()
    {
        await using var fake = await Pro4Pm("hunter2");
        using var http = Http();
        var client = new Gen2Client(http, fake.Host, null);
        var ex = await Assert.ThrowsAsync<ShellyException>(() => client.GetStatusAsync(CancellationToken.None));
        Assert.Equal(ShellyErrorKind.Unauthorized, ex.Kind, "kind");
    }

    [Test]
    public static async Task WrongPasswordIsRejected()
    {
        await using var fake = await Pro4Pm("hunter2");
        using var http = Http();
        var ex = await Assert.ThrowsAsync<ShellyException>(() =>
            Gen2Client.DeriveHa1Async(http, fake.Host, "wrong", CancellationToken.None));
        Assert.Equal(ShellyErrorKind.Unauthorized, ex.Kind, "kind");
    }

    [Test]
    public static async Task CorrectPasswordAuthenticatesAndReusesNonce()
    {
        await using var fake = await Pro4Pm("hunter2");
        using var http = Http();
        var ha1 = await Gen2Client.DeriveHa1Async(http, fake.Host, "hunter2", CancellationToken.None);
        Assert.Equal(ShellyDigest.ComputeHa1(fake.DeviceId, "hunter2"), ha1, "ha1");

        var client = new Gen2Client(http, fake.Host, ha1);
        var challengesBefore = fake.ChallengesIssued;
        for (var i = 0; i < 5; i++) await client.GetStatusAsync(CancellationToken.None);
        await client.SetSwitchAsync(0, true, CancellationToken.None);
        Assert.True(fake.Switches[0].On, "authenticated command applied");
        Assert.Equal(challengesBefore + 1, fake.ChallengesIssued, "one challenge, then nonce reuse");
    }

    [Test]
    public static async Task RecoversWhenNonceExpires()
    {
        await using var fake = await Pro4Pm("hunter2");
        using var http = Http();
        var ha1 = ShellyDigest.ComputeHa1(fake.DeviceId, "hunter2");
        var client = new Gen2Client(http, fake.Host, ha1);
        await client.GetStatusAsync(CancellationToken.None);
        fake.ExpireNonces();
        var channels = await client.GetStatusAsync(CancellationToken.None);
        Assert.Equal(3, channels.Count, "status after re-challenge");
    }

    [Test]
    public static async Task OfflineDeviceIsUnreachable()
    {
        int port;
        using (var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0))
        {
            l.Start();
            port = ((IPEndPoint)l.LocalEndpoint).Port;
        }
        using var http = Http();
        var client = new Gen2Client(http, $"127.0.0.1:{port}", null);
        var ex = await Assert.ThrowsAsync<ShellyException>(() => client.GetStatusAsync(CancellationToken.None));
        Assert.Equal(ShellyErrorKind.Unreachable, ex.Kind, "kind");
    }
}
