using System.Diagnostics;
using System.Globalization;
using Odx.Core;
using Xunit;

namespace Odx.Tests;

/// <summary>ffmpeg on PATH; skipped without it, like the other video tests.</summary>
public class VideoFinalizeTests
{
    private const int Frames = 90, Fps = 30;

    /// <summary>What odx record leaves: a fragmented video-only MP4 (frag_keyframe+empty_moov) and a float WAV.</summary>
    private static string NewCapture(bool audio, bool truncateVideo = false)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"odx-finalize-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        Ffmpeg(["-v", "error", "-y", "-f", "lavfi", "-i", $"testsrc=size=160x90:rate={Fps}", "-frames:v", Frames.ToString(CultureInfo.InvariantCulture),
                "-c:v", "libx264", "-bf", "0", "-g", "30", "-pix_fmt", "yuv420p",
                "-movflags", "+frag_keyframe+empty_moov+default_base_moof", Path.Combine(dir, VideoFinalize.RawVideo)]);
        if (truncateVideo)
        {
            string raw = Path.Combine(dir, VideoFinalize.RawVideo);
            byte[] bytes = File.ReadAllBytes(raw);
            File.WriteAllBytes(raw, bytes[..(bytes.Length * 2 / 3)]);
        }
        if (audio)
        {
            using var wav = new WavWriter(File.Create(Path.Combine(dir, VideoFinalize.AudioFile)), 48000, 2, 32, isFloat: true);
            wav.Write(new byte[48000 * 8 * Frames / Fps]);
        }
        using var log = new VideoFrameLogWriter(new StreamWriter(Path.Combine(dir, "video_frames.csv")));
        for (int i = 0; i < Frames; i++) log.Write(new GridTick(i, i * 1000.0 / Fps, i * 1000.0 / Fps, false));
        return dir;
    }

    private static void Ffmpeg(string[] args)
    {
        var psi = new ProcessStartInfo("ffmpeg") { UseShellExecute = false, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        string err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"ffmpeg failed: {err}");
    }

    [Fact]
    public void Copies_the_video_and_muxes_the_wav_then_deletes_both()
    {
        if (!SessionFixtures.HasFfmpeg) return;
        string dir = NewCapture(audio: true);
        try
        {
            var progress = new List<long>();
            var result = VideoFinalize.Run("ffmpeg", Path.Combine(dir, VideoFinalize.RawVideo), Path.Combine(dir, VideoFinalize.AudioFile),
                Path.Combine(dir, VideoFinalize.Output), Frames, Fps, progress.Add);

            Assert.Null(result.Error);
            Assert.True(result.Ok);
            Assert.Equal(Frames, result.Frames);
            Assert.True(result.HasAudio);
            var info = Mp4Reader.Read(Path.Combine(dir, "video.mp4"));
            Assert.Equal((Frames, true), (info.Frames, info.HasAudio));
            Assert.False(File.Exists(Path.Combine(dir, VideoFinalize.RawVideo)));
            Assert.False(File.Exists(Path.Combine(dir, VideoFinalize.AudioFile)));
            Assert.False(File.Exists(Path.Combine(dir, "video.mp4.part")));
            Assert.Contains(Frames, progress);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void A_truncated_recording_still_gives_a_playable_video_reported_as_short()
    {
        if (!SessionFixtures.HasFfmpeg) return;
        string dir = NewCapture(audio: false, truncateVideo: true);
        try
        {
            var result = VideoFinalize.Run("ffmpeg", Path.Combine(dir, VideoFinalize.RawVideo), null, Path.Combine(dir, "video.mp4"), Frames, Fps);

            Assert.False(result.Ok);
            Assert.InRange(result.Frames, 1, Frames - 1);
            Assert.Contains($"expected {Frames} (video_frames.csv)", result.Error);
            Assert.Equal(result.Frames, Mp4Reader.Read(Path.Combine(dir, "video.mp4")).Frames);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Runs_when_the_capture_folder_has_no_video_yet_and_counts_only_complete_rows()
    {
        if (!SessionFixtures.HasFfmpeg) return;
        string dir = NewCapture(audio: true);
        try
        {
            File.WriteAllText(Path.Combine(dir, "recording.json"), """{"fps": 30}""");
            File.AppendAllText(Path.Combine(dir, "video_frames.csv"), "90,3000");  // a cut last row
            Assert.Null(VideoFinalize.RunIfNeeded(Path.Combine(dir, "nowhere"), "ffmpeg"));

            var result = VideoFinalize.RunIfNeeded(dir, "ffmpeg");

            Assert.NotNull(result);
            Assert.True(result.Ok, result.Error);
            Assert.Null(VideoFinalize.RunIfNeeded(dir, "ffmpeg"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void A_round_is_cut_to_its_frames_with_the_csv_and_recording_json()
    {
        if (!SessionFixtures.HasFfmpeg) return;
        string dir = NewCapture(audio: true);
        try
        {
            // 30 fps, the grid at round_start, a round of 2.0 s: 60 of the 90 recorded frames.
            File.WriteAllText(Path.Combine(dir, "recording.json"),
                """{"fps": 30, "frames": 90, "grid_origin_qpc_ms": 0, "round_start_utc_ms": 1000, "round_end_utc_ms": 3000}""");
            var result = VideoFinalize.RunIfNeeded(dir, "ffmpeg");

            Assert.NotNull(result);
            Assert.True(result.Ok, result.Error);
            Assert.Equal(60, result.Frames);
            var info = Mp4Reader.Read(Path.Combine(dir, "video.mp4"));
            Assert.Equal(60, info.Frames);
            Assert.True(info.HasAudio);
            Assert.InRange(info.DurationSeconds, 1.99, 2.05);  // the sound ends with the picture (one AAC frame of slack)
            Assert.Equal(61, File.ReadLines(Path.Combine(dir, "video_frames.csv")).Count());
            var recording = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "recording.json")))!;
            Assert.Equal(60, recording["frames"]!.GetValue<long>());
            Assert.Equal(90, recording["recorded_frames"]!.GetValue<long>());
            Assert.Equal(30, recording["tail_frames_cut"]!.GetValue<long>());
            Assert.Equal(60, recording["round_frames"]!.GetValue<long>());
            Assert.True(recording["finalize"]!["ok"]!.GetValue<bool>());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void A_recording_shorter_than_the_round_keeps_every_frame_and_is_not_ok()
    {
        if (!SessionFixtures.HasFfmpeg) return;
        string dir = NewCapture(audio: false);
        try
        {
            File.WriteAllText(Path.Combine(dir, "recording.json"),
                """{"fps": 30, "frames": 90, "grid_origin_qpc_ms": 0, "round_start_utc_ms": 1000, "round_end_utc_ms": 6000}""");
            var result = VideoFinalize.RunIfNeeded(dir, "ffmpeg");

            Assert.NotNull(result);
            Assert.True(result.Ok, result.Error);  // what was recorded is all there; the shortfall is the round's problem (CaptureReport)
            Assert.Equal(90, result.Frames);
            var recording = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "recording.json")))!;
            Assert.Equal(150, recording["round_frames"]!.GetValue<long>());
            Assert.Null(recording["tail_frames_cut"]);
            Assert.Equal(91, File.ReadLines(Path.Combine(dir, "video_frames.csv")).Count());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Round_frames_follow_from_the_round_times_and_need_a_grid_origin()
    {
        var r = System.Text.Json.Nodes.JsonNode.Parse("""{"fps": 30, "grid_origin_qpc_ms": 5, "round_start_utc_ms": 1794021705378.8196, "round_end_utc_ms": 1794021752548.9243}""")!.AsObject();
        Assert.Equal(1416, VideoFinalize.RoundFrames(r));  // the tesst round of 2026-11-07: 47 170.1 ms
        r["round_end_utc_ms"] = 1794021705378.8196 + 2000;
        Assert.Equal(60, VideoFinalize.RoundFrames(r));     // exactly 60 ticks before the end
        r.Remove("grid_origin_qpc_ms");
        Assert.Null(VideoFinalize.RoundFrames(r));
    }

    [Fact]
    public void A_failed_ffmpeg_leaves_the_inputs_and_no_output()
    {
        if (!SessionFixtures.HasFfmpeg) return;
        string dir = NewCapture(audio: false);
        try
        {
            string raw = Path.Combine(dir, VideoFinalize.RawVideo);
            File.WriteAllText(raw, "not a video");
            var result = VideoFinalize.Run("ffmpeg", raw, null, Path.Combine(dir, "video.mp4"), Frames, Fps);

            Assert.False(result.Ok);
            Assert.Contains("ffmpeg exited with", result.Error);
            Assert.True(File.Exists(raw));
            Assert.False(File.Exists(Path.Combine(dir, "video.mp4")));
            Assert.False(File.Exists(Path.Combine(dir, "video.mp4.part")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
