// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json.Serialization;
using KQ4WLR.PowerStation.Model;

namespace KQ4WLR.PowerStation.Services;

/// <summary>One output a scene sets: which channel, on or off, and dimmer level.</summary>
public sealed record SceneTarget
{
    public required string DeviceId { get; init; }
    public required ChannelKind Kind { get; init; }
    public required int Index { get; init; }
    public bool On { get; init; }

    /// <summary>Dimmers and colour lights: level to set when turning on (1-100). Null keeps the current level.</summary>
    public double? Brightness { get; init; }

    /// <summary>Colour lights: [r, g, b] 0-255 to set when turning on. Null keeps the current colour.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int[]? Rgb { get; init; }

    /// <summary>RGBW: white channel 0-255.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? White { get; init; }

    [JsonIgnore]
    public string ChannelKey => KeyFor(Kind, Index);

    public static string KeyFor(ChannelKind kind, int index) => $"{kind.Prefix()}:{index}";
}

/// <summary>A named group of outputs and levels applied together.</summary>
public sealed record Scene
{
    public required string Id { get; init; }
    public required string Name { get; init; }

    /// <summary>Optional fade for dimmers, in seconds.</summary>
    public double? FadeSeconds { get; init; }

    /// <summary>Safety timer (minutes) put on every output this scene turns on.</summary>
    public int? SafetyMinutes { get; init; }

    public IReadOnlyList<SceneTarget> Targets { get; init; } = [];
}

public sealed record SceneRequest(string? Name, double? FadeSeconds, List<SceneTarget>? Targets, int? SafetyMinutes = null);

/// <summary>
/// "apply" sets every target to its saved state. "off" turns every output in
/// the scene off, so a scene also works as an on/off group.
/// </summary>
public sealed record SceneRunRequest(string? Mode);

public sealed record TargetResult(string DeviceId, string ChannelKey, bool Ok, string? Error);

public sealed record SceneRunResult
{
    public required string SceneId { get; init; }
    public required string Mode { get; init; }
    public required int Succeeded { get; init; }
    public required int Failed { get; init; }
    public required IReadOnlyList<TargetResult> Results { get; init; }
    public required IReadOnlyList<DeviceView> Devices { get; init; }
}

/// <summary>Stores scenes and runs them through the device manager.</summary>
public sealed class SceneManager
{
    public const int MaxScenes = 50;
    public const int MaxTargets = 64;

    private readonly IDeviceStore _store;
    private readonly DeviceManager _devices;
    private readonly SemaphoreSlim _mutate = new(1, 1);
    private List<Scene> _scenes = [];

    public SceneManager(IDeviceStore store, DeviceManager devices)
    {
        _store = store;
        _devices = devices;
    }

    public async Task LoadAsync(CancellationToken ct) =>
        _scenes = (await _store.LoadScenesAsync(ct).ConfigureAwait(false)).ToList();

    public IReadOnlyList<Scene> List() => _scenes.ToArray();

    public Scene? Find(string id) => _scenes.FirstOrDefault(s => s.Id == id);

