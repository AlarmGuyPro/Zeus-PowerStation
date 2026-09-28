// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json;
using System.Text.Json.Nodes;

namespace KQ4WLR.PowerStation.Shelly;

/// <summary>
/// Parses device replies the way browsers and the Shelly apps do: if an
/// object repeats a property name, the last value wins. Some firmware does
/// this (the Plus RGBW PM repeats <c>button_fade_rate</c> in its config), and
/// <see cref="JsonNode.Parse(string, JsonNodeOptions?, JsonDocumentOptions)"/>
/// would otherwise throw when the object is read.
/// </summary>
public static class LenientJson
{
    public static JsonNode? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, MaxDepth = 64 });
        return Convert(doc.RootElement);
    }

    private static JsonNode? Convert(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var o = new JsonObject();
                foreach (var p in e.EnumerateObject()) o[p.Name] = Convert(p.Value); // indexer set: last one wins
                return o;
            }
            case JsonValueKind.Array:
            {
                var a = new JsonArray();
                foreach (var item in e.EnumerateArray()) a.Add(Convert(item));
                return a;
            }
            case JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False:
                // Element-backed, so it reads as int, long, double or string like JsonNode.Parse's values.
                return JsonValue.Create(e.Clone());
            default:
                return null;
        }
    }
}
