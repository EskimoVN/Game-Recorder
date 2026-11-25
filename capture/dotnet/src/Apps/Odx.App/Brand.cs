namespace Odx.App;

/// <summary>
/// The product name players see (window title, header, tray, notices, error box). The files keep their Odx names
/// (Odx Capture.exe, bin\odx-app.exe, bin\odx.exe) so update packages and the company server still find them.
/// The CI smoke test waits for a window with this title (build-eskimo.yml).
/// </summary>
public static class Brand
{
    public const string Name = "Odx Capture";
}
