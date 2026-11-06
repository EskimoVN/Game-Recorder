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
            Assert.Contains($"video_frames.csv {Frames}", result.Error);
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
