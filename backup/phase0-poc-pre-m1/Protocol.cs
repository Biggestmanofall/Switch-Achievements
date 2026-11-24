using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SwitchAchievements.Longshot.Poc;

/// <summary>
/// Wire helpers for Longshot's control bridge (protocol v1): newline-delimited JSON,
/// requests up to 64 KB per line, replies carry id+frame, subscription events carry
/// an "event" key and no "ok".
/// </summary>
public static class Wire
{
    public const int ProtocolVersion = 1;
    public const int MaxLineBytes = 64 * 1024;

    public static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = null,
        WriteIndented = false,
    };

    public static JsonElement ParseOneLine(string line)
    {
        using var doc = JsonDocument.Parse(line);
        return doc.RootElement.Clone();
    }

    public static bool IsEvent(JsonElement el) => el.TryGetProperty("event", out _);

    public static string? GetEvent(JsonElement el) =>
        el.TryGetProperty("event", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;

    public static long? GetId(JsonElement el) =>
        el.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.Number && i.TryGetInt64(out var v) ? v : null;

    public static bool GetOk(JsonElement el) =>
        el.TryGetProperty("ok", out var o) && o.ValueKind == JsonValueKind.True;

    public static string? GetError(JsonElement el) =>
        el.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;

    public static string? GetMessage(JsonElement el) =>
        el.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;

    public static string FormatScalar(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString() ?? "<null>",
        JsonValueKind.Null => "null",
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => el.GetRawText(),
    };
}

/// <summary>Builders for protocol v1 requests. "id" is injected by the client.</summary>
public static class Requests
{
    public static Dictionary<string, object?> Status() => new() { ["cmd"] = "status" };

    public static Dictionary<string, object?> Subscribe(string[] events, int every, string[]? flags = null, bool changes = false)
    {
        var d = new Dictionary<string, object?> { ["cmd"] = "subscribe", ["events"] = events, ["every"] = every };
        if (flags is { Length: > 0 }) d["flags"] = flags;
        if (changes) d["changes"] = true;
        return d;
    }

    public static Dictionary<string, object?> Unsubscribe() => new() { ["cmd"] = "unsubscribe" };

    public static Dictionary<string, object?> Quit() => new() { ["cmd"] = "quit" };
}

/// <summary>Ocarina of Time game snapshot (status.game, and the oot event's game object).</summary>
public sealed class OotGameSnapshot
{
    [JsonPropertyName("t")] public double? T { get; set; }
    [JsonPropertyName("frame")] public long? SampleFrame { get; set; }
    [JsonPropertyName("map")] public string? Map { get; set; }
    [JsonPropertyName("pos")] public double[]? Pos { get; set; }
    [JsonPropertyName("facing")] public double? Facing { get; set; }
    [JsonPropertyName("camera")] public double? Camera { get; set; }
    [JsonPropertyName("event")] public bool? EventPlaying { get; set; }
    [JsonPropertyName("loading")] public bool? Loading { get; set; }
    [JsonPropertyName("age")] public string? Age { get; set; }
    [JsonPropertyName("life")] public int? Life { get; set; }
    [JsonPropertyName("max_life")] public int? MaxLife { get; set; }
    [JsonPropertyName("rupees")] public int? Rupees { get; set; }
    [JsonPropertyName("prompt")] public string? Prompt { get; set; }
    [JsonPropertyName("music")] public string? Music { get; set; }
    [JsonPropertyName("flags")] public Dictionary<string, JsonElement>? Flags { get; set; }
    [JsonPropertyName("changed")] public Dictionary<string, JsonElement>? Changed { get; set; }

    public override string ToString()
    {
        static string B(bool? v) => v is bool x ? (x ? "true" : "false") : "?";
        static string N(double? v) => v is null ? "?" : v.Value.ToString("0.###");
        var pos = Pos is { Length: 3 } ? $"[{Pos[0]:0.#}, {Pos[1]:0.#}, {Pos[2]:0.#}]" : "null";
        var flags = Flags is { Count: > 0 }
            ? string.Join(" ", Flags.Select(kv => $"{kv.Key}={Wire.FormatScalar(kv.Value)}"))
            : "(none)";
        var changed = Changed is { Count: > 0 }
            ? string.Join(" ", Changed.Select(kv => $"{kv.Key}={Wire.FormatScalar(kv.Value)}"))
            : "{}";
        return $"map={Map ?? "null"} age={Age ?? "?"} event={B(EventPlaying)} loading={B(Loading)} " +
               $"life={Life}/{MaxLife} rupees={Rupees} pos={pos} facing={N(Facing)} camera={N(Camera)} " +
               $"prompt={Prompt ?? "?"} music={Music ?? "?"} | flags: {flags} | changed: {changed}";
    }
}

/// <summary>A status reply.</summary>
public sealed class StatusReply
{
    [JsonPropertyName("frame")] public long Frame { get; set; }
    [JsonPropertyName("protocol")] public int Protocol { get; set; }
    [JsonPropertyName("fps")] public double Fps { get; set; }
    [JsonPropertyName("state")] public string? State { get; set; }
    [JsonPropertyName("stalled")] public bool Stalled { get; set; }
    [JsonPropertyName("draws")] public double Draws { get; set; }
    [JsonPropertyName("replayed")] public long Replayed { get; set; }
    [JsonPropertyName("map")] public string? Map { get; set; }
    [JsonPropertyName("version")] public string? Version { get; set; }
    [JsonPropertyName("renderer")] public string? Renderer { get; set; }
    [JsonPropertyName("uptime")] public double Uptime { get; set; }
    [JsonPropertyName("clients")] public int Clients { get; set; }
    [JsonPropertyName("game")] public OotGameSnapshot? Game { get; set; }
}

/// <summary>An oot subscription event.</summary>
public sealed class OotEvent
{
    [JsonPropertyName("sub")] public long Sub { get; set; }
    [JsonPropertyName("frame")] public long Frame { get; set; }
    [JsonPropertyName("game")] public OotGameSnapshot? Game { get; set; }
}