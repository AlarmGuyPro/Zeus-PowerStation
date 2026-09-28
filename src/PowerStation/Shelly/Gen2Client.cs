// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KQ4WLR.PowerStation.Model;
using KQ4WLR.PowerStation.Services;

namespace KQ4WLR.PowerStation.Shelly;

/// <summary>Operations every supported Shelly generation offers PowerStation.</summary>
public interface IShellyClient
{
    Task<IReadOnlyList<ChannelState>> GetStatusAsync(CancellationToken ct);
    Task SetSwitchAsync(int index, bool on, CancellationToken ct);
    Task ToggleAsync(ChannelKind kind, int index, CancellationToken ct);
    Task SetLightAsync(int index, bool? on, double? brightness, double? transitionSeconds, CancellationToken ct);
    Task DimAsync(int index, DimDirection direction, CancellationToken ct);

    /// <summary>
    /// Turns the output on (or keeps it on) with a device-side flip-back timer:
    /// the device itself switches it off after <paramref name="seconds"/>
    /// unless this is called again first.
    /// </summary>
    Task SetSafetyTimerAsync(ChannelKind kind, int index, int seconds, CancellationToken ct);

    /// <summary>Colour lights: any of on/off, level, colour and white, with an optional fade.</summary>
    Task SetColorAsync(ChannelKind kind, int index, bool? on, double? brightness, int[]? rgb, double? white,
        double? transitionSeconds, CancellationToken ct);
}

public enum DimDirection { Up, Down, Stop }

/// <summary>
/// Client for Shelly Gen2, Gen3, Gen4 and "Powered by Shelly" devices, which
/// share the JSON-RPC API at <c>http://&lt;host&gt;/rpc</c>. One instance per
/// device; it caches the digest nonce so authenticated devices don't need a
/// 401 round trip on every poll (firmware 2.0+ allows nonce reuse).
/// </summary>
public sealed class Gen2Client : IShellyClient
{
    /// <summary>Marks state changes as coming from Zeus in the device's status and logs.</summary>
    public const string ChangeTag = "zeus";
    private const string RpcPath = "/rpc";

    private readonly HttpClient _http;
    private readonly string _host;
    private readonly string? _ha1;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DigestChallenge? _challenge;
    private int _nonceCount;
    private int _requestId;
    private bool _tagUnsupported;
    private Dictionary<string, string> _deviceNames = new();
    private DateTimeOffset _namesFetchedAt = DateTimeOffset.MinValue;

    private readonly TrafficLog? _log;
    private readonly string? _deviceId;

    public Gen2Client(HttpClient http, string host, string? ha1, TrafficLog? log = null, string? deviceId = null)
    {
        _http = http;
        _host = host;
        _ha1 = string.IsNullOrEmpty(ha1) ? null : ha1;
        _log = log;
        _deviceId = deviceId;
    }

    /// <summary>How long channel names read from the device are cached.</summary>
    public static TimeSpan NameRefreshInterval { get; set; } = TimeSpan.FromMinutes(1);

    // ---------------------------------------------------------------- identity

