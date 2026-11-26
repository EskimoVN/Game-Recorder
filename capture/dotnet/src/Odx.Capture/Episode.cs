using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Odx.Core;
using Odx.Protocol;
using Parquet;
using Parquet.Schema;

namespace Odx.Capture;

/// <summary>What <see cref="Episode.Write"/> needs from a built capture.</summary>
/// <param name="FrameUtcMs">Each frame's grid time on the session's clock (Unix ms), one per <paramref name="Rows"/>.</param>
/// <param name="Inputs">The bound inputs and actions (<see cref="SessionBuilder.MapInputs"/>) with their Unix ms.</param>
/// <param name="Camera">The camera at its own rate (Unix ms, canonical C2W, intrinsics); null without camera samples.</param>
/// <param name="Player">player.json as <see cref="PlayerFile.Write"/> built it.</param>
/// <param name="SyncAccuracyMs">clock.sync_accuracy_ns in ms (TIM-05); null when not measured.</param>
public sealed record EpisodeSource(string CaptureId, string SessionId, double Fps, int Width, int Height,
    IReadOnlyList<FrameRow> Rows, IReadOnlyList<double> FrameUtcMs, IReadOnlyList<(double UtcMs, InputEvent Event)> Inputs,
    IReadOnlyList<(double UtcMs, double[,] C2w, Intrinsics Intrinsics)>? Camera, string? CameraModel, GameProfile Profile,
    JsonObject Player, double? SyncAccuracyMs);

/// <summary>
/// A player's round folder as one episode of the client's Data Delivery Format (§1, §4–§6, §8 games): episode.json,
/// metadata/games.json, videos/pov.mp4, frames.parquet, sensors.json, streams/actions.parquet and
/// streams/camera_pose.parquet, annotations/events.jsonl once the round's events are in. delivery.json and
/// manifest.parquet belong to the delivery, not the episode. raw/ keeps the working files and is not part of the episode.
/// </summary>
public static class Episode
{
    public const string FileName = "episode.json";
    public const string MetadataFile = "metadata/games.json";
    public const string FramesFile = "frames.parquet";
    public const string SensorsFile = "sensors.json";
    public const string ActionsFile = "streams/actions.parquet";
    public const string CameraPoseFile = "streams/camera_pose.parquet";
    public const string EventsFile = "annotations/events.jsonl";
    public const string VideoFile = VideoFinalize.Delivered;
    public const string CameraId = "pov";
    private const string WorldConvention = "world RFU right-handed: X right/east, Y forward/north, Z up";

    /// <summary>Our values where the format only shows robotics ones (capture/README.md, decided 2026-11-26).</summary>
    public const string CameraRole = "player_pov", CalibrationSource = "game_engine";

    /// <summary>Every encoder here writes H.264 yuv420p, limited range (FfmpegEncoder.ColorArgs, VideoTrim); MP4 boxes do not say.</summary>
    private const string PixFmt = "yuv420p", ColorRange = "tv";

    private static readonly string[] IntrinsicColumns = Frames.IntrinsicColumns;
    private static readonly string[] DistortionColumns =
        ["camera_radial_k1", "camera_radial_k2", "camera_radial_k3", "camera_radial_k4", "camera_radial_k5", "camera_radial_k6",
         "camera_tangential_p1", "camera_tangential_p2"];

