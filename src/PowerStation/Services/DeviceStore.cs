// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json;
using KQ4WLR.PowerStation.Model;
using Zeus.Plugins.Contracts;

namespace KQ4WLR.PowerStation.Services;

public sealed record PowerStationOptions
{
    /// <summary>How often each online device is polled.</summary>
    public int PollIntervalMs { get; init; } = 2000;

    /// <summary>Longest wait between polls of a device that isn't answering.</summary>
    public int MaxBackoffMs { get; init; } = 30000;

    /// <summary>Consecutive missed polls before PowerStation looks for a device elsewhere.</summary>
    public int RefindAfterFailures { get; init; } = 3;

    /// <summary>Wait before the first re-find retry; doubles each time up to 30 minutes.</summary>
    public int RefindCooldownMs { get; init; } = 60000;
}

public interface IDeviceStore
{
    Task<IReadOnlyList<DeviceRecord>> LoadDevicesAsync(CancellationToken ct);
    Task SaveDevicesAsync(IReadOnlyList<DeviceRecord> devices, CancellationToken ct);
    Task<PowerStationOptions> LoadOptionsAsync(CancellationToken ct);
    Task<IReadOnlyList<Scene>> LoadScenesAsync(CancellationToken ct);
    Task SaveScenesAsync(IReadOnlyList<Scene> scenes, CancellationToken ct);
    Task<DiscoverySettings> LoadDiscoverySettingsAsync(CancellationToken ct);
    Task SaveDiscoverySettingsAsync(DiscoverySettings settings, CancellationToken ct);
    Task<Layout?> LoadLayoutAsync(CancellationToken ct);
    Task SaveLayoutAsync(Layout layout, CancellationToken ct);
    /// <summary>Whether the Debug traffic log is on. Defaults to on.</summary>
    Task<bool> LoadDebugEnabledAsync(CancellationToken ct) => Task.FromResult(false);
    Task SaveDebugEnabledAsync(bool enabled, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>Status-tab grid: column count (0 = automatic) and device IDs per column, top to bottom.</summary>
public sealed record Layout
{
    public int Columns { get; init; }
    public IReadOnlyList<IReadOnlyList<string>> Order { get; init; } = [];
}

/// <summary>
/// Persists devices in the plugin-scoped Zeus settings store. Values are
/// written as JSON strings we serialize ourselves, so the on-disk shape is
/// under our control and versioned by key.
/// </summary>
public sealed class SettingsDeviceStore : IDeviceStore
{
    internal const string DevicesKey = "devices.v1";
    internal const string OptionsKey = "options.v1";
    internal const string ScenesKey = "scenes.v1";
    internal const string LayoutKey = "layout.v1";
    internal const string DebugKey = "debug.v1";

    private readonly IPluginSettings _settings;

    public SettingsDeviceStore(IPluginSettings settings) => _settings = settings;

    public async Task<IReadOnlyList<DeviceRecord>> LoadDevicesAsync(CancellationToken ct)
    {
        var json = await _settings.GetAsync<string>(DevicesKey, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json)) return [];
        return JsonSerializer.Deserialize<List<DeviceRecord>>(json, Json.Options) ?? [];
    }

    public Task SaveDevicesAsync(IReadOnlyList<DeviceRecord> devices, CancellationToken ct) =>
        _settings.SetAsync(DevicesKey, JsonSerializer.Serialize(devices, Json.Options), ct);

    public async Task<IReadOnlyList<Scene>> LoadScenesAsync(CancellationToken ct)
    {
        var json = await _settings.GetAsync<string>(ScenesKey, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json)) return [];
        return JsonSerializer.Deserialize<List<Scene>>(json, Json.Options) ?? [];
    }

    public Task SaveScenesAsync(IReadOnlyList<Scene> scenes, CancellationToken ct) =>
        _settings.SetAsync(ScenesKey, JsonSerializer.Serialize(scenes, Json.Options), ct);

    public async Task<DiscoverySettings> LoadDiscoverySettingsAsync(CancellationToken ct)
    {
        var json = await _settings.GetAsync<string>(DiscoveryService.SettingsKey, ct).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(json)
            ? new DiscoverySettings()
            : JsonSerializer.Deserialize<DiscoverySettings>(json, Json.Options) ?? new DiscoverySettings();
    }

    public Task SaveDiscoverySettingsAsync(DiscoverySettings settings, CancellationToken ct) =>
        _settings.SetAsync(DiscoveryService.SettingsKey, JsonSerializer.Serialize(settings, Json.Options), ct);

    public async Task<Layout?> LoadLayoutAsync(CancellationToken ct)
    {
        var json = await _settings.GetAsync<string>(LayoutKey, ct).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<Layout>(json, Json.Options);
    }

    public Task SaveLayoutAsync(Layout layout, CancellationToken ct) =>
        _settings.SetAsync(LayoutKey, JsonSerializer.Serialize(layout, Json.Options), ct);

    public async Task<bool> LoadDebugEnabledAsync(CancellationToken ct)
    {
        var json = await _settings.GetAsync<string>(DebugKey, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json)) return false;
        try { return JsonDocument.Parse(json).RootElement.GetProperty("enabled").GetBoolean(); }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { return false; }
    }

    public Task SaveDebugEnabledAsync(bool enabled, CancellationToken ct) =>
        _settings.SetAsync(DebugKey, JsonSerializer.Serialize(new { enabled }, Json.Options), ct);

    public async Task<PowerStationOptions> LoadOptionsAsync(CancellationToken ct)
    {
        var json = await _settings.GetAsync<string>(OptionsKey, ct).ConfigureAwait(false);
        var options = string.IsNullOrWhiteSpace(json)
            ? new PowerStationOptions()
            : JsonSerializer.Deserialize<PowerStationOptions>(json, Json.Options) ?? new PowerStationOptions();
        return options with
        {
            PollIntervalMs = Math.Clamp(options.PollIntervalMs, 500, 60000),
            MaxBackoffMs = Math.Clamp(options.MaxBackoffMs, 2000, 300000),
            RefindAfterFailures = Math.Clamp(options.RefindAfterFailures, 1, 20),
            RefindCooldownMs = Math.Clamp(options.RefindCooldownMs, 1000, 3600000),
        };
    }
}

internal static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };
}
