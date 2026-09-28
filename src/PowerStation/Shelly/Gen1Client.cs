// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KQ4WLR.PowerStation.Model;
using static KQ4WLR.PowerStation.Shelly.Gen2Client;

namespace KQ4WLR.PowerStation.Shelly;

/// <summary>
/// Client for first-generation Shelly devices (Shelly 1, 1PM, 2.5 in relay
/// mode, Plug / Plug S, Dimmer 1/2, ShellyEM), which use a plain HTTP GET
/// API: <c>/status</c>, <c>/settings</c>, <c>/relay/N</c>, <c>/light/N</c>.
/// Authentication, when enabled on the device, is HTTP Basic.
/// </summary>
public sealed class Gen1Client : IShellyClient
{
    private readonly HttpClient _http;
    private readonly string _host;
    private readonly AuthenticationHeaderValue? _auth;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<string, string> _names = new();
    private DateTimeOffset _namesFetchedAt = DateTimeOffset.MinValue;

    public Gen1Client(HttpClient http, string host, string? user, string? password)
    {
        _http = http;
        _host = host;
        if (!string.IsNullOrEmpty(password))
            _auth = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{(string.IsNullOrEmpty(user) ? "admin" : user)}:{password}")));
    }

    /// <summary>Friendly model names for the Gen1 <c>type</c> codes PowerStation knows.</summary>
    public static string? AppFor(string? type) => type?.ToUpperInvariant() switch
    {
        "SHSW-1" => "Shelly1",
        "SHSW-L" => "Shelly1L",
        "SHSW-PM" => "Shelly1PM",
        "SHSW-25" => "Shelly2.5",
        "SHSW-21" => "Shelly2",
        "SHPLG-1" => "Plug",
        "SHPLG2-1" => "Plug",
        "SHPLG-S" => "PlugS",
        "SHPLG-U1" => "PlugUS",
        "SHDM-1" => "Dimmer",
        "SHDM-2" => "Dimmer2",
        "SHEM" => "ShellyEM",
        "SHEM-3" => "Shelly3EM",
        _ => null,
    };

