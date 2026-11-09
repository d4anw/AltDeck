using System.Text.Json;

namespace RoDeck.Services;

/// <summary>
/// Reading Roblox's answers without crashing when a field is missing or has another type (Roblox leaves fields out
/// when it's busy, and changes them now and then): a missing list is empty, a missing number is 0.
/// </summary>
public static class JsonX
{
    /// <summary>The array called <paramref name="name"/>, or nothing.</summary>
    public static IEnumerable<JsonElement> Arr(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray() : Enumerable.Empty<JsonElement>();

    /// <summary>The whole-number field called <paramref name="name"/> (also when Roblox sends it as text), or 0.</summary>
    public static long Num(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l
            : v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out var s) ? s : 0
            : 0;

    /// <summary>The text field called <paramref name="name"/>, or "".</summary>
    public static string Text(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
