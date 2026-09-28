// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json;
using KQ4WLR.PowerStation.Services;
using KQ4WLR.PowerStation.Shelly;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace KQ4WLR.PowerStation.Api;

/// <summary>
/// HTTP surface for the PowerStation panels. The Zeus host mounts these
/// under <c>/api/plugins/io.github.alarmguypro.powerstation/</c>; the UI
/// reaches them only through <c>callBackend</c>.
/// </summary>
internal static class PowerStationEndpoints
{
    public static void Map(
        IEndpointRouteBuilder endpoints, Func<DeviceManager?> manager, Func<DiscoveryService?> discovery, string version)
    {
        endpoints.MapGet("discovery", (Handler)((HttpContext http) => Run(http, manager, (_, _) =>
            Task.FromResult<object?>(Discovery(discovery).View()))));

        endpoints.MapPut("discovery", (Handler)((HttpContext http) => Run(http, manager, async (_, ct) =>
        {
            var body = await ReadAsync<DiscoverySettingsRequest>(http, ct).ConfigureAwait(false);
            return await Discovery(discovery).UpdateSettingsAsync(body, ct).ConfigureAwait(false);
        })));

        endpoints.MapPost("discovery/scan", (Handler)((HttpContext http) => Run(http, manager, async (_, ct) =>
        {
            var body = await ReadOptionalAsync<ScanRequest>(http, ct).ConfigureAwait(false) ?? new ScanRequest(null, null);
            return Discovery(discovery).StartScan(body);
        })));

        endpoints.MapPost("discovery/cancel", (Handler)((HttpContext http) => Run(http, manager, (_, _) =>
            Task.FromResult<object?>(Discovery(discovery).CancelScan()))));

        endpoints.MapGet("status", (Handler)((HttpContext http) => Run(http, manager, (m, _) =>
            Task.FromResult<object?>(new
            {
                version,
                pollIntervalMs = m.Options.PollIntervalMs,
                devices = m.List(),
                scenes = m.Scenes.List(),
                layout = m.Layout,
            }))));

        endpoints.MapPut("layout", (Handler)((HttpContext http) => Run(http, manager, async (m, ct) =>
        {
            var body = await ReadAsync<Layout>(http, ct).ConfigureAwait(false);
            return await m.SaveLayoutAsync(body, ct).ConfigureAwait(false);
        })));

        endpoints.MapPost("scenes", (Handler)((HttpContext http) => Run(http, manager, async (m, ct) =>
        {
            var body = await ReadAsync<SceneRequest>(http, ct).ConfigureAwait(false);
            return await m.Scenes.CreateAsync(body, ct).ConfigureAwait(false);
        })));

        endpoints.MapPut("scenes/{id}", (HttpContext http, string id) => Run(http, manager, async (m, ct) =>
        {
            var body = await ReadAsync<SceneRequest>(http, ct).ConfigureAwait(false);
            return await m.Scenes.UpdateAsync(id, body, ct).ConfigureAwait(false);
        }));

        endpoints.MapDelete("scenes/{id}", (HttpContext http, string id) => Run(http, manager, async (m, ct) =>
        {
            await m.Scenes.DeleteAsync(id, ct).ConfigureAwait(false);
            return new { removed = id };
        }));

        endpoints.MapPost("scenes/{id}/run", (HttpContext http, string id) => Run(http, manager, async (m, ct) =>
        {
            var body = await ReadOptionalAsync<SceneRunRequest>(http, ct).ConfigureAwait(false);
            return await m.Scenes.RunAsync(id, body, ct).ConfigureAwait(false);
        }));

        endpoints.MapPost("devices/probe", (Handler)((HttpContext http) => Run(http, manager, async (m, ct) =>
        {
            var body = await ReadAsync<ProbeRequest>(http, ct).ConfigureAwait(false);
            var identity = await m.ProbeAsync(body.Host, ct).ConfigureAwait(false);
            return new
            {
                identity.DeviceId,
                identity.Generation,
                identity.Model,
                identity.App,
                identity.Firmware,
                identity.AuthRequired,
                identity.DefaultName,
                supported = identity.Generation >= 2,
            };
        })));

        endpoints.MapPost("devices", (Handler)((HttpContext http) => Run(http, manager, async (m, ct) =>
        {
            var body = await ReadAsync<AddDeviceRequest>(http, ct).ConfigureAwait(false);
            return await m.AddAsync(body, ct).ConfigureAwait(false);
        })));

        endpoints.MapPatch("devices/{id}", (HttpContext http, string id) => Run(http, manager, async (m, ct) =>
        {
            var body = await ReadAsync<UpdateDeviceRequest>(http, ct).ConfigureAwait(false);
            return await m.UpdateAsync(id, body, ct).ConfigureAwait(false);
        }));

        endpoints.MapDelete("devices/{id}", (HttpContext http, string id) => Run(http, manager, async (m, ct) =>
        {
            await m.RemoveAsync(id, ct).ConfigureAwait(false);
            return new { removed = id };
        }));

        endpoints.MapPost("devices/{id}/refresh", (HttpContext http, string id) =>
            Run(http, manager, async (m, ct) => await m.RefreshAsync(id, ct).ConfigureAwait(false)));

        endpoints.MapPost("devices/{id}/channels/{kind}/{index:int}", (HttpContext http, string id, string kind, int index) =>
            Run(http, manager, async (m, ct) =>
            {
                var body = await ReadAsync<ChannelCommand>(http, ct).ConfigureAwait(false);
                return await m.CommandAsync(id, kind, index, body, ct).ConfigureAwait(false);
            }));
    }