    /// <summary>
    /// Reads the unauthenticated <c>/shelly</c> endpoint, which every Shelly
    /// generation serves even when authentication is enabled.
    /// </summary>
    public static async Task<DeviceIdentity> IdentifyAsync(HttpClient http, string host, CancellationToken ct, TrafficLog? log = null)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        int? status = null;
        string? body = null;
        try
        {
            var id = await IdentifyCoreAsync(http, host, ct, (s, b) => { status = s; body = b; }).ConfigureAwait(false);
            log?.Add("http", id.DeviceId, host, "/shelly", null, status, body, null, System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return id;
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            log?.Add("http", null, host, "/shelly", null, status, body, ex.Message, System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            throw;
        }
    }

    private static async Task<DeviceIdentity> IdentifyCoreAsync(HttpClient http, string host, CancellationToken ct, Action<int, string?> seen)
    {
        JsonObject info;
        try
        {
            using var response = await http.GetAsync(new Uri($"http://{host}/shelly"), ct).ConfigureAwait(false);
            seen((int)response.StatusCode, null);
            if (!response.IsSuccessStatusCode)
                throw new ShellyException(ShellyErrorKind.Protocol,
                    $"{host} answered HTTP {(int)response.StatusCode} on /shelly. Is this a Shelly device?");
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            seen((int)response.StatusCode, body);
            info = JsonNode.Parse(body) as JsonObject
                   ?? throw new ShellyException(ShellyErrorKind.Protocol, $"{host} didn't return Shelly device info.");
        }
        catch (ShellyException) { throw; }
        catch (JsonException ex)
        {
            throw new ShellyException(ShellyErrorKind.Protocol, $"{host} didn't return Shelly device info.", inner: ex);
        }
        catch (Exception ex) when (IsNetworkFailure(ex, ct))
        {
            throw Unreachable(host, ex);
        }

        var gen = GetInt(info, "gen");
        if (gen is >= 2)
        {
            var id = GetString(info, "id")
                     ?? throw new ShellyException(ShellyErrorKind.Protocol, $"{host} didn't report a device id.");
            return new DeviceIdentity
            {
                DeviceId = id,
                Generation = gen.Value,
                Model = GetString(info, "model"),
                App = GetString(info, "app"),
                Mac = GetString(info, "mac"),
                Firmware = GetString(info, "ver") ?? GetString(info, "fw_id"),
                AuthRequired = GetBool(info, "auth_en") ?? false,
                DefaultName = GetString(info, "name"),
            };
        }

        var type = GetString(info, "type");
        if (type is not null)
        {
            var mac = GetString(info, "mac");
            return new DeviceIdentity
            {
                DeviceId = mac is null ? type.ToLowerInvariant() : $"{type.ToLowerInvariant()}-{mac.ToLowerInvariant()}",
                Generation = 1,
                Model = type,
                App = Gen1Client.AppFor(type),
                Mac = mac,
                Firmware = GetString(info, "fw"),
                AuthRequired = GetBool(info, "auth") ?? false,
            };
        }

        throw new ShellyException(ShellyErrorKind.Protocol, $"{host} answered, but not like a Shelly device.");
    }

    /// <summary>
    /// Confirms a password against the device and returns the HA1 to store.
    /// The realm comes from the device's own challenge, so the stored HA1
    /// always matches what the firmware expects.
    /// </summary>
    public static async Task<string> DeriveHa1Async(HttpClient http, string host, string password, CancellationToken ct, TrafficLog? log = null)
    {
        var challenge = await GetChallengeAsync(http, host, ct).ConfigureAwait(false)
            ?? throw new ShellyException(ShellyErrorKind.Protocol,
                "The device didn't ask for a password, so there is nothing to check.");
        var ha1 = ShellyDigest.ComputeHa1(challenge.Realm, password);
        var probe = new Gen2Client(http, host, ha1, log);
        await probe.CallAsync("Shelly.GetDeviceInfo", null, ct).ConfigureAwait(false);
        await probe.CallAsync("Sys.GetStatus", null, ct).ConfigureAwait(false);
        return ha1;
    }

    private static async Task<DigestChallenge?> GetChallengeAsync(HttpClient http, string host, CancellationToken ct)
    {
        try
        {
            using var request = BuildRequest(host, "Sys.GetStatus", null, 1);
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.Unauthorized) return null;
            return ParseChallenge(response);
        }
        catch (Exception ex) when (IsNetworkFailure(ex, ct))
        {
            throw Unreachable(host, ex);
        }
    }

    // ---------------------------------------------------------------- control

    public async Task<IReadOnlyList<ChannelState>> GetStatusAsync(CancellationToken ct)
    {
        if (DateTimeOffset.UtcNow - _namesFetchedAt > NameRefreshInterval)
        {
            try
            {
                var config = await CallAsync("Shelly.GetConfig", null, ct).ConfigureAwait(false) as JsonObject;
                _deviceNames = ParseNames(config);
            }
            catch (ShellyException ex) when (ex.Kind == ShellyErrorKind.DeviceError)
            {
                // Names are cosmetic; keep polling without them.
            }
            _namesFetchedAt = DateTimeOffset.UtcNow;
        }

        var status = await CallAsync("Shelly.GetStatus", null, ct).ConfigureAwait(false) as JsonObject
                     ?? throw new ShellyException(ShellyErrorKind.Protocol, "Shelly.GetStatus returned no data.");
        return ParseChannels(status, _deviceNames);
    }

    public Task SetSwitchAsync(int index, bool on, CancellationToken ct) =>
        CallTaggedAsync("Switch.Set", new JsonObject { ["id"] = index, ["on"] = on }, ct);

