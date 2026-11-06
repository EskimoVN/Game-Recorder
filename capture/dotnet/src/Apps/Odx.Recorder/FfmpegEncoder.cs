using System.Diagnostics;

namespace Odx.Recorder;

/// <summary>
/// Encodes raw NV12 frames piped to an ffmpeg process into a constant-frame-rate MP4 without B-frames, with an AAC track
/// from an <see cref="AudioInput"/> when given.
/// </summary>
internal sealed class FfmpegEncoder : IDisposable
{
    private readonly Process _proc;
    private readonly Stream _stdin;

    /// <summary>nvenc, mf or x264.</summary>
    public string Encoder { get; }

    public FfmpegEncoder(string ffmpeg, string output, int width, int height, double fps, string encoder, AudioInput? audio = null)
    {
        Encoder = encoder;
        string codec = encoder switch
        {
            "nvenc" => "-c:v h264_nvenc -preset p4 -rc vbr -cq 23 -bf 0 -g 120",
            "x264" => "-c:v libx264 -preset veryfast -crf 20 -bf 0 -g 120",
            // Media Foundation's hardware H.264 encoder: Intel Quick Sync, AMD or NVIDIA, whichever GPU has one.
            "mf" => "-c:v h264_mf -hw_encoding 1 -rate_control quality -quality 70 -bf 0 -g 120",
            _ => throw new ArgumentException($"unknown encoder '{encoder}' (nvenc|mf|x264)"),
        };
        var fr = fps.ToString(System.Globalization.CultureInfo.InvariantCulture);
        // The audio input comes first: ffmpeg opens inputs in order, and the pipe's writer waits for it to connect.
        // Both inputs start at timestamp 0, so audio sample 0 is video frame 0. The audio queue holds ~20 s of 4 KB
        // packets, so audio running ahead of a stalled video does not block its writer.
        var (audioIn, audioOut) = audio is null ? ("", "")
            : ($"-thread_queue_size 1024 -f {audio.SampleFormat} -ar {audio.SampleRate} -ac {audio.Channels} -i \"{audio.Pipe}\" ",
               "-map 1:v -map 0:a -c:a aac -b:a 160k ");
        var args = $"-y -loglevel error {audioIn}-f rawvideo -pix_fmt nv12 -s {width}x{height} -framerate {fr} -i - " +
                   $"{audioOut}{codec} -r {fr} \"{output}\"";
        _proc = Process.Start(new ProcessStartInfo(ffmpeg, args)
        {
            RedirectStandardInput = true,
            UseShellExecute = false,
        }) ?? throw new InvalidOperationException("cannot start ffmpeg");
        _stdin = _proc.StandardInput.BaseStream;    }

    public void Write(byte[] bgraFrame) => _stdin.Write(bgraFrame, 0, bgraFrame.Length);

    public void EndVideo() => _stdin.Close();

    /// <summary>
    /// Ends the video input and waits for ffmpeg to finish the file. -1 when it has not exited within <paramref name="wait"/>
    /// (a hardware encoder that never drains, or an input it still waits on): it is killed, and the file is unusable.
    /// </summary>
    public int Finish(TimeSpan wait)
    {
        EndVideo();
        if (_proc.WaitForExit(wait)) return _proc.ExitCode;
        _proc.Kill();
        _proc.WaitForExit();
        return -1;
    }

    public void Dispose() => _proc.Dispose();
}