    /// <summary>
    /// Handlers taking only HttpContext would otherwise bind as a plain
    /// RequestDelegate and silently drop the returned IResult.
    /// </summary>
    private delegate Task<IResult> Handler(HttpContext http);

    private sealed record ProbeRequest(string? Host);

    private static DiscoveryService Discovery(Func<DiscoveryService?> get) =>
        get() ?? throw new PowerStationRequestException(503, "PowerStation is still starting.");

    private static async Task<IResult> Run(
        HttpContext http, Func<DeviceManager?> getManager, Func<DeviceManager, CancellationToken, Task<object?>> action)
    {
        var manager = getManager();
        if (manager is null) return Error(503, "starting", "PowerStation is still starting.");
        try
        {
            var result = await action(manager, http.RequestAborted).ConfigureAwait(false);
            return Results.Json(result, Json.Options);
        }
        catch (PowerStationRequestException ex)
        {
            return Error(ex.StatusCode, "request", ex.Message);
        }
        catch (ShellyException ex)
        {
            var status = ex.Kind switch
            {
                ShellyErrorKind.Unreachable => 504,
                ShellyErrorKind.Unauthorized => 403,
                ShellyErrorKind.Throttled => 429,
                ShellyErrorKind.Unsupported => 422,
                _ => 502,
            };
            return Error(status, ex.Kind.ToString().ToLowerInvariant(), ex.Message);
        }
        catch (JsonException)
        {
            return Error(400, "request", "The request body wasn't valid JSON.");
        }
    }

    private static IResult Error(int status, string kind, string message) =>
        Results.Json(new { error = message, kind }, Json.Options, statusCode: status);

    /// <summary>
    /// Reads a body that may be absent. Doesn't trust Content-Length, which
    /// is missing for chunked requests.
    /// </summary>
    private static async Task<T?> ReadOptionalAsync<T>(HttpContext http, CancellationToken ct) where T : class
    {
        using var reader = new StreamReader(http.Request.Body);
        var buffer = new char[16 * 1024 + 1];
        var read = await reader.ReadBlockAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
        if (read > 16 * 1024) throw new PowerStationRequestException(413, "Request too large.");
        var text = new string(buffer, 0, read);
        return string.IsNullOrWhiteSpace(text) ? null : JsonSerializer.Deserialize<T>(text, Json.Options);
    }

    private static async Task<T> ReadAsync<T>(HttpContext http, CancellationToken ct) where T : class =>
        await ReadOptionalAsync<T>(http, ct).ConfigureAwait(false)
        ?? throw new PowerStationRequestException(400, "The request body was empty.");
}