    public Task ToggleAsync(ChannelKind kind, int index, CancellationToken ct) =>
        CallTaggedAsync($"{kind.RpcName()}.Toggle",
            new JsonObject { ["id"] = index }, ct);

    public Task SetLightAsync(int index, bool? on, double? brightness, double? transitionSeconds, CancellationToken ct)
    {
        if (on is null && brightness is null)
            throw new ArgumentException("Light.Set needs on and/or brightness.");
        var p = new JsonObject { ["id"] = index };
        if (on is not null) p["on"] = on.Value;
        if (brightness is not null) p["brightness"] = Math.Clamp(Math.Round(brightness.Value), 0, 100);
        if (transitionSeconds is > 0) p["transition_duration"] = transitionSeconds.Value;
        return CallTaggedAsync("Light.Set", p, ct);
    }

    public Task SetSafetyTimerAsync(ChannelKind kind, int index, int seconds, CancellationToken ct) =>
        CallTaggedAsync($"{kind.RpcName()}.Set",
            new JsonObject { ["id"] = index, ["on"] = true, ["toggle_after"] = Math.Max(1, seconds) }, ct);

    public Task SetColorAsync(ChannelKind kind, int index, bool? on, double? brightness, int[]? rgb, double? white,
        double? transitionSeconds, CancellationToken ct)
    {
        if (!kind.IsColor()) throw new ArgumentException("Not a colour light.", nameof(kind));
        var p = new JsonObject { ["id"] = index };
        if (on is not null) p["on"] = on.Value;
        if (brightness is not null) p["brightness"] = Math.Clamp(Math.Round(brightness.Value), 1, 100);
        if (rgb is { Length: 3 }) p["rgb"] = new JsonArray(rgb.Select(v => (JsonNode)Math.Clamp(v, 0, 255)).ToArray());
        if (white is not null && kind == ChannelKind.Rgbw) p["white"] = Math.Clamp(Math.Round(white.Value), 0, 255);
        if (transitionSeconds is > 0) p["transition_duration"] = transitionSeconds.Value;
        // The device needs on or brightness in every Set; a colour change alone turns it on.
        if (on is null && brightness is null) p["on"] = true;
        return CallTaggedAsync($"{kind.RpcName()}.Set", p, ct);
    }

    public Task DimAsync(int index, DimDirection direction, CancellationToken ct) =>
        CallAsync(direction switch
        {
            DimDirection.Up => "Light.DimUp",
            DimDirection.Down => "Light.DimDown",
            _ => "Light.DimStop",
        }, new JsonObject { ["id"] = index }, ct);

