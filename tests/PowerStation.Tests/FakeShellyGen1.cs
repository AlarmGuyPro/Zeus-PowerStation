// SPDX-License-Identifier: GPL-2.0-or-later
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
/// In-process imitation of a first-generation Shelly: /shelly, /settings,
/// /status, /relay/N and /light/N, with optional HTTP Basic auth and the
/// flip-back timer. Shapes follow the Gen1 API docs for the ShellyEM,
/// Shelly 1, Plug S and Dimmer 2.
/// </summary>
public sealed class FakeShellyGen1 : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly object _lock = new();

    public required string Type { get; init; }
    public string Mac { get; init; } = "C45BBE6A1F22";
    public string User { get; set; } = "admin";
    public string? Password { get; set; }
    public List<Relay> Relays { get; } = [];
    public List<Light> Lights { get; } = [];
    public List<EMeter> EMeters { get; } = [];
    /// <summary>Plug / 1PM style meters, index-matched to relays or lights.</summary>
    public List<double> MeterPower { get; } = [];
    public List<string> Requests { get; } = [];
    /// <summary>Authorization headers received, including on /shelly.</summary>
    public List<string> AuthHeaders { get; } = [];
    public string Host { get; private set; } = "";

    public sealed class Relay { public bool On; public string? Name; public int TimerSeconds; public bool Overpower; }
    public sealed class Light { public bool On; public double Brightness = 50; public string? Name; public int TimerSeconds; public int LastTransitionMs; }
    public sealed class EMeter { public double Power = 1184.2, Reactive = 150, Voltage = 121.3, Total = 412876; public string? Name; }

    public string DeviceId => $"{Type.ToLowerInvariant()}-{Mac.ToLowerInvariant()}";

    private FakeShellyGen1(WebApplication app) => _app = app;

    public static async Task<FakeShellyGen1> StartAsync(string type, Action<FakeShellyGen1>? configure = null, string url = "http://127.0.0.1:0")
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls(url);
        var app = builder.Build();
        var fake = new FakeShellyGen1(app) { Type = type };
        configure?.Invoke(fake);

        app.Use(async (h, next) =>
        {
            var auth = h.Request.Headers.Authorization.ToString();
            if (auth.Length > 0) lock (fake._lock) fake.AuthHeaders.Add(auth);
            await next(h);
        });
        app.MapGet("/shelly", () => Results.Json(new JsonObject
        {
            ["type"] = fake.Type, ["mac"] = fake.Mac, ["auth"] = fake.Password is not null, ["fw"] = "20230913-114008/v1.14.0-gcb84623",
            ["num_outputs"] = Math.Max(fake.Relays.Count, fake.Lights.Count), ["num_emeters"] = fake.EMeters.Count,
        }));
        app.MapGet("/settings", (HttpContext h) => fake.Guard(h, fake.Settings));
        app.MapGet("/status", (HttpContext h) => fake.Guard(h, fake.Status));
        app.MapGet("/relay/{i:int}", (HttpContext h, int i) => fake.Guard(h, () => fake.SetRelay(i, h.Request.Query)));
        app.MapGet("/light/{i:int}", (HttpContext h, int i) => fake.Guard(h, () => fake.SetLight(i, h.Request.Query)));
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        fake.Host = new Uri(address).Authority;
        return fake;
    }

    private IResult Guard(HttpContext http, Func<JsonObject> handler)
    {
        lock (_lock) Requests.Add(http.Request.Path + http.Request.QueryString);
        if (Password is not null)
        {
            var header = http.Request.Headers.Authorization.ToString();
            var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{User}:{Password}"));
            if (header != expected)
            {
                http.Response.Headers.WWWAuthenticate = "Basic realm=\"" + DeviceId + "\"";
                return Results.Text("401 Unauthorized", statusCode: 401);
            }
        }
        try
        {
            lock (_lock) return Results.Json(handler());
        }
        catch (ArgumentOutOfRangeException)
        {
            return Results.Text("Bad id", statusCode: 404);
        }
    }

    private JsonObject Settings() => new()
    {
        ["device"] = new JsonObject { ["type"] = Type, ["mac"] = Mac },
        ["name"] = "Shack mains",
        ["relays"] = new JsonArray(Relays.Select(r => (JsonNode)new JsonObject { ["name"] = r.Name, ["ison"] = r.On }).ToArray()),
        ["lights"] = new JsonArray(Lights.Select(l => (JsonNode)new JsonObject { ["name"] = l.Name }).ToArray()),
        ["emeters"] = new JsonArray(EMeters.Select(e => (JsonNode)new JsonObject { ["name"] = e.Name }).ToArray()),
    };

    private JsonObject Status()
    {
        var st = new JsonObject
        {
            ["relays"] = new JsonArray(Relays.Select(r => (JsonNode)new JsonObject
            {
                ["ison"] = r.On, ["has_timer"] = r.TimerSeconds > 0, ["timer_remaining"] = r.TimerSeconds,
                ["overpower"] = r.Overpower, ["source"] = "http",
            }).ToArray()),
            ["temperature"] = 38.2,
            ["overtemperature"] = false,
        };
        if (Lights.Count > 0)
            st["lights"] = new JsonArray(Lights.Select(l => (JsonNode)new JsonObject
            {
                ["ison"] = l.On, ["brightness"] = l.Brightness, ["has_timer"] = l.TimerSeconds > 0, ["timer_remaining"] = l.TimerSeconds,
            }).ToArray());
        if (MeterPower.Count > 0)
            st["meters"] = new JsonArray(MeterPower.Select((p, i) => (JsonNode)new JsonObject
            {
                ["power"] = IsOn(i) ? p : 0, ["is_valid"] = true, ["total"] = 60000,
            }).ToArray());
        if (EMeters.Count > 0)
            st["emeters"] = new JsonArray(EMeters.Select(e => (JsonNode)new JsonObject
            {
                ["power"] = e.Power, ["reactive"] = e.Reactive, ["voltage"] = e.Voltage, ["is_valid"] = true,
                ["total"] = e.Total, ["total_returned"] = 0,
            }).ToArray());
        return st;
    }

    private bool IsOn(int i) => i < Relays.Count ? Relays[i].On : i < Lights.Count && Lights[i].On;

    private JsonObject SetRelay(int i, IQueryCollection q)
    {
        var r = Relays[i];
        switch (q["turn"].ToString())
        {
            case "on": r.On = true; break;
            case "off": r.On = false; break;
            case "toggle": r.On = !r.On; break;
        }
        r.TimerSeconds = int.TryParse(q["timer"], out var t) ? t : 0;
        return new JsonObject { ["ison"] = r.On, ["has_timer"] = r.TimerSeconds > 0 };
    }

    private JsonObject SetLight(int i, IQueryCollection q)
    {
        var l = Lights[i];
        switch (q["turn"].ToString())
        {
            case "on": l.On = true; break;
            case "off": l.On = false; break;
            case "toggle": l.On = !l.On; break;
        }
        if (double.TryParse(q["brightness"], System.Globalization.CultureInfo.InvariantCulture, out var b)) l.Brightness = b;
        if (int.TryParse(q["transition"], out var tr)) l.LastTransitionMs = tr;
        l.TimerSeconds = int.TryParse(q["timer"], out var t) ? t : 0;
        return new JsonObject { ["ison"] = l.On, ["brightness"] = l.Brightness };
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
