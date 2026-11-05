using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using Odx.Core;
using Xunit;

namespace Odx.Tests;

/// <summary>Mp4Reader must agree with ffprobe. Set ODX_MP4_DIR to also check every .mp4 under that folder.</summary>
public class Mp4ReaderTests
{
    public static TheoryData<string, string> Variants => new()
    {
        { "320x180 60fps", "-f lavfi -i testsrc=size=320x180:rate=60 -frames:v 120" },
        { "640x360 30fps odd count", "-f lavfi -i testsrc=size=640x360:rate=30 -frames:v 97" },
        { "moov first (faststart)", "-f lavfi -i testsrc=size=320x180:rate=60 -frames:v 60 -movflags +faststart" },
        { "1920x1080 one frame", "-f lavfi -i testsrc=size=1920x1080:rate=60 -frames:v 1" },
        { "long, 60s at 60fps", "-f lavfi -i testsrc=size=160x90:rate=60 -frames:v 3600" },
    };

    [SkippableTheory]
    [MemberData(nameof(Variants))]
    public void Agrees_with_ffprobe(string name, string input)
    {
        Skip.IfNot(SessionFixtures.HasFfmpeg, "ffmpeg/ffprobe not on PATH");
        string path = Path.Combine(Path.GetTempPath(), $"odx-mp4-{Guid.NewGuid():N}.mp4");
        try
        {
            Run("ffmpeg", $"-v error -y {input} -c:v libx264 -preset ultrafast -bf 0 -pix_fmt yuv420p \"{path}\"");
            AssertMatchesFfprobe(path, name);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [SkippableFact]
    public void Sees_whether_there_is_a_sound_track()
    {
        Skip.IfNot(SessionFixtures.HasFfmpeg, "ffmpeg not on PATH");
        string dir = Path.Combine(Path.GetTempPath(), $"odx-mp4-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            SessionFixtures.MakeVideo(Path.Combine(dir, "silent.mp4"), nFrames: 30);
            SessionFixtures.MakeVideo(Path.Combine(dir, "sound.mp4"), nFrames: 30, audio: true);
            Assert.False(Mp4Reader.Read(Path.Combine(dir, "silent.mp4")).HasAudio);
            var sound = Mp4Reader.Read(Path.Combine(dir, "sound.mp4"));
            Assert.True(sound.HasAudio);
            Assert.Equal(30, sound.Frames);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [SkippableFact]
    public void Agrees_with_ffprobe_on_real_recordings()
    {
        string? dir = Environment.GetEnvironmentVariable("ODX_MP4_DIR");
        Skip.If(dir is null || !SessionFixtures.HasFfmpeg, "set ODX_MP4_DIR (and have ffprobe on PATH) to run");
        var files = Directory.GetFiles(dir!, "*.mp4", SearchOption.AllDirectories).Where(f => new FileInfo(f).Length > 0).ToList();
        Assert.NotEmpty(files);
        foreach (var f in files) AssertMatchesFfprobe(f, f);
    }

    [Fact]
    public void Rejects_files_that_are_not_mp4()
    {
        string path = Path.Combine(Path.GetTempPath(), $"odx-mp4-{Guid.NewGuid():N}.mp4");
        try
        {
            File.WriteAllText(path, "frame_index,t_grid_qpc_ms\n0,1\n");
            Assert.Throws<InvalidDataException>(() => Mp4Reader.Read(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void AssertMatchesFfprobe(string path, string name)
    {
        var probe = JsonNode.Parse(Run("ffprobe", "-v error -select_streams v:0 -count_packets -show_entries " +
                                                  $"stream=width,height,nb_read_packets:format=duration -of json \"{path}\""))!;
        if (probe["streams"]!.AsArray().Count == 0)
        {
            // e.g. a recording stopped before its first frame: header only, no video stream.
            Assert.Throws<InvalidDataException>(() => Mp4Reader.Read(path));
            return;
        }
        var stream = probe["streams"]![0]!;
        var info = Mp4Reader.Read(path);
        Assert.True(stream["width"]!.GetValue<int>() == info.Width && stream["height"]!.GetValue<int>() == info.Height,
            $"{name}: size {info.Width}x{info.Height}");
        Assert.True(long.Parse(stream["nb_read_packets"]!.GetValue<string>(), CultureInfo.InvariantCulture) == info.Frames,
            $"{name}: {info.Frames} frames vs ffprobe {stream["nb_read_packets"]}");
        double ffDuration = double.Parse(probe["format"]!["duration"]!.GetValue<string>(), CultureInfo.InvariantCulture);
        Assert.True(Math.Abs(ffDuration - info.DurationSeconds) < 0.001, $"{name}: {info.DurationSeconds} s vs ffprobe {ffDuration} s");
    }

    private static string Run(string exe, string args)
    {
        using var p = Process.Start(new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        string stdout = p.StandardOutput.ReadToEnd(), stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"{exe} failed: {stderr}");
        return stdout;
    }
}
