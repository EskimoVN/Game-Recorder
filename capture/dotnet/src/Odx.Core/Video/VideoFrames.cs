using System.Globalization;

namespace Odx.Core;

/// <summary>One output video frame on the constant-rate grid.</summary>
/// <param name="Index">0-based output frame index (= video frame index).</param>
/// <param name="TGridMs">Grid time (QPC ms) this output frame represents.</param>
/// <param name="TSourceMs">QPC ms of the captured frame shown (WGC SystemRelativeTime); the grid time itself for a black frame.</param>
/// <param name="Duplicate">True if this captured frame was already emitted (no new frame since the last tick), and for a black frame.</param>
/// <param name="Black">No frame had been captured by this tick (a grid that starts before the capture did): the frame is black.</param>
public readonly record struct GridTick(long Index, double TGridMs, double TSourceMs, bool Duplicate, bool Black = false);

/// <summary>
/// Turns variable-rate captured frames into a constant-rate output. Grid tick k (time t0 + k/fps) shows the most
/// recent captured frame with source time &lt;= that tick. t0 is <paramref name="originMs"/> when given (a round's start
/// on this machine's QPC clock, so every player's frame k is the same instant: document/video-alignment-plan.md §3.1),
/// else the first captured frame. Frames replaced before any tick are counted as dropped, except before the origin,
/// where only the last one matters. Ticks before the first captured frame are black. Pure logic: call Advance for every
/// captured frame (in time order) and Finish once at stop.
/// </summary>
public sealed class GridResampler(double fps, double? originMs = null)
{
    private readonly double _intervalMs = 1000.0 / fps;
    private double _t0 = originMs ?? double.NaN;
    private double _current = double.NaN;
    private bool _currentEmitted;
    private long _next;

    public long DroppedSourceFrames { get; private set; }
    public long Duplicates { get; private set; }
    /// <summary>Black ticks: the first frame came after them (not counted as duplicates).</summary>
    public long LeadingBlackFrames { get; private set; }

    /// <summary>A new frame captured at <paramref name="tSourceMs"/>. Returns the ticks to emit with the previous frame.</summary>
    public IReadOnlyList<GridTick> Advance(double tSourceMs)
    {
        if (double.IsNaN(_t0))
        {
            _t0 = tSourceMs;
            _current = tSourceMs;
            return [];
        }
        var ticks = EmitBefore(tSourceMs);
        if (!_currentEmitted && _current >= _t0) DroppedSourceFrames++;
        _current = tSourceMs;
        _currentEmitted = false;
        return ticks;
    }

    /// <summary>Recording stops at <paramref name="tStopMs"/>. Returns the remaining ticks, all showing the last frame.</summary>
    public IReadOnlyList<GridTick> Finish(double tStopMs) =>
        double.IsNaN(_current) ? [] : EmitBefore(tStopMs);

    private List<GridTick> EmitBefore(double tMs)
    {
        var ticks = new List<GridTick>();
        while (_t0 + _next * _intervalMs < tMs)
        {
            double t = _t0 + _next * _intervalMs;
            if (double.IsNaN(_current))
            {
                LeadingBlackFrames++;
                ticks.Add(new GridTick(_next, t, t, true, Black: true));
            }
            else
            {
                bool dup = _currentEmitted;
                if (dup) Duplicates++;
                ticks.Add(new GridTick(_next, t, _current, dup));
                _currentEmitted = true;
            }
            _next++;
        }
        return ticks;
    }
}

/// <summary>
/// Writes video_frames.csv: <c>frame_index,t_grid_qpc_ms,t_source_qpc_ms,duplicate</c>, one row per video frame.
/// Read by <see cref="VideoFrameLogReader"/>; pinned by dotnet/fixtures/video_frames.csv.
/// </summary>
public sealed class VideoFrameLogWriter(TextWriter writer) : IDisposable
{
    public const string Header = "frame_index,t_grid_qpc_ms,t_source_qpc_ms,duplicate";
    private bool _headerWritten;

    public void Write(GridTick t)
    {
        if (!_headerWritten)
        {
            writer.Write(Header + "\n");
            _headerWritten = true;
        }
        writer.Write(string.Join(",", t.Index.ToString(CultureInfo.InvariantCulture), InputLogWriter.Num(t.TGridMs),
            InputLogWriter.Num(t.TSourceMs), t.Duplicate ? "1" : "0") + "\n");
    }

    public void Flush() => writer.Flush();
    public void Dispose() => writer.Dispose();
}

public static class VideoFrameLogReader
{
    public static List<GridTick> Read(string path)
    {
        var (_, rows) = Csv.ReadFile(path);
        return rows.Select(r => new GridTick(long.Parse(r["frame_index"], CultureInfo.InvariantCulture),
            Frames.ParseDouble(r["t_grid_qpc_ms"]), Frames.ParseDouble(r["t_source_qpc_ms"]), r["duplicate"] == "1")).ToList();
    }
}