    /// <summary>Writes every episode file but the video and the events, and episode.json without sizes and checksums (<see cref="Seal"/>).</summary>
    public static void Write(string dir, EpisodeSource s, CaptureConfig config)
    {
        foreach (string sub in new[] { MetadataFile, ActionsFile }) Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(dir, sub))!);
        var frameNs = s.FrameUtcMs.Select(Ns).ToArray();
        WriteFrames(Path.Combine(dir, FramesFile), s.Rows, frameNs);
        WriteActions(Path.Combine(dir, ActionsFile), s.Inputs);
        string posePath = Path.Combine(dir, CameraPoseFile);
        if (s.Camera is { } camera) WriteCameraPose(posePath, camera);
        else File.Delete(posePath);
        WriteJson(Path.Combine(dir, SensorsFile), Sensors(s));
        WriteJson(Path.Combine(dir, MetadataFile), Metadata(s));

        double durationS = s.Rows.Count / s.Fps;
        long startNs = frameNs[0];
        var timebase = new JsonObject { ["clock"] = "episode", ["column"] = "timestamp_ns" };
        var artifacts = new JsonArray
        {
            Artifact("frames", "frames", FramesFile, config, timebase, s.Rows.Count),
            Artifact("actions", "actions", ActionsFile, config, timebase),
        };
        if (s.Camera is not null) artifacts.Add(Artifact("camera-pose", "camera_pose", CameraPoseFile, config, timebase));
        artifacts.Add(Artifact("sensors", "sensors", SensorsFile, config, null));

        string path = Path.Combine(dir, FileName);
        // REV-03: a revision's timestamp is allocated once and kept when the folder is built again.
        string revisionAt = File.Exists(path) && JsonNode.Parse(File.ReadAllText(path))?["revision_at"]?.GetValue<string>() is { } kept
            ? kept
            : UtcText.IsoUs(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var episode = new JsonObject
        {
            ["metadata_schema_version"] = config.MetadataSchemaVersion,
            ["item_id"] = s.CaptureId,
            ["revision_at"] = revisionAt,
            ["replaces_item_id"] = null,
            ["replaces_revision_at"] = null,
            ["replaces_metadata_uri"] = null,
            ["recording_id"] = s.CaptureId,
            ["session_id"] = s.SessionId,
            ["duration_s"] = durationS,
            ["clock"] = new JsonObject
            {
                ["domain"] = "utc_unix",
                ["unit"] = "ns",
                ["capture_start_ns"] = startNs,
                ["capture_end_ns"] = startNs + (long)Math.Round(durationS * 1e9),
                ["sync_accuracy_ns"] = s.SyncAccuracyMs is double ms ? (long)Math.Round(ms * 1e6) : null,
            },
            ["domain_metadata"] = new JsonObject { ["path"] = MetadataFile, ["size_bytes"] = null, ["sha256"] = null },
            ["cameras"] = new JsonArray(new JsonObject
            {
                ["camera_id"] = CameraId,
                ["role"] = CameraRole,
                ["is_primary"] = true,
                ["path"] = VideoFile,
                ["size_bytes"] = null,
                ["sha256"] = null,
                ["width"] = s.Width,
                ["height"] = s.Height,
                ["fps"] = s.Fps,
                ["frame_count"] = s.Rows.Count,
                ["duration_s"] = durationS,
                ["first_frame_ns"] = startNs,
                // The frame times are already on the session clock; nothing is shifted afterwards.
                ["time_offset_ns"] = 0,
                ["codec"] = null,
                ["bitrate_bps"] = null,
                ["pix_fmt"] = PixFmt,
                ["color_range"] = ColorRange,
                ["keyframe_interval_s"] = null,
                ["calibration_source"] = s.Camera is null ? null : CalibrationSource,
            }),
            ["artifacts"] = artifacts,
        };
        WriteJson(path, episode);
    }

    /// <summary>
    /// Fills episode.json's sizes and SHA-256 (PTH-04) and the video's encoding from the file, lists
    /// annotations/events.jsonl once it is there and drops artifacts whose file is gone. Nothing to do without episode.json.
    /// </summary>
    public static void Seal(string dir, CaptureConfig? config = null)
    {
        string path = Path.Combine(dir, FileName);
        if (!File.Exists(path)) return;
        var episode = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var artifacts = episode["artifacts"]!.AsArray();
        if (File.Exists(Path.Combine(dir, EventsFile)) && artifacts.All(a => a!["path"]!.GetValue<string>() != EventsFile))
            artifacts.Add(Artifact("events", "annotations", EventsFile, config ?? CaptureConfig.Load(), null));
        string round = CaptureFolder.Raw(dir, RoundRecord.FileName);
        if (File.Exists(round)) AddRoundPlayers(Path.Combine(dir, MetadataFile), RoundRecord.Load(Path.GetDirectoryName(round)!));
        foreach (var gone in artifacts.Where(a => !File.Exists(Path.Combine(dir, a!["path"]!.GetValue<string>()))).ToList())
            artifacts.Remove(gone);
        foreach (var node in artifacts.Append(episode["domain_metadata"]).Concat(episode["cameras"]!.AsArray()))
            Checksum(dir, node!.AsObject());

        var camera = episode["cameras"]![0]!.AsObject();
        string video = Path.Combine(dir, VideoFile);
        // A killed recorder leaves a video without moov: the encoding fields stay null, the checksums are still right.
        if (File.Exists(video) && TryReadMp4(video) is { } mp4)
        {
            camera["codec"] = mp4.Codec is "avc1" or "avc3" ? "h264" : mp4.Codec;
            camera["bitrate_bps"] = mp4.DurationSeconds > 0 ? (long)Math.Round(mp4.VideoBytes * 8 / mp4.DurationSeconds) : null;
            camera["keyframe_interval_s"] = mp4.KeyFrames > 0 ? Math.Round(mp4.DurationSeconds / mp4.KeyFrames, 3) : null;
        }
        WriteJson(path, episode);
    }

    private static Mp4Info? TryReadMp4(string path)
    {
        try
        {
            return Mp4Reader.Read(path);
        }
        catch (Exception e) when (e is InvalidDataException or EndOfStreamException)
        {
            return null;
        }
    }

    private static void Checksum(string dir, JsonObject entry)
    {
        string file = Path.Combine(dir, entry["path"]!.GetValue<string>());
        if (!File.Exists(file)) return;
        using var stream = File.OpenRead(file);
        entry["size_bytes"] = stream.Length;
        entry["sha256"] = Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static JsonObject Artifact(string id, string role, string path, CaptureConfig config, JsonObject? timebase,
        int? rowCount = null)
    {
        var (schemaId, schemaVersion) = config.ArtifactSchemas[id];
        var artifact = new JsonObject
        {
            ["artifact_id"] = id,
            ["role"] = role,
            ["path"] = path,
            ["schema_id"] = schemaId,
            ["schema_version"] = schemaVersion,
            ["size_bytes"] = null,
            ["sha256"] = null,
        };
        if (rowCount is int rows) artifact["row_count"] = rows;
        if (timebase is not null) artifact["timebase"] = timebase.DeepClone();
        return artifact;
    }

    /// <summary>VTM games: title/platform, the participant and their camera, the input mappings.</summary>
    private static JsonObject Metadata(EpisodeSource s)
    {
        var p = s.Profile.Json;
        var participant = new JsonObject
        {
            ["player_id"] = s.Player["player"]?["player_id"]?.DeepClone(),
            ["name"] = s.Player["player"]?["name"]?.DeepClone(),
            ["camera_id"] = CameraId,
            ["network"] = s.Player["network"]?.DeepClone(),
            ["clock_sync"] = s.Player["clock_sync"]?.DeepClone(),
        };
        return new JsonObject
        {
            ["game_title"] = s.Profile.GameTitle,
            ["platform"] = s.Profile.Platform,
            ["engine"] = p["engine"]?.DeepClone(),
            ["localization"] = s.Profile.Localization,
            // The poses are delivered in the canonical world, not the engine's axes (kept as game_axes).
            ["coordinate_system"] = new JsonObject
            {
                ["axes"] = WorldConvention,
                ["units"] = "meter",
                ["game_axes"] = p["coordinate_system"]?["axes"]?.DeepClone(),
            },
            ["participants"] = new JsonArray(participant),
            ["input_mappings"] = new JsonObject
            {
                ["bindings"] = p["bindings"]?.DeepClone(),
                ["mouse_convention"] = s.Profile.InputMouseConvention.DeepClone(),
            },
            ["screen"] = new JsonObject
            {
                ["width_px"] = s.Player["screen_width_px"]?.DeepClone(),
                ["height_px"] = s.Player["screen_height_px"]?.DeepClone(),
            },
            ["capture"] = new JsonObject
            {
                ["tool_version"] = BuildInfo.Version,
                ["recording"] = s.Player["recording"]?.DeepClone(),
            },
        };
    }

    /// <summary>
    /// VTM-04: every player of the round (round.json) in participants, this episode's own first; the others have their own
    /// episodes in the same session_id, so their camera_id is null here.
    /// </summary>
    private static void AddRoundPlayers(string metadataPath, RoundRecord round)
    {
        if (!File.Exists(metadataPath)) return;
        var metadata = JsonNode.Parse(File.ReadAllText(metadataPath))!.AsObject();
        var participants = metadata["participants"]!.AsArray();
        var own = participants.Where(p => (string?)p!["camera_id"] == CameraId).Select(p => p!.DeepClone()).ToList();
        var ownIds = own.Select(p => (string?)p["player_id"]).ToHashSet();
        metadata["participants"] = new JsonArray([.. own, .. round.Players.Where(p => !ownIds.Contains(p.PlayerId))
            .Select(p => new JsonObject { ["player_id"] = p.PlayerId, ["name"] = p.Name, ["camera_id"] = null })]);
        WriteJson(metadataPath, metadata);
    }

    /// <summary>FRM-05: one record per sensor; the game camera's intrinsics and pose change every frame, so they point at the columns.</summary>
    private static JsonObject Sensors(EpisodeSource s)
    {
        bool camera = s.Camera is not null;
        return new JsonObject
        {
            ["sensors"] = new JsonArray(new JsonObject
            {
                ["sensor_id"] = CameraId,
                ["type"] = "camera",
                ["camera_id"] = CameraId,
                ["width"] = s.Width,
                ["height"] = s.Height,
                ["camera_model"] = s.CameraModel?.ToLowerInvariant(),
                ["intrinsics"] = camera
                    ? new JsonObject
                    {
                        ["per_frame"] = true,
                        ["artifact_id"] = "frames",
                        ["columns"] = new JsonArray(IntrinsicColumns.Select(c => (JsonNode)c).ToArray()),
                    }
                    : null,
                ["distortion"] = camera ? new JsonObject { ["model"] = "none", ["coefficients"] = new JsonArray() } : null,
                ["extrinsics"] = camera
                    ? new JsonObject
                    {
                        ["parent_frame"] = "world",
                        ["child_frame"] = "camera",
                        ["direction"] = "camera_to_world",
                        ["convention"] = $"{WorldConvention}; C2W columns right, up, backward, translation (spec v2 §1.1.2.4)",
                        ["per_frame"] = true,
                        ["artifact_id"] = "frames",
                        ["columns"] = new JsonArray(Frames.C2wColumns.Select(c => (JsonNode)c).ToArray()),
                        ["stream_artifact_id"] = "camera-pose",
                    }
                    : null,
            }),
        };
    }

    /// <summary>FRM-01–03: one row per video frame, every column present, null where there is no value.</summary>
    private static void WriteFrames(string path, IReadOnlyList<FrameRow> rows, long[] frameNs)
    {
        var table = new ParquetTable()
            .Add("frame_id", rows.Select(r => r.FrameId).ToArray())
            .Add("camera_frame_index", rows.Select((_, i) => (long)i).ToArray())
            .Add("timestamp_ns", frameNs)
            // Every row is a frame of the video; whether a repeated (duplicate) frame counts as invalid is still open.
            .Add("image_valid", rows.Select(_ => true).ToArray());
        for (int k = 0; k < 16; k++)
        {
            int i = k;
            table.Add(Frames.C2wColumns[i], rows.Select(r => r.C2w is { } m ? m[i / 4, i % 4] : (double?)null).ToArray());
        }
        table.Add("camera_model", rows.Select(r => r.CameraModel?.ToLowerInvariant()).ToArray());
        table.Add(IntrinsicColumns[0], rows.Select(r => r.Intrinsics?.Fx).ToArray())
            .Add(IntrinsicColumns[1], rows.Select(r => r.Intrinsics?.Fy).ToArray())
            .Add(IntrinsicColumns[2], rows.Select(r => r.Intrinsics?.Cx).ToArray())
            .Add(IntrinsicColumns[3], rows.Select(r => r.Intrinsics?.Cy).ToArray());
        foreach (string column in DistortionColumns) table.Add(column, new double?[rows.Count]);
        table.Add("input_keys", rows.Select(r => r.Input.Keys).ToArray())
            .Add("input_actions", rows.Select(r => r.Input.Actions).ToArray())
            .Add("input_mouse_buttons", rows.Select(r => r.Input.MouseButtons).ToArray())
            .Add("input_mouse_dx", rows.Select(r => r.Input.MouseDx).ToArray())
            .Add("input_mouse_dy", rows.Select(r => r.Input.MouseDy).ToArray())
            .Write(path);
    }

    /// <summary>FRM-04: the inputs at their own times, as input_events.csv has them, keys and buttons bound to actions.</summary>
    private static void WriteActions(string path, IReadOnlyList<(double UtcMs, InputEvent Event)> inputs) =>
        new ParquetTable()
            .Add("timestamp_ns", inputs.Select(i => Ns(i.UtcMs)).ToArray())
            .Add("kind", inputs.Select(i => (string?)InputLogWriter.KindName(i.Event.Kind)).ToArray())
            .Add("value", inputs.Select(i => i.Event.Value.Length == 0 ? null : i.Event.Value).ToArray())
            .Add("dx", inputs.Select(i => i.Event.Dx).ToArray())
            .Add("dy", inputs.Select(i => i.Event.Dy).ToArray())
            .Write(path);

    private static void WriteCameraPose(string path, IReadOnlyList<(double UtcMs, double[,] C2w, Intrinsics Intrinsics)> samples)
    {
        var table = new ParquetTable().Add("timestamp_ns", samples.Select(s => Ns(s.UtcMs)).ToArray());
        for (int k = 0; k < 16; k++)
        {
            int i = k;
            table.Add(Frames.C2wColumns[i], samples.Select(s => s.C2w[i / 4, i % 4]).ToArray());
        }
        table.Add(IntrinsicColumns[0], samples.Select(s => s.Intrinsics.Fx).ToArray())
            .Add(IntrinsicColumns[1], samples.Select(s => s.Intrinsics.Fy).ToArray())
            .Add(IntrinsicColumns[2], samples.Select(s => s.Intrinsics.Cx).ToArray())
            .Add(IntrinsicColumns[3], samples.Select(s => s.Intrinsics.Cy).ToArray())
            .Write(path);
    }

    /// <summary>TIM-01: Unix nanoseconds, to the microsecond (a double holds Unix ms to well under 1 µs; ns would not be exact).</summary>
    public static long Ns(double unixMs) => (long)Math.Round(unixMs * 1000) * 1000;

    private static void WriteJson(string path, JsonObject json) => File.WriteAllText(path, json.ToJsonString(SessionBuilder.Json));

    /// <summary>One row group of named columns, written in the order added.</summary>
    private sealed class ParquetTable
    {
        private readonly List<(DataField Field, Func<ParquetRowGroupWriter, Task> Write)> _columns = [];

        public ParquetTable Add<T>(string name, T[] values) where T : struct
        {
            var field = new DataField<T>(name, false);
            _columns.Add((field, g => g.WriteAsync<T>(field, values)));
            return this;
        }

        public ParquetTable Add<T>(string name, T?[] values) where T : struct
        {
            var field = new DataField<T?>(name, true);
            _columns.Add((field, g => g.WriteAsync<T>(field, values)));
            return this;
        }

        public ParquetTable Add(string name, string?[] values)
        {
            var field = new DataField<string>(name, true);
            _columns.Add((field, g => g.WriteAsync(field, values!)));
            return this;
        }

        // On the thread pool: the writer is async, and a caller on a UI thread must not wait on its own context.
        public void Write(string path) => Task.Run(async () =>
        {
            await using var stream = File.Create(path);
            await using var writer = await ParquetWriter.CreateAsync(new ParquetSchema(_columns.Select(c => (Field)c.Field).ToArray()), stream);
            using var group = writer.CreateRowGroup();
            foreach (var (_, write) in _columns) await write(group);
        }).GetAwaiter().GetResult();
    }
}
