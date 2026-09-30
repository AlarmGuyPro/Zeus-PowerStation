// SPDX-License-Identifier: GPL-2.0-or-later
using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace PowerStation.Tests;

/// <summary>
/// In-process imitation of a Shelly Gen2+ device (e.g. Pro 4PM, Dimmer Gen3):
/// serves /shelly and JSON-RPC at /rpc, including SHA-256 digest auth with
/// nonce reuse and replay protection. Its digest check is written
/// independently of the plugin's so the two can't share a bug.
/// </summary>
public sealed class FakeShellyGen2 : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ConcurrentDictionary<string, int> _nonces = new();
    private readonly object _lock = new();

    public string DeviceId { get; }
    public string Model { get; init; } = "SPSW-104PE16EU";
    public string? Password { get; set; }
    public bool RejectTag { get; set; }
    /// <summary>Raw JSON to send as the Shelly.GetConfig result (to imitate firmware quirks).</summary>
    public string? RawConfigResult { get; set; }
    public int Gen { get; init; } = 2;
    public List<FakeSwitch> Switches { get; } = [];
    public List<FakeLight> Lights { get; } = [];
    /// <summary>Plus RGBW PM in RGB or RGBW profile: one rgb:0 or rgbw:0 component.</summary>
    public List<FakeColor> Colors { get; } = [];
    public bool ColorHasWhite { get; set; }
    public List<JsonObject> Calls { get; } = [];
    /// <summary>Runs as each RPC arrives (method name), before it is handled.</summary>
    public Action<string>? OnCall { get; set; }
    public int ChallengesIssued { get; private set; }
    public string Host { get; private set; } = "";

    public sealed class FakeSwitch
    {
        public bool On;
        public string? Name;
        public double? Power = 42.5, Voltage = 121.3, Current = 0.35;
    }

    public sealed class FakeLight
    {
        public bool On;
        public double Brightness = 50;
        public string? Name;
    }

    public sealed class FakeColor
    {
        public bool On;
        public double Brightness = 100;
        public int[] Rgb = [255, 255, 255];
        public double White;
        public string? Name;
        public double? LastTransition;
    }

    private FakeShellyGen2(string deviceId, WebApplication app)
    {
        DeviceId = deviceId;
        _app = app;
    }

    public static async Task<FakeShellyGen2> StartAsync(string deviceId = "shellypro4pm-aabbccddeeff", Action<FakeShellyGen2>? configure = null, int gen = 2, string url = "http://127.0.0.1:0")
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls(url);
        var app = builder.Build();
        var fake = new FakeShellyGen2(deviceId, app) { Gen = gen };
        configure?.Invoke(fake);

        app.MapGet("/shelly", fake.HandleShelly);
        app.MapPost("/rpc", fake.HandleRpc);
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        fake.Host = new Uri(address).Authority;
        return fake;
    }

    /// <summary>Forget all issued nonces, as a rebooted device or expired nonce would.</summary>
    public void ExpireNonces() => _nonces.Clear();

    private IResult HandleShelly()
    {
        if (Gen == 1)
            return Results.Json(new JsonObject
            {
                ["type"] = "SHPLG-S", ["mac"] = "AABBCCDDEEFF", ["auth"] = Password is not null, ["fw"] = "20230913-114008/v1.14.0",
            });
        return Results.Json(new JsonObject
        {
            ["name"] = "Shack Rack", ["id"] = DeviceId, ["mac"] = "AABBCCDDEEFF", ["model"] = Model,
            ["gen"] = Gen, ["fw_id"] = "20250101-000000/1.5.0-g0000000", ["ver"] = "1.5.0",
            ["app"] = "Pro4PM", ["auth_en"] = Password is not null, ["auth_domain"] = Password is null ? null : DeviceId,
        });
    }

    private async Task HandleRpc(HttpContext http)
    {
        var body = await new StreamReader(http.Request.Body).ReadToEndAsync();
        var frame = JsonNode.Parse(body)!.AsObject();
        lock (_lock) Calls.Add(frame);
        OnCall?.Invoke(frame["method"]?.GetValue<string>() ?? "");

        if (Password is not null && !IsAuthorized(http.Request.Headers.Authorization.ToString(), out var stale))
        {
            var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(12));
            _nonces[nonce] = 0;
            ChallengesIssued++;
            http.Response.StatusCode = 401;
            http.Response.Headers.WWWAuthenticate =
                $"Digest qop=\"auth\", realm=\"{DeviceId}\", nonce=\"{nonce}\", algorithm=SHA-256" + (stale ? ", stale=true" : "");
            return;
        }

        var id = frame["id"]?.GetValue<int>() ?? 0;
        var method = frame["method"]!.GetValue<string>();
        if (method == "Shelly.GetConfig" && RawConfigResult is not null)
        {
            http.Response.ContentType = "application/json";
            await http.Response.WriteAsync($"{{\"id\":{id},\"src\":\"{DeviceId}\",\"result\":{RawConfigResult}}}");
            return;
        }
        var p = frame["params"] as JsonObject ?? new JsonObject();
        JsonNode? result;
        try
        {
            result = Dispatch(method, p);
        }
        catch (RpcError e)
        {
            http.Response.StatusCode = 500;
            await http.Response.WriteAsJsonAsync(new JsonObject
            {
                ["id"] = id, ["src"] = DeviceId,
                ["error"] = new JsonObject { ["code"] = e.Code, ["message"] = e.Message },
            });
            return;
        }
        await http.Response.WriteAsJsonAsync(new JsonObject { ["id"] = id, ["src"] = DeviceId, ["result"] = result });
    }

    private sealed class RpcError(int code, string message) : Exception(message)
    {
        public int Code { get; } = code;
    }

    private JsonNode? Dispatch(string method, JsonObject p)
    {
        if (RejectTag && p.ContainsKey("tag")) throw new RpcError(-103, "Invalid argument 'tag'!");
        int Id() => p["id"]?.GetValue<int>() ?? throw new RpcError(-103, "Missing required argument 'id'!");
        lock (_lock)
        {
            switch (method)
            {
                case "Shelly.GetDeviceInfo":
                    return new JsonObject { ["id"] = DeviceId, ["gen"] = Gen };
                case "Sys.GetStatus":
                    return new JsonObject { ["uptime"] = 1234 };
                case "Shelly.GetConfig":
                {
                    var cfg = new JsonObject();
                    for (var i = 0; i < Switches.Count; i++) cfg[$"switch:{i}"] = new JsonObject { ["id"] = i, ["name"] = Switches[i].Name };
                    for (var i = 0; i < Lights.Count; i++) cfg[$"light:{i}"] = new JsonObject { ["id"] = i, ["name"] = Lights[i].Name };
                    for (var i = 0; i < Colors.Count; i++) cfg[$"{ColorPrefix}:{i}"] = new JsonObject { ["id"] = i, ["name"] = Colors[i].Name };
                    return cfg;
                }
                case "Shelly.GetStatus":
                {
                    var st = new JsonObject { ["sys"] = new JsonObject { ["uptime"] = 1234 } };
                    for (var i = 0; i < Switches.Count; i++)
                    {
                        var s = Switches[i];
                        st[$"switch:{i}"] = new JsonObject
                        {
                            ["id"] = i, ["source"] = "WS_in", ["output"] = s.On,
                            ["apower"] = s.On ? s.Power : 0, ["voltage"] = s.Voltage, ["current"] = s.On ? s.Current : 0,
                            ["pf"] = 0.97, ["freq"] = 60.0,
                            ["aenergy"] = new JsonObject { ["total"] = 1234.5 },
                            ["temperature"] = new JsonObject { ["tC"] = 41.2, ["tF"] = 106.2 },
                        };
                    }
                    for (var i = 0; i < Lights.Count; i++)
                    {
                        st[$"light:{i}"] = new JsonObject
                        {
                            ["id"] = i, ["source"] = "WS_in", ["output"] = Lights[i].On, ["brightness"] = Lights[i].Brightness,
                        };
                    }
                    for (var i = 0; i < Colors.Count; i++)
                    {
                        var c = Colors[i];
                        var o = new JsonObject
                        {
                            ["id"] = i, ["source"] = "WS_in", ["output"] = c.On, ["brightness"] = c.Brightness,
                            ["rgb"] = new JsonArray(c.Rgb.Select(v => (JsonNode)v).ToArray()),
                            ["apower"] = c.On ? 9.6 : 0, ["voltage"] = 24.1, ["current"] = c.On ? 0.4 : 0,
                        };
                        if (ColorHasWhite) o["white"] = c.White;
                        st[$"{ColorPrefix}:{i}"] = o;
                    }
                    return st;
                }
                case "RGB.Set" or "RGBW.Set":
                {
                    if ((method == "RGBW.Set") != ColorHasWhite) throw new RpcError(-114, $"Method {method} failed: No such method!");
                    if (p["on"] is null && p["brightness"] is null) throw new RpcError(-103, "Missing required argument 'on' or 'brightness'!");
                    var c = Color(Id());
                    if (p["on"] is JsonValue on) c.On = on.GetValue<bool>();
                    if (p["brightness"] is JsonValue b) c.Brightness = b.GetValue<double>();
                    if (p["rgb"] is JsonArray rgb) c.Rgb = rgb.Select(v => v!.GetValue<int>()).ToArray();
                    if (p["white"] is JsonValue w) c.White = w.GetValue<double>();
                    c.LastTransition = p["transition_duration"]?.GetValue<double>();
                    return null;
                }
                case "RGB.Toggle" or "RGBW.Toggle":
                    Color(Id()).On ^= true;
                    return null;
                case "Switch.Set":
                {
                    var s = Switch(Id());
                    var was = s.On;
                    s.On = p["on"]!.GetValue<bool>();
                    return new JsonObject { ["was_on"] = was };
                }
                case "Switch.Toggle":
                {
                    var s = Switch(Id());
                    var was = s.On;
                    s.On = !s.On;
                    return new JsonObject { ["was_on"] = was };
                }
                case "Light.Set":
                {
                    var l = Light(Id());
                    if (p["on"] is JsonValue on) l.On = on.GetValue<bool>();
                    if (p["brightness"] is JsonValue b) l.Brightness = b.GetValue<double>();
                    return null;
                }
                case "Light.Toggle":
                    Light(Id()).On ^= true;
                    return null;
                case "Light.DimUp":
                    Light(Id()).Brightness = Math.Min(100, Light(Id()).Brightness + 10);
                    return null;
                case "Light.DimDown":
                    Light(Id()).Brightness = Math.Max(1, Light(Id()).Brightness - 10);
                    return null;
                case "Light.DimStop":
                    Light(Id());
                    return null;
                default:
                    throw new RpcError(-114, $"Method {method} failed: No such method!");
            }
        }
    }

    private FakeSwitch Switch(int id) => id >= 0 && id < Switches.Count ? Switches[id] : throw new RpcError(-105, $"Argument 'id', value {id} not found!");
    private string ColorPrefix => ColorHasWhite ? "rgbw" : "rgb";
    private FakeColor Color(int id) => id >= 0 && id < Colors.Count ? Colors[id] : throw new RpcError(-105, $"Argument 'id', value {id} not found!");
    private FakeLight Light(int id) => id >= 0 && id < Lights.Count ? Lights[id] : throw new RpcError(-105, $"Argument 'id', value {id} not found!");

    private bool IsAuthorized(string header, out bool stale)
    {
        stale = false;
        if (!header.StartsWith("Digest ", StringComparison.Ordinal)) return false;
        var f = new Dictionary<string, string>();
        foreach (var part in header[7..].Split(','))
        {
            var kv = part.Trim().Split('=', 2);
            if (kv.Length == 2) f[kv[0]] = kv[1].Trim('"');
        }
        if (!f.TryGetValue("nonce", out var nonce) || !_nonces.TryGetValue(nonce, out var lastNc))
        {
            stale = f.ContainsKey("nonce");
            return false;
        }
        if (f.GetValueOrDefault("username") != "admin" || f.GetValueOrDefault("realm") != DeviceId ||
            f.GetValueOrDefault("algorithm") != "SHA-256" || f.GetValueOrDefault("qop") != "auth") return false;
        var nc = Convert.ToInt32(f["nc"], 16);
        if (nc <= lastNc) return false; // replay protection, as firmware 2.0+ does
        static string H(string s) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
        var ha1 = H($"admin:{DeviceId}:{Password}");
        var ha2 = H($"POST:{f["uri"]}");
        var expected = H($"{ha1}:{nonce}:{f["nc"]}:{f["cnonce"]}:auth:{ha2}");
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(f["response"]))) return false;
        _nonces[nonce] = nc;
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
