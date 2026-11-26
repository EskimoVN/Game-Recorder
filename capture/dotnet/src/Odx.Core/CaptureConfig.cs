using System.Text.Json;
using System.Text.Json.Nodes;

namespace Odx.Core;

/// <summary>
/// Deployment settings in odx-capture.json next to the executables, preset once for every machine of a team instead of
/// in each user's settings. A missing file or field leaves the user's own setting in charge.
/// </summary>
/// <param name="TimeServer">NTP server ("host" or "host:port") every machine measures its clock against (spec v3 §4 baseline).</param>
/// <param name="Orchestrator">odx-orchestrator's address (http://host:5180); players then only type a room's invite code.
/// Null: odx-app records on its own only.</param>
/// <param name="AccessKey">Shared secret the orchestrator asks of every client (its --access-key); null sends none.</param>
/// <param name="VendorName">vendor_name of the client's delivery format (player.json as spelled, lower case in
/// session.json and the delivery folder).</param>
/// <param name="FramesBinMagic">magic of frames_bin.bin's FileHeader in the client's format.</param>
/// <param name="FramesBinRecordType">record_type of frames_bin.bin's FileHeader in the client's format.</param>
/// <remarks>MetadataSchemaVersion and ArtifactSchemas: episode.json's metadata_schema_version and each artifact's schema_id /
/// schema_version by artifact_id, which the client publishes (Data Delivery Format VER-01, VER-03); until then the defaults
/// are ours. A config entry replaces the default's fields it names.
/// The client's format names stay out of the repository (NDA): publish.ps1 presets them from the CI variables; the
/// defaults are placeholders.</remarks>
public sealed record CaptureConfig(string? TimeServer, Uri? Orchestrator = null, string? AccessKey = null,
    string VendorName = "Vendor", string FramesBinMagic = "CLIENT-MP", string FramesBinRecordType = "client.mp.v1.FrameRecord")
{
    public const string FileName = "odx-capture.json";

    public string MetadataSchemaVersion { get; init; } = "1.0.0";

    public IReadOnlyDictionary<string, (string Id, string Version)> ArtifactSchemas { get; init; } = DefaultArtifactSchemas;

    private static readonly Dictionary<string, (string Id, string Version)> DefaultArtifactSchemas = new()
    {
        ["frames"] = ("games.frames", "1.0.0"),
        ["actions"] = ("games.actions", "1.0.0"),
        ["camera-pose"] = ("games.camera_pose", "1.0.0"),
        ["sensors"] = ("games.sensors", "1.0.0"),
        ["events"] = ("games.events", "1.0.0"),
    };

    /// <exception cref="InvalidDataException">The file exists but is not valid.</exception>
    public static CaptureConfig Load(string? dir = null)
    {
        string path = Path.Combine(dir ?? AppContext.BaseDirectory, FileName);
        if (!File.Exists(path)) return new CaptureConfig((string?)null);
        try
        {
            var json = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
                       ?? throw new InvalidDataException($"{path}: not a JSON object");
            string? server = json["time_server"]?.GetValue<string>().Trim();
            string? orchestrator = json["orchestrator"]?.GetValue<string>().Trim();
            Uri? uri = null;
            if (!string.IsNullOrEmpty(orchestrator) &&
                (!Uri.TryCreate(orchestrator, UriKind.Absolute, out uri) || uri.Scheme is not ("http" or "https")))
                throw new InvalidDataException($"{path}: orchestrator must be an http:// address, not \"{orchestrator}\"");
            var config = new CaptureConfig(NullIfEmpty(server), uri, NullIfEmpty(json["access_key"]?.GetValue<string>().Trim()));
            if (NullIfEmpty(json["vendor_name"]?.GetValue<string>().Trim()) is { } vendor) config = config with { VendorName = vendor };
            if (NullIfEmpty(json["frames_bin_magic"]?.GetValue<string>().Trim()) is { } magic) config = config with { FramesBinMagic = magic };
            if (NullIfEmpty(json["frames_bin_record_type"]?.GetValue<string>().Trim()) is { } type)
                config = config with { FramesBinRecordType = type };
            if (NullIfEmpty(json["metadata_schema_version"]?.GetValue<string>().Trim()) is { } version)
                config = config with { MetadataSchemaVersion = version };
            if (json["artifact_schemas"] is JsonObject schemas)
            {
                var merged = new Dictionary<string, (string Id, string Version)>(DefaultArtifactSchemas);
                foreach (var (id, node) in schemas)
                {
                    merged.TryGetValue(id, out var schema);
                    string? schemaId = NullIfEmpty(node?["schema_id"]?.GetValue<string>().Trim()) ?? schema.Id;
                    string? schemaVersion = NullIfEmpty(node?["schema_version"]?.GetValue<string>().Trim()) ?? schema.Version;
                    if (schemaId is null || schemaVersion is null)
                        throw new InvalidDataException($"{path}: artifact_schemas.{id} needs schema_id and schema_version");
                    merged[id] = (schemaId, schemaVersion);
                }
                config = config with { ArtifactSchemas = merged };
            }
            return config;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            throw new InvalidDataException($"{path}: {e.Message}", e);
        }
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;
}
