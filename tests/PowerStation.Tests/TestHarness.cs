// SPDX-License-Identifier: GPL-2.0-or-later
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Plugins.Contracts;

namespace PowerStation.Tests;

[AttributeUsage(AttributeTargets.Method)]
public sealed class TestAttribute : Attribute;

public sealed class AssertionException(string message) : Exception(message);

public static class Assert
{
    public static void True(bool condition, string message = "expected true")
    {
        if (!condition) throw new AssertionException(message);
    }

    public static void False(bool condition, string message = "expected false") => True(!condition, message);

    public static void Equal<T>(T expected, T actual, string? what = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new AssertionException($"{what ?? "value"}: expected <{expected}> but got <{actual}>");
    }

    public static void NotNull(object? value, string what = "value")
    {
        if (value is null) throw new AssertionException($"{what} was null");
    }

    public static void Contains(string needle, string? haystack, string what = "text")
    {
        if (haystack is null || !haystack.Contains(needle, StringComparison.Ordinal))
            throw new AssertionException($"{what} should contain \"{needle}\" but was \"{haystack}\"");
    }

    public static void DoesNotContain(string needle, string? haystack, string what = "text")
    {
        if (haystack is not null && haystack.Contains(needle, StringComparison.Ordinal))
            throw new AssertionException($"{what} must not contain \"{needle}\"");
    }

    public static async Task<TException> ThrowsAsync<TException>(Func<Task> action) where TException : Exception
    {
        try { await action(); }
        catch (TException ex) { return ex; }
        catch (Exception ex) { throw new AssertionException($"expected {typeof(TException).Name} but got {ex.GetType().Name}: {ex.Message}"); }
        throw new AssertionException($"expected {typeof(TException).Name} but nothing was thrown");
    }
}

/// <summary>In-memory stand-in for the Zeus per-plugin settings store.</summary>
public sealed class MemorySettings : IPluginSettings
{
    public ConcurrentDictionary<string, object?> Values { get; } = new();

    public Task<T?> GetAsync<T>(string key, CancellationToken ct = default) =>
        Task.FromResult(Values.TryGetValue(key, out var v) ? (T?)v : default);

    public Task SetAsync<T>(string key, T value, CancellationToken ct = default)
    {
        Values[key] = value;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        Values.TryRemove(key, out _);
        return Task.CompletedTask;
    }
}

public sealed class FakePluginContext : IPluginContext
{
    public string PluginId => "io.github.alarmguypro.powerstation";
    public PluginManifest Manifest { get; } = new()
    {
        Id = "io.github.alarmguypro.powerstation",
        Name = "PowerStation",
        Version = "0.1.0-test",
        Sdk = new SdkRequirement { Abi = 1, MinVersion = "1.5.0" },
        Entrypoint = new EntryPoint { Assembly = "KQ4WLR.PowerStation.dll" },
    };
    public ILogger Logger { get; } = NullLogger.Instance;
    public string PluginRootPath => Path.GetTempPath();
    public PluginCapabilities GrantedCapabilities => PluginCapabilities.NetworkAccess | PluginCapabilities.PersistSettings;
    public IPluginSettings Settings { get; } = new MemorySettings();
    public IRadioStateReader? Radio => null;
    public IRadioController? RadioController => null;
}

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var filter = args.FirstOrDefault();
        var tests = typeof(Program).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.GetCustomAttribute<TestAttribute>() is not null)
                .Select(m => (Name: $"{t.Name}.{m.Name}", Method: m)))
            .Where(t => filter is null || t.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToList();

        var failures = 0;
        var total = Stopwatch.StartNew();
        foreach (var (name, method) in tests)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var result = method.Invoke(null, null);
                if (result is Task task)
                {
                    var finished = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(30)));
                    if (finished != task) throw new AssertionException("timed out after 30 s");
                    await task;
                }
                Console.WriteLine($"  PASS  {name} ({sw.ElapsedMilliseconds} ms)");
            }
            catch (Exception ex)
            {
                failures++;
                var inner = ex is TargetInvocationException { InnerException: { } i } ? i : ex;
                Console.WriteLine($"  FAIL  {name}: {inner.GetType().Name}: {inner.Message}");
                if (inner is not AssertionException) Console.WriteLine(inner.StackTrace);
            }
        }
        Console.WriteLine($"{tests.Count - failures}/{tests.Count} passed in {total.ElapsedMilliseconds} ms");
        return failures;
    }
}
