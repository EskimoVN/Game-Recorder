using System.Collections.Concurrent;
using System.Diagnostics;
using Odx.Core;

namespace Odx.Recorder;

/// <summary>
/// Writes grid ticks to ffmpeg and video_frames.csv on its own thread, so the capture callback does not wait for the
/// encoder: while that callback is blocked, Windows.Graphics.Capture (2-buffer frame pool) drops frames and the video
/// jumps. Frames are pooled buffers; one goes back to the pool once its ticks are written, and <see cref="TryRent"/> fails
/// only when the encoder is a whole pool behind.
/// </summary>
internal sealed class FrameWriter
{
    // The frame on screen plus 3 queued: ~100 ms of slack at 30 fps (frames are 8 MB at 1080p).
    private const int Buffers = 4;

    private readonly FfmpegEncoder _encoder;
    private readonly VideoFrameLogWriter _log;
    private readonly BlockingCollection<byte[]> _free = new();
    private readonly BlockingCollection<(byte[]? Frame, IReadOnlyList<GridTick> Ticks)> _queue = new();
    private readonly Thread _thread;
    private readonly byte[] _black;
    private long _written, _writeMs, _writeMaxMs;
    private volatile string? _error;

    public long Written => Interlocked.Read(ref _written);
    /// <summary>Time spent blocked on ffmpeg's stdin, in all and for the slowest frame: how far behind the encoder runs.</summary>
    public long WriteMs => Interlocked.Read(ref _writeMs);
    public long WriteMaxMs => Interlocked.Read(ref _writeMaxMs);
    /// <summary>Why frames stopped being written (ffmpeg closed its input); later ticks are dropped.</summary>
    public string? Error => _error;

    public FrameWriter(FfmpegEncoder encoder, VideoFrameLogWriter log, int frameBytes)
    {
        _encoder = encoder;
        _log = log;
        for (int i = 0; i < Buffers; i++) _free.Add(new byte[frameBytes]);
        // NV12 black: Y 16, Cb/Cr 128 (limited range); the Y plane is two thirds of the frame.
        _black = new byte[frameBytes];
        int luma = frameBytes * 2 / 3;
        Array.Fill(_black, (byte)16, 0, luma);
        Array.Fill(_black, (byte)128, luma, frameBytes - luma);
        _thread = new Thread(Run) { IsBackground = true, Name = "frame writer" };
        _thread.Start();
    }

    public byte[] Rent() => _free.Take();

    /// <summary>A pooled buffer, or false when none came back within <paramref name="wait"/>: the caller drops its frame.</summary>
    public bool TryRent(TimeSpan wait, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out byte[]? frame) =>
        _free.TryTake(out frame, wait);

    /// <summary>
    /// Queues <paramref name="ticks"/>, all showing <paramref name="frame"/>, which returns to the pool after; null shows
    /// black (ticks before the first captured frame).
    /// </summary>
    public void Submit(byte[]? frame, IReadOnlyList<GridTick> ticks)
    {
        try
        {
            _queue.Add((frame, ticks));
        }
        catch (InvalidOperationException)
        {
            // The queue is closed: a capture callback the stop did not wait for came back after it; its frame is lost.
        }
    }

    /// <summary>
    /// Writes what is queued; <see cref="Submit"/> may not be called after. False when ffmpeg did not take it all within
    /// <paramref name="wait"/>: the writer is left blocked on ffmpeg's stdin and <see cref="Error"/> says so.
    /// </summary>
    public bool Complete(TimeSpan wait)
    {
        _queue.CompleteAdding();
        if (_thread.Join(wait)) return true;
        _error ??= "ffmpeg stopped reading video";
        return false;
    }

    private void Run()
    {
        foreach (var (frame, ticks) in _queue.GetConsumingEnumerable())
        {
            foreach (var t in ticks)
            {
                if (_error is not null) break;
                long started = Stopwatch.GetTimestamp();
                try
                {
                    _encoder.Write(frame ?? _black);
                }
                catch (Exception e) when (e is IOException or ObjectDisposedException)
                {
                    // ObjectDisposedException: the stop closed the pipe under a write ffmpeg was not taking.
                    _error = $"ffmpeg pipe closed: {e.Message}";
                    break;
                }
                long ms = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                Interlocked.Add(ref _writeMs, ms);
                if (ms > Interlocked.Read(ref _writeMaxMs)) Interlocked.Exchange(ref _writeMaxMs, ms);
                try
                {
                    _log.Write(t);
                }
                catch (ObjectDisposedException)
                {
                    return;   // the stop gave the drain up and has closed the log: nothing of this write is wanted any more
                }
                Interlocked.Increment(ref _written);
            }
            if (frame is not null) _free.Add(frame);
        }
    }
}
