using System.Diagnostics;
using System.Globalization;
using Odx.Core;
using Odx.Merge;
using Xunit;

namespace Odx.Tests;

public class VideoTrimTests
{
    [SkippableFact]
    public void Cuts_the_head_frame_accurately_and_counts_decoded_frames()
    {
        Skip.IfNot(SessionFixtures.HasFfmpeg, "needs ffmpeg");
        string dir = Path.Combine(Path.GetTempPath(), $"odx-trim-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            string src = Path.Combine(dir, "src.mp4"), dst = Path.Combine(dir, "dst.mp4");
            SessionFixtures.MakeVideo(src, nFrames: 100, fps: 60, audio: true);
            Assert.Equal(100, VideoTrim.DecodedFrameCount("ffmpeg", src));

            VideoTrim.Trim("ffmpeg", src, dst, 7, 60, hasAudio: true);

            var info = Mp4Reader.Read(dst);
            Assert.Equal(93, info.Frames);
            Assert.True(info.HasAudio);
            Assert.Equal(93, VideoTrim.DecodedFrameCount("ffmpeg", dst));
            Assert.InRange(info.DurationSeconds, 93.0 / 60 - 0.05, 93.0 / 60 + 0.05);
            // Frame 0 of the cut is frame 7 of the source: testsrc draws a frame counter, so the neighbour differs more.
            byte[] expected = Luma(src, 7), actual = Luma(dst, 0), other = Luma(src, 6);
            Assert.InRange(MeanAbsDiff(expected, actual), 0, 3);
            Assert.True(MeanAbsDiff(other, actual) > MeanAbsDiff(expected, actual));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Only_the_spec_tree_belongs_in_a_delivery()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"odx-delivery-{Guid.NewGuid():N}");
        try
        {
            foreach (string f in new[] { "session.json", "validation.json", "schema.bin.gz", "server/events.jsonl", "server/packets.bin",
                         "players/p1/player.json", "players/p1/frames.csv", "players/p1/frames.bin", "players/p1/video.mp4",
                         "players/p1/events.jsonl", "players/p1/key_bindings.json", "players/p1/packets.txt",
                         "players/p1/capture/session.json", "players/p1/frames_bin.bin", "server/capture/demo.dem", "notes.txt" })
            {
                string path = Path.Combine(dir, f.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, "");
            }
            Assert.Equal(["notes.txt", "players/p1/capture/session.json", "players/p1/frames_bin.bin", "server/capture/demo.dem"],
                DeliveryFiles.Unexpected(dir));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private static byte[] Luma(string video, int frame)
    {
        var psi = new ProcessStartInfo("ffmpeg") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string a in new[] { "-v", "error", "-i", video, "-vf", $"select=eq(n\\,{frame.ToString(CultureInfo.InvariantCulture)})",
                     "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "gray", "-" })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        using var output = new MemoryStream();
        p.StandardOutput.BaseStream.CopyTo(output);
        p.WaitForExit();
        return output.ToArray();
    }

    private static double MeanAbsDiff(byte[] a, byte[] b)
    {
        Assert.Equal(a.Length, b.Length);
        return a.Zip(b, (x, y) => Math.Abs(x - y)).Average();
    }
}
