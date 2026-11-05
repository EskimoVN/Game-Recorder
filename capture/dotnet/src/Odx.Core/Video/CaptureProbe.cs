using System.Text.Json;

namespace Odx.Core;

/// <summary>
/// What <c>odx record --probe</c> found in up to 2 s of capturing the chosen source without recording: whether frames
/// come at all and whether they are black, which is what Windows.Graphics.Capture gives for an exclusive-fullscreen
/// window (only borderless or windowed games, or the monitor, can be recorded). Checked before a player is ready.
/// </summary>
/// <param name="Frames">Frames captured (0: the source gives none, e.g. a minimized window).</param>
/// <param name="Black">Every captured frame was black (luma below 32 everywhere).</param>
/// <param name="WindowStyle">"windowed" (title bar), "borderless" (no frame, fills its monitor), "popup" (no frame, smaller);
/// null for a monitor source.</param>
/// <param name="Source">"window:&lt;process&gt;", "monitor:&lt;process&gt;" (the monitor the game's window is on) or "monitor:primary".</param>
public sealed record CaptureProbe(int Width, int Height, int Frames, bool Black, double LumaMean, string? WindowStyle, string Source)
{
    /// <summary>The source can be recorded: it gives frames and they are not all black.</summary>
    public bool Ok => Frames > 0 && !Black;

    public string ToJson() => JsonSerializer.Serialize(this, RecorderStatus.Options);

    public static CaptureProbe? TryParse(string line)
    {
        if (!line.StartsWith('{')) return null;
        try
        {
            return JsonSerializer.Deserialize<CaptureProbe>(line, RecorderStatus.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
