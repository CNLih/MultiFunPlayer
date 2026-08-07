using System.Text.Json;
using System.Text.Json.Nodes;

namespace MultiFunPlayer.Infrastructure.Configuration;

public sealed record MigratedAxis(string Name, bool Enabled, double DefaultValue);

public sealed record MigratedOutput(string Id, string Protocol, string? Endpoint, bool Enabled);

public sealed record MigratedMediaSource(string Id, bool Selected, string? Endpoint);

public sealed record MigratedConfiguration(
    int SchemaVersion,
    int SourceConfigVersion,
    string? SelectedDevice,
    IReadOnlyList<MigratedAxis> Axes,
    IReadOnlyList<MigratedOutput> Outputs,
    IReadOnlyList<MigratedMediaSource> MediaSources,
    IReadOnlyList<string> ScriptRoots,
    IReadOnlyList<string> Warnings);

public sealed record ConfigurationMigrationResult(bool Success, MigratedConfiguration? Configuration,
    string? Error)
{
    public static ConfigurationMigrationResult Failed(string error)
        => new(false, null, error);
}

/// <summary>
/// Reads the legacy desktop JSON without loading WPF, Stylet, Newtonsoft
/// converters, or plugin types. Unknown and secret fields are intentionally
/// ignored; the source file is never modified.
/// </summary>
public sealed class ConfigurationMigrationService
{
    private static readonly string[] SensitiveNames =
        ["token", "secret", "password", "passwd", "api_key", "api-key", "apikey", "credential", "connectionkey"];

    public ConfigurationMigrationResult Read(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return ConfigurationMigrationResult.Failed("Configuration JSON is empty.");

        JsonObject root;
        try
        {
            var node = JsonNode.Parse(json);
            root = node as JsonObject
                ?? throw new JsonException("The configuration root must be an object.");
        }
        catch (JsonException exception)
        {
            return ConfigurationMigrationResult.Failed($"Invalid configuration JSON: {exception.Message}");
        }

        var warnings = new List<string>();
        var sourceVersion = ReadInt(root["ConfigVersion"]) ?? 0;
        var deviceRoot = AsObject(root["Device"]) ?? root;
        var selectedDevice = ReadString(deviceRoot["SelectedDevice"]);
        var devices = AsArray(deviceRoot["Devices"]);
        var selected = devices?.OfType<JsonObject>().FirstOrDefault(device =>
            string.Equals(ReadString(device["Name"]), selectedDevice, StringComparison.OrdinalIgnoreCase))
            ?? devices?.OfType<JsonObject>().FirstOrDefault();
        if (selectedDevice is null)
            selectedDevice = ReadString(selected?["Name"]);

        var axes = (AsArray(selected?["Axes"]) ?? [])
            .OfType<JsonObject>()
            .Select(axis => new MigratedAxis(
                ReadString(axis["Name"]) ?? "unknown",
                ReadBool(axis["Enabled"]) ?? false,
                Math.Clamp(ReadDouble(axis["DefaultValue"]) ?? 0, 0, 1)))
            .Where(axis => !string.Equals(axis.Name, "unknown", StringComparison.Ordinal))
            .ToArray();

        var outputs = ReadOutputs(root["OutputTarget"], warnings);
        var mediaSources = ReadMediaSources(root["MediaSource"], warnings);
        var scriptRoots = ReadScriptRoots(root, warnings);

        return new ConfigurationMigrationResult(true,
            new MigratedConfiguration(1, sourceVersion, selectedDevice, axes, outputs,
                mediaSources, scriptRoots, warnings), null);
    }

    private static IReadOnlyList<MigratedOutput> ReadOutputs(JsonNode? node, ICollection<string> warnings)
    {
        var items = AsArray(AsObject(node)?["Items"]);
        if (items is null)
            return [];

        var outputs = new List<MigratedOutput>();
        foreach (var item in items.OfType<JsonObject>())
        {
            var type = ReadString(item["Type"]) ?? ReadString(item["$type"]);
            var protocol = ProtocolFromType(type);
            if (protocol is null)
            {
                warnings.Add("Skipped unsupported output target type.");
                continue;
            }

            AddSecretWarnings(item, warnings);
            outputs.Add(new MigratedOutput(
                ReadString(item["Name"]) ?? $"{protocol}-{outputs.Count + 1}",
                protocol,
                NormalizePathOrEndpoint(ReadString(item["Endpoint"]) ?? ReadString(item["Uri"])),
                ReadBool(item["Enabled"]) ?? false));
        }

        return outputs;
    }

    private static IReadOnlyList<MigratedMediaSource> ReadMediaSources(JsonNode? node, ICollection<string> warnings)
    {
        var media = AsObject(node);
        if (media is null)
            return [];

        AddSecretWarnings(media, warnings);
        var selected = ReadString(media["Selected"]);
        var endpoint = NormalizePathOrEndpoint(ReadString(media["RemoteEndpoint"])
            ?? ReadString(media["Endpoint"]));
        return selected is null ? [] : [new MigratedMediaSource(selected, true, endpoint)];
    }

    private static IReadOnlyList<string> ReadScriptRoots(JsonObject root, ICollection<string> warnings)
    {
        var values = new List<string>();
        foreach (var key in new[] { "ScriptLibraries", "ScriptRoots", "Libraries" })
        {
            if (root[key] is not JsonArray array)
                continue;
            foreach (var item in array)
            {
                var path = item is JsonValue value && value.TryGetValue<string>(out var text)
                    ? NormalizePathOrEndpoint(text)
                    : null;
                if (!string.IsNullOrWhiteSpace(path))
                    values.Add(path);
            }
        }

        if (values.Any(path => path.Contains(":/", StringComparison.Ordinal)))
            warnings.Add("Windows drive paths were normalized but still require an explicit Linux mount mapping.");
        return values.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string? ProtocolFromType(string? type)
    {
        if (string.IsNullOrWhiteSpace(type)) return null;
        var name = type.ToLowerInvariant();
        if (name.Contains("tcp")) return "tcp";
        if (name.Contains("udp")) return "udp";
        if (name.Contains("websocket")) return "websocket";
        if (name.Contains("serial")) return "serial";
        if (name.Contains("file")) return "file";
        return null;
    }

    private static void AddSecretWarnings(JsonObject value, ICollection<string> warnings)
    {
        if (value.Any(property => IsSensitive(property.Key)))
            warnings.Add("Sensitive desktop settings were omitted and must be entered again.");
    }

    private static bool IsSensitive(string name)
        => SensitiveNames.Any(part => name.Contains(part, StringComparison.OrdinalIgnoreCase));

    private static JsonObject? AsObject(JsonNode? node) => node as JsonObject;
    private static JsonArray? AsArray(JsonNode? node) => node as JsonArray;

    private static string? ReadString(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var result) ? result : null;

    private static int? ReadInt(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<int>(out var result) ? result : null;

    private static bool? ReadBool(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<bool>(out var result) ? result : null;

    private static double? ReadDouble(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<double>(out var result) ? result : null;

    private static string? NormalizePathOrEndpoint(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim().Replace('\\', '/');
}
