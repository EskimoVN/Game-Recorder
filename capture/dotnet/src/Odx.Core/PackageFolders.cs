namespace Odx.Core;

/// <summary>
/// Where the package keeps its data, next to <c>bin\</c> (publish.ps1 layout):
/// <c>data\captures\</c> each recording made on this PC, <c>data\delivery\</c> the sessions to hand over (odx merge's output),
/// <c>data\logs\</c> odx-app's daily log. Outside a package (a development build) <c>data\</c> sits next to the executable.
/// </summary>
public static class PackageFolders
{
    public static string Data { get; } = Path.Combine(PackageRoot(), "data");

    public static string Captures => Path.Combine(Data, "captures");

    public static string Delivery => Path.Combine(Data, "delivery");

    public static string Logs => Path.Combine(Data, "logs");

    private static string PackageRoot()
    {
        string dir = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        return Path.GetFileName(dir) == "bin" ? Path.GetDirectoryName(dir)! : dir;
    }
}