    /// <summary>
    /// Adds the Zeus change tag. Older firmware rejects unknown parameters
    /// with -103 (invalid argument); in that case we retry without the tag
    /// and stop sending it to this device.
    /// </summary>
    private async Task CallTaggedAsync(string method, JsonObject parameters, CancellationToken ct)
    {
        if (!_tagUnsupported)
        {
            var tagged = (JsonObject)parameters.DeepClone();
            tagged["tag"] = ChangeTag;
            try
            {
                await CallAsync(method, tagged, ct).ConfigureAwait(false);
                return;
            }
            catch (ShellyException ex) when (ex.Kind == ShellyErrorKind.DeviceError && ex.RpcCode == -103)
            {
                _tagUnsupported = true;
            }
        }
        await CallAsync(method, parameters, ct).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- transport

    /// <summary>What came back, for the traffic log.</summary>
    private sealed class Seen { public int? Status; public string? Body; }

    internal async Task<JsonNode?> CallAsync(string method, JsonObject? parameters, CancellationToken ct)
    {
        if (_log is null) return await CallCoreAsync(method, parameters, ct, new Seen()).ConfigureAwait(false);
        var seen = new Seen();
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            var result = await CallCoreAsync(method, parameters, ct, seen).ConfigureAwait(false);
            _log.Add("rpc", _deviceId, _host, method, parameters?.ToJsonString(), seen.Status, seen.Body, null,
                System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return result;
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            _log.Add("rpc", _deviceId, _host, method, parameters?.ToJsonString(), seen.Status, seen.Body,
                $"{(ex is ShellyException se ? se.Kind.ToString() : ex.GetType().Name)}: {ex.Message}",
                System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            throw;
        }
    }

    private async Task<JsonNode?> CallCoreAsync(string method, JsonObject? parameters, CancellationToken ct, Seen seen)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var id = Interlocked.Increment(ref _requestId);
            for (var attempt = 0; attempt < 2; attempt++)
            {
                using var request = BuildRequest(_host, method, parameters, id);
                if (_ha1 is not null && _challenge is not null)
                {
                    _nonceCount++;
                    request.Headers.TryAddWithoutValidation("Authorization",
                        ShellyDigest.BuildAuthorizationHeader(_ha1, _challenge, _nonceCount, "POST", RpcPath));
                }

                HttpResponseMessage response;
                try
                {
                    response = await _http.SendAsync(request, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (IsNetworkFailure(ex, ct))
                {
                    throw Unreachable(_host, ex);
                }

                using (response)
                {
                    seen.Status = (int)response.StatusCode;
                    if (response.StatusCode == HttpStatusCode.Unauthorized)
                    {
                        if (_ha1 is null)
                            throw new ShellyException(ShellyErrorKind.Unauthorized,
                                "This device has a password set. Enter it in PowerStation Devices.");
                        // First contact, or the cached nonce expired / was evicted
                        // (stale=true): take the fresh nonce and retry exactly once.
                        var challenge = ParseChallenge(response);
                        _challenge = challenge;
                        _nonceCount = 0;
                        if (challenge is not null && attempt == 0)
                            continue;
                        throw new ShellyException(ShellyErrorKind.Unauthorized,
                            "The device rejected the saved password.");
                    }
                    if (response.StatusCode == HttpStatusCode.TooManyRequests)
                        throw new ShellyException(ShellyErrorKind.Throttled,
                            "The device is rate-limiting requests (too many failed logins?). Try again shortly.");

                    var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    seen.Body = body;
                    return ParseRpcResponse(method, response.StatusCode, body);
                }
            }
            throw new ShellyException(ShellyErrorKind.Unauthorized, "The device rejected the saved password.");
        }
        finally
        {
            _gate.Release();
        }
    }

    private static HttpRequestMessage BuildRequest(string host, string method, JsonObject? parameters, int id)
    {
        var frame = new JsonObject { ["id"] = id, ["src"] = "powerstation", ["method"] = method };
        if (parameters is not null) frame["params"] = parameters.DeepClone();
        return new HttpRequestMessage(HttpMethod.Post, new Uri($"http://{host}{RpcPath}"))
        {
            Content = new StringContent(frame.ToJsonString(), Encoding.UTF8, "application/json"),
        };
    }

    private static DigestChallenge? ParseChallenge(HttpResponseMessage response)
    {
        foreach (var header in response.Headers.WwwAuthenticate)
        {
            var challenge = ShellyDigest.ParseChallenge(header.ToString());
            if (challenge is not null) return challenge;
        }
        return null;
    }

    private static JsonNode? ParseRpcResponse(string method, HttpStatusCode status, string body)
    {
        JsonObject? frame;
        try { frame = JsonNode.Parse(body) as JsonObject; }
        catch (JsonException ex)
        {
            throw new ShellyException(ShellyErrorKind.Protocol,
                $"{method}: device returned HTTP {(int)status} with unreadable data.", inner: ex);
        }
        if (frame is null)
            throw new ShellyException(ShellyErrorKind.Protocol, $"{method}: device returned HTTP {(int)status}.");

        if (frame["error"] is JsonObject error)
        {
            var code = GetInt(error, "code");
            var message = GetString(error, "message") ?? "unknown error";
            if (code == 401)
                throw new ShellyException(ShellyErrorKind.Unauthorized, "The device rejected the saved password.", code);
            throw new ShellyException(ShellyErrorKind.DeviceError, $"{method}: {message}", code);
        }
        if (frame.ContainsKey("result")) return frame["result"];
        // Some firmware answers GET-style calls with the bare result object.
        if (!frame.ContainsKey("id") && (int)status < 300) return frame;
        if ((int)status >= 300)
            throw new ShellyException(ShellyErrorKind.Protocol, $"{method}: device returned HTTP {(int)status}.");
        return null;
    }

    // ---------------------------------------------------------------- parsing

    internal static IReadOnlyList<ChannelState> ParseChannels(JsonObject status, IReadOnlyDictionary<string, string> names)
    {
        var channels = new List<ChannelState>();
        foreach (var (key, node) in status)
        {
            if (node is not JsonObject c) continue;
            var colon = key.IndexOf(':');
            if (colon <= 0) continue;
            var prefix = key[..colon];
            ChannelKind kind;
            if (prefix == "switch") kind = ChannelKind.Switch;
            else if (prefix == "light") kind = ChannelKind.Light;
            else if (prefix == "rgb") kind = ChannelKind.Rgb;
            else if (prefix == "rgbw") kind = ChannelKind.Rgbw;
            else continue;
            if (!int.TryParse(key[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var index)) continue;

            channels.Add(new ChannelState
            {
                Key = key,
                Kind = kind,
                Index = index,
                Name = names.TryGetValue(key, out var n) ? n : null,
                On = GetBool(c, "output") ?? false,
                Brightness = kind.IsDimmable() ? GetDouble(c, "brightness") : null,
                Rgb = kind.IsColor() && c["rgb"] is JsonArray rgb && rgb.Count == 3
                    ? rgb.Select(v => v is JsonValue jv && jv.TryGetValue<double>(out var d) ? (int)Math.Round(d) : 0).ToArray()
                    : null,
                White = kind == ChannelKind.Rgbw ? GetDouble(c, "white") : null,
                PowerW = GetDouble(c, "apower"),
                VoltageV = GetDouble(c, "voltage"),
                CurrentA = GetDouble(c, "current"),
                PowerFactor = GetDouble(c, "pf"),
                FrequencyHz = GetDouble(c, "freq"),
                EnergyWh = c["aenergy"] is JsonObject e ? GetDouble(e, "total") : null,
                TemperatureC = c["temperature"] is JsonObject t ? GetDouble(t, "tC") : null,
                Source = GetString(c, "source"),
                Errors = GetStrings(c, "errors"),
                Flags = GetStrings(c, "flags"),
                TimerEndsAt = GetDouble(c, "timer_started_at") is { } started && GetDouble(c, "timer_duration") is { } duration
                    ? DateTimeOffset.FromUnixTimeMilliseconds((long)((started + duration) * 1000))
                    : null,
            });
        }
        channels.Sort((a, b) =>
        {
            var byKind = a.Kind.CompareTo(b.Kind);
            return byKind != 0 ? byKind : a.Index.CompareTo(b.Index);
        });
        return channels;
    }

    internal static Dictionary<string, string> ParseNames(JsonObject? config)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        if (config is null) return names;
        foreach (var (key, node) in config)
        {
            if (!(key.StartsWith("switch:", StringComparison.Ordinal) || key.StartsWith("light:", StringComparison.Ordinal) ||
                  key.StartsWith("rgb:", StringComparison.Ordinal) || key.StartsWith("rgbw:", StringComparison.Ordinal))) continue;
            if (node is JsonObject c && GetString(c, "name") is { Length: > 0 } name) names[key] = name;
        }
        return names;
    }

    internal static string? GetString(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    internal static bool? GetBool(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    internal static int? GetInt(JsonObject o, string key)
    {
        if (o[key] is not JsonValue v) return null;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<double>(out var d) && d == Math.Floor(d) && d is >= int.MinValue and <= int.MaxValue) return (int)d;
        return null;
    }

    internal static double? GetDouble(JsonObject o, string key)
    {
        if (o[key] is not JsonValue v) return null;
        if (v.TryGetValue<double>(out var d)) return d;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<long>(out var l)) return l;
        return null;
    }

    internal static IReadOnlyList<string> GetStrings(JsonObject o, string key) =>
        o[key] is JsonArray a
            ? a.Select(x => x is JsonValue v && v.TryGetValue<string>(out var s) ? s : null)
               .Where(s => s is not null).Cast<string>().ToArray()
            : [];

    internal static bool IsNetworkFailure(Exception ex, CancellationToken ct) =>
        ex is HttpRequestException ||
        (ex is TaskCanceledException && !ct.IsCancellationRequested) ||
        ex is System.Net.Sockets.SocketException ||
        ex is IOException;

    internal static ShellyException Unreachable(string host, Exception ex) =>
        new(ShellyErrorKind.Unreachable,
            ex is TaskCanceledException
                ? $"{host} didn't answer in time. Check the address and that this computer can reach that network."
                : $"Couldn't connect to {host}: {ex.Message}",
            inner: ex);
}