    public async Task<Scene> CreateAsync(SceneRequest request, CancellationToken ct)
    {
        await _mutate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_scenes.Count >= MaxScenes)
                throw new PowerStationRequestException(400, $"You can have up to {MaxScenes} scenes.");
            var scene = Validate(Guid.NewGuid().ToString("N")[..12], request);
            _scenes.Add(scene);
            await _store.SaveScenesAsync(_scenes, ct).ConfigureAwait(false);
            return scene;
        }
        finally { _mutate.Release(); }
    }

    public async Task<Scene> UpdateAsync(string id, SceneRequest request, CancellationToken ct)
    {
        await _mutate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var index = IndexOf(id);
            var scene = Validate(id, request);
            _scenes[index] = scene;
            await _store.SaveScenesAsync(_scenes, ct).ConfigureAwait(false);
            return scene;
        }
        finally { _mutate.Release(); }
    }

    public async Task DeleteAsync(string id, CancellationToken ct)
    {
        await _mutate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _scenes.RemoveAt(IndexOf(id));
            await _store.SaveScenesAsync(_scenes, ct).ConfigureAwait(false);
        }
        finally { _mutate.Release(); }
    }

    /// <summary>Drops a removed device's outputs from every scene.</summary>
    public async Task ForgetDeviceAsync(string deviceId, CancellationToken ct)
    {
        await _mutate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var changed = false;
            for (var i = 0; i < _scenes.Count; i++)
            {
                var kept = _scenes[i].Targets.Where(t => t.DeviceId != deviceId).ToArray();
                if (kept.Length == _scenes[i].Targets.Count) continue;
                _scenes[i] = _scenes[i] with { Targets = kept };
                changed = true;
            }
            if (changed) await _store.SaveScenesAsync(_scenes, ct).ConfigureAwait(false);
        }
        finally { _mutate.Release(); }
    }

    public async Task<SceneRunResult> RunAsync(string id, SceneRunRequest? request, CancellationToken ct)
    {
        var scene = _scenes[IndexOf(id)];
        _devices.Tx.ThrowIfBlocked();
        var mode = (request?.Mode ?? "apply").Trim().ToLowerInvariant();
        if (mode is not ("apply" or "off"))
            throw new PowerStationRequestException(400, "Mode must be apply or off.");
        if (scene.Targets.Count == 0)
            throw new PowerStationRequestException(400, $"\"{scene.Name}\" has no outputs. Edit it to add some.");

        var targets = mode == "off"
            ? scene.Targets.Select(t => t with { On = false, Brightness = null, Rgb = null, White = null }).ToArray()
            : scene.Targets.ToArray();
        if (mode == "apply" && scene.SafetyMinutes is { } minutes)
            _devices.SetSceneSafety(targets.Where(t => t.On), minutes);
        var (results, views) = await _devices.ApplyTargetsAsync(targets, scene.FadeSeconds, ct).ConfigureAwait(false);
        return new SceneRunResult
        {
            SceneId = scene.Id,
            Mode = mode,
            Succeeded = results.Count(r => r.Ok),
            Failed = results.Count(r => !r.Ok),
            Results = results,
            Devices = views,
        };
    }

    /// <summary>Colour only on colour lights, white only on RGBW; values 0-255.</summary>
    internal static (int[]? Rgb, double? White) ValidateColor(ChannelKind kind, bool on, int[]? rgb, double? white)
    {
        if (!kind.IsColor())
        {
            if (rgb is not null || white is not null)
                throw new PowerStationRequestException(400, "Only colour lights have a colour.");
            return (null, null);
        }
        if (!on) return (null, null);
        if (rgb is not null && (rgb.Length != 3 || rgb.Any(v => v is < 0 or > 255)))
            throw new PowerStationRequestException(400, "A colour needs red, green and blue values from 0 to 255.");
        if (white is not null && kind != ChannelKind.Rgbw)
            throw new PowerStationRequestException(400, "Only RGBW lights have a white channel.");
        if (white is < 0 or > 255) throw new PowerStationRequestException(400, "White must be from 0 to 255.");
        return (rgb, white is null ? null : Math.Round(white.Value));
    }

    private int IndexOf(string id)
    {
        var index = _scenes.FindIndex(s => s.Id == id);
        return index >= 0 ? index : throw new PowerStationRequestException(404, "That scene doesn't exist.");
    }

    private Scene Validate(string id, SceneRequest request)
    {
        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name)) throw new PowerStationRequestException(400, "Give the scene a name.");
        if (name.Length > 40) throw new PowerStationRequestException(400, "Scene names can be up to 40 characters.");
        if (_scenes.Any(s => s.Id != id && string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new PowerStationRequestException(409, $"There's already a scene called \"{name}\".");
        if (request.FadeSeconds is < 0 or > 600)
            throw new PowerStationRequestException(400, "Fade must be between 0 and 600 seconds.");
        if (request.SafetyMinutes is < 0 or > 1440)
            throw new PowerStationRequestException(400, "The safety timer must be between 1 and 1,440 minutes.");

        var targets = request.Targets ?? [];
        if (targets.Count == 0) throw new PowerStationRequestException(400, "Pick at least one output for the scene.");
        if (targets.Count > MaxTargets) throw new PowerStationRequestException(400, $"A scene can hold up to {MaxTargets} outputs.");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var clean = new List<SceneTarget>();
        foreach (var t in targets)
        {
            if (string.IsNullOrWhiteSpace(t.DeviceId) || !_devices.Contains(t.DeviceId))
                throw new PowerStationRequestException(400, "The scene refers to a device that isn't in PowerStation.");
            if (t.Index is < 0 or > 15) throw new PowerStationRequestException(400, "Channel number out of range.");
            if (t.Kind == ChannelKind.Meter) throw new PowerStationRequestException(400, "Meters can't be part of a scene.");
            if (!seen.Add($"{t.DeviceId}/{t.ChannelKey}"))
                throw new PowerStationRequestException(400, "Each output can appear only once in a scene.");
            double? brightness = null;
            if (t.Kind.IsDimmable() && t.On && t.Brightness is not null)
            {
                if (t.Brightness is < 1 or > 100)
                    throw new PowerStationRequestException(400, "Dimmer levels must be between 1 and 100%.");
                brightness = Math.Round(t.Brightness.Value);
            }
            else if (t.Kind == ChannelKind.Switch && t.Brightness is not null)
            {
                throw new PowerStationRequestException(400, "Only dimmers have a level.");
            }
            var (rgb, white) = ValidateColor(t.Kind, t.On, t.Rgb, t.White);
            clean.Add(t with { Brightness = brightness, Rgb = rgb, White = white });
        }

        return new Scene
        {
            Id = id,
            Name = name,
            FadeSeconds = request.FadeSeconds is > 0 ? request.FadeSeconds : null,
            SafetyMinutes = request.SafetyMinutes is > 0 ? request.SafetyMinutes : null,
            Targets = clean,
        };
    }
}
