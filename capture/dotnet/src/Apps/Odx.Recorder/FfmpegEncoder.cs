using System.Diagnostics;

namespace Odx.Recorder;

/// <summary>
/// Encodes raw NV12 frames piped to an ffmpeg process into a constant-frame-rate, fragmented MP4 without B-frames
/// (video_raw.mp4). ffmpeg has this one input and nothing to wait for but the next frame, and a fragment is closed at
/// every keyframe (4 s at 30 fps), so a killed ffmpeg still leaves a playable file minus its last fragment; the sound is
/// muxed in afterwards (VideoFinalize).
/// </summary>
internal sealed class FfmpegEncoder : IDisposable
{
    private readonly Process _proc;
    private readonly Stream _stdin;

    /// <summary>nvenc, mf or x264.</summary>
    public string Encoder { get; }

    public FfmpegEncoder(string ffmpeg, string output, int width, int height, double fps, string encoder)
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
        var args = $"-y -loglevel error -nostdin -f rawvideo -pix_fmt nv12 -s {width}x{height} -framerate {fr} -i - " +
                   $"{codec} -r {fr} -movflags +frag_keyframe+empty_moov+default_base_moof -f mp4 \"{output}\"";
        _proc = Process.Start(new ProcessStartInfo(ffmpeg, args)
        {
            RedirectStandardInput = true,
            UseShellExecute = false,
        }) ?? throw new InvalidOperationException("cannot start ffmpeg");
        _stdin = _proc.StandardInput.BaseStream;
    }

    public void Write(byte[] nv12Frame) => _stdin.Write(nv12Frame, 0, nv12Frame.Length);

    /// <summary>Ends the input: ffmpeg flushes the encoder and closes the file.</summary>
    public void EndVideo() => _stdin.Close();

    /// <summary>
    /// Ends the input and waits for ffmpeg to finish the file. -1 when it has not exited within <paramref name="wait"/>
    /// (a hardware encoder that never drains): it is killed, and the file keeps the fragments written before.
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