    /// <summary>Checks the credentials against the device. Throws Unauthorized if they're wrong.</summary>
    public static async Task VerifyAsync(HttpClient http, string host, string? user, string password, CancellationToken ct)
    {
        var client = new Gen1Client(http, host, user, password);
        await client.GetJsonAsync("/settings", ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ChannelState>> GetStatusAsync(CancellationToken ct)
    {
        if (DateTimeOffset.UtcNow - _namesFetchedAt > NameRefreshInterval)
        {
            try { _names = ParseNames(await GetJsonAsync("/settings", ct).ConfigureAwait(false)); }
            catch (ShellyException ex) when (ex.Kind is ShellyErrorKind.Protocol or ShellyErrorKind.DeviceError) { }
            _namesFetchedAt = DateTimeOffset.UtcNow;
        }
        var status = await GetJsonAsync("/status", ct).ConfigureAwait(false);
        return ParseStatus(status, _names, DateTimeOffset.UtcNow);
    }

    public Task SetSwitchAsync(int index, bool on, CancellationToken ct) =>
        GetJsonAsync($"/relay/{index}?turn={(on ? "on" : "off")}", ct);

    public Task ToggleAsync(ChannelKind kind, int index, CancellationToken ct) =>
        GetJsonAsync($"/{Path(kind)}/{index}?turn=toggle", ct);

    public Task SetLightAsync(int index, bool? on, double? brightness, double? transitionSeconds, CancellationToken ct)
    {
        if (on is null && brightness is null)
            throw new ArgumentException("A light command needs on and/or brightness.");
        var q = new List<string>();
        if (on is not null) q.Add($"turn={(on.Value ? "on" : "off")}");
        if (brightness is not null)
            q.Add($"brightness={Math.Clamp(Math.Round(brightness.Value), 1, 100).ToString(CultureInfo.InvariantCulture)}");
        // Dimmer 2 accepts a transition in milliseconds, up to 5 s.
        if (transitionSeconds is > 0)
            q.Add($"transition={Math.Min(5000, (int)Math.Round(transitionSeconds.Value * 1000)).ToString(CultureInfo.InvariantCulture)}");
        return GetJsonAsync($"/light/{index}?{string.Join('&', q)}", ct);
    }

    public Task DimAsync(int index, DimDirection direction, CancellationToken ct) =>
        GetJsonAsync($"/light/{index}?dim={direction.ToString().ToLowerInvariant()}&step=10", ct);

    public Task SetSafetyTimerAsync(ChannelKind kind, int index, int seconds, CancellationToken ct) =>
        GetJsonAsync($"/{Path(kind)}/{index}?turn=on&timer={Math.Max(1, seconds).ToString(CultureInfo.InvariantCulture)}", ct);

    public Task SetColorAsync(ChannelKind kind, int index, bool? on, double? brightness, int[]? rgb, double? white,
        double? transitionSeconds, CancellationToken ct) =>
        throw new ShellyException(ShellyErrorKind.Unsupported, "Colour control isn't supported on Gen1 devices yet.");

    private static string Path(ChannelKind kind) => kind switch
    {
        ChannelKind.Light => "light",
        ChannelKind.Switch => "relay",
        _ => throw new ShellyException(ShellyErrorKind.Unsupported, "Meters can't be switched."),
    };

    // ---------------------------------------------------------------- transport

    internal async Task<JsonObject> GetJsonAsync(string pathAndQuery, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"http://{_host}{pathAndQuery}"));
            if (_auth is not null) request.Headers.Authorization = _auth;
            HttpResponseMessage response;
            try { response = await _http.SendAsync(request, ct).ConfigureAwait(false); }
            catch (Exception ex) when (IsNetworkFailure(ex, ct)) { throw Unreachable(_host, ex); }
            using (response)
            {
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                    throw new ShellyException(ShellyErrorKind.Unauthorized, _auth is null
                        ? "This device has a password set. Enter it in PowerStation setup."
                        : "The device rejected the saved user name or password.");
                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    throw new ShellyException(ShellyErrorKind.DeviceError,
                        $"{pathAndQuery.Split('?')[0]}: device answered HTTP {(int)response.StatusCode}{(body.Length is > 0 and < 200 ? $" ({body.Trim()})" : "")}.");
                try
                {
                    return JsonNode.Parse(body) as JsonObject
                           ?? throw new ShellyException(ShellyErrorKind.Protocol, "The device returned no data.");
                }
                catch (JsonException ex)
                {
                    throw new ShellyException(ShellyErrorKind.Protocol, "The device returned unreadable data.", inner: ex);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    // ---------------------------------------------------------------- parsing

    internal static Dictionary<string, string> ParseNames(JsonObject settings)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        void Take(string array, string prefix)
        {
            if (settings[array] is not JsonArray a) return;
            for (var i = 0; i < a.Count; i++)
                if (a[i] is JsonObject o && GetString(o, "name") is { Length: > 0 } n) names[$"{prefix}:{i}"] = n;
        }
        Take("relays", "switch");
        Take("lights", "light");
        Take("emeters", "emeter");
        return names;
    }

    internal static IReadOnlyList<ChannelState> ParseStatus(JsonObject status, IReadOnlyDictionary<string, string> names, DateTimeOffset now)
    {
        var channels = new List<ChannelState>();
        var meters = status["meters"] as JsonArray;
        var deviceErrors = new List<string>();
        if (GetBool(status, "overtemperature") == true) deviceErrors.Add("overtemp");
        var temperature = GetDouble(status, "temperature") ?? (status["tmp"] is JsonObject tmp ? GetDouble(tmp, "tC") : null);
        // The 2.5 reports one supply voltage for the device.
        var deviceVoltage = GetDouble(status, "voltage");

        if (status["relays"] is JsonArray relays)
            for (var i = 0; i < relays.Count; i++)
            {
                if (relays[i] is not JsonObject r) continue;
                var errors = new List<string>(deviceErrors);
                if (GetBool(r, "overpower") == true) errors.Add("overpower");
                var meter = meters is not null && i < meters.Count ? meters[i] as JsonObject : null;
                channels.Add(Channel($"switch:{i}", ChannelKind.Switch, i, GetBool(r, "ison") ?? false, null, r, meter,
                    deviceVoltage, temperature, errors, names, now));
            }

        if (status["lights"] is JsonArray lights)
            for (var i = 0; i < lights.Count; i++)
            {
                if (lights[i] is not JsonObject l) continue;
                var errors = new List<string>(deviceErrors);
                if (GetBool(status, "overload") == true || GetBool(l, "overpower") == true) errors.Add("overpower");
                if (GetBool(status, "loaderror") == true) errors.Add("unsupported_load");
                var meter = meters is not null && i < meters.Count ? meters[i] as JsonObject : null;
                channels.Add(Channel($"light:{i}", ChannelKind.Light, i, GetBool(l, "ison") ?? false, GetDouble(l, "brightness"),
                    l, meter, deviceVoltage, temperature, errors, names, now));
            }

        if (status["emeters"] is JsonArray emeters)
            for (var i = 0; i < emeters.Count; i++)
            {
                if (emeters[i] is not JsonObject e) continue;
                var p = GetDouble(e, "power");
                var q = GetDouble(e, "reactive");
                var v = GetDouble(e, "voltage");
                // The EM reports active and reactive power but not current; derive it from apparent power.
                var current = GetDouble(e, "current") ?? (p is not null && v is > 1
                    ? Math.Round(Math.Sqrt(p.Value * p.Value + (q ?? 0) * (q ?? 0)) / v.Value, 2)
                    : null);
                channels.Add(new ChannelState
                {
                    Key = $"emeter:{i}",
                    Kind = ChannelKind.Meter,
                    Index = i,
                    Name = names.TryGetValue($"emeter:{i}", out var n) ? n : null,
                    PowerW = p,
                    VoltageV = v,
                    CurrentA = current,
                    PowerFactor = GetDouble(e, "pf"),
                    EnergyWh = GetDouble(e, "total"),
                    Flags = GetBool(e, "is_valid") == false ? ["invalid_reading"] : [],
                });
            }

        return channels;
    }

    private static ChannelState Channel(
        string key, ChannelKind kind, int index, bool on, double? brightness, JsonObject o, JsonObject? meter,
        double? deviceVoltage, double? temperature, List<string> errors, IReadOnlyDictionary<string, string> names, DateTimeOffset now)
    {
        var power = meter is not null ? GetDouble(meter, "power") : null;
        // Plug, 1PM and Dimmer meters count energy in watt-minutes.
        var energyWh = meter is not null && GetDouble(meter, "total") is { } wmin ? Math.Round(wmin / 60.0, 1) : (double?)null;
        var current = power is not null && deviceVoltage is > 1 ? Math.Round(power.Value / deviceVoltage.Value, 2) : (double?)null;
        DateTimeOffset? timerEnds = GetBool(o, "has_timer") == true && GetDouble(o, "timer_remaining") is { } left and > 0
            ? now.AddSeconds(left)
            : null;
        return new ChannelState
        {
            Key = key,
            Kind = kind,
            Index = index,
            Name = names.TryGetValue(key, out var n) ? n : null,
            On = on,
            Brightness = brightness,
            PowerW = power,
            VoltageV = power is not null ? deviceVoltage : null,
            CurrentA = current,
            EnergyWh = energyWh,
            TemperatureC = power is not null ? temperature : null,
            Source = GetString(o, "source"),
            Errors = errors,
            TimerEndsAt = timerEnds,
        };
    }
}
