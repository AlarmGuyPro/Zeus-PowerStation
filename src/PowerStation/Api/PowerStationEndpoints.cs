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
    public static void Map(IEndpointRouteBuilder endpoints, Func<DeviceManager?> manager, string version)
    {
        endpoints.MapGet("status", (Handler)((HttpContext http) => Run(http, manager, (m, _) =>
            Task.FromResult<object?>(new
            {
                version,
                pollIntervalMs = m.Options.PollIntervalMs,
                devices = m.List(),
            }))));

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

    private static async Task<T> ReadAsync<T>(HttpContext http, CancellationToken ct)
    {
        if (http.Request.ContentLength is > 16 * 1024)
            throw new PowerStationRequestException(413, "Request too large.");
        var value = await JsonSerializer.DeserializeAsync<T>(http.Request.Body, Json.Options, ct).ConfigureAwait(false);
        return value ?? throw new PowerStationRequestException(400, "The request body was empty.");
    }
}
