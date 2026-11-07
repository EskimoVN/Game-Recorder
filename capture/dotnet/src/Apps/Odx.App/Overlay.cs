using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Odx.App;

/// <summary>
/// Windows shown over the game (countdown, "END", notices): click-through, never focused, and excluded from screen
/// capture, so they never show in a recording, even of the whole monitor or while the capture already runs.
/// </summary>
internal static class Overlay
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;  // Windows 10 2004+
    private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10, SWP_FRAMECHANGED = 0x20;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    /// <summary>
    /// Windows 11. On some Windows 10 PCs a layered (AllowsTransparency) window excluded from capture is never drawn, so
    /// the 3-2-1 did not show (field report 2026-11-07); there the overlays stay capturable. None is on screen while
    /// delivered video is recorded: the solo 3-2-1 closes before the capture starts, a room's falls in the pre-roll cut
    /// at round_start, "END" shows once the capture stopped.
    /// </summary>
    private const int ExcludeLayeredFromCaptureMinBuild = 22000;

    /// <summary>Where failures go (the app's daily log); set by MainWindow.</summary>
    public static Action<string>? Log { get; set; }

    /// <summary>Call from the window's OnSourceInitialized.</summary>
    public static void Apply(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        SetWindowLongW(hwnd, GWL_EXSTYLE,
            GetWindowLongW(hwnd, GWL_EXSTYLE) | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, ExcludeLayeredFromCaptureMinBuild))
        {
            if (!SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE))
                Log?.Invoke($"overlay: SetWindowDisplayAffinity failed, error {Marshal.GetLastPInvokeError()}");
            return;
        }
        // Windows 10: apply the new extended styles and put the overlay back on top of the topmost windows (the game).
        if (!SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_FRAMECHANGED))
            Log?.Invoke($"overlay: SetWindowPos failed, error {Marshal.GetLastPInvokeError()}");
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern int GetWindowLongW(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLongW(IntPtr hWnd, int nIndex, int dwNewLong);
}
