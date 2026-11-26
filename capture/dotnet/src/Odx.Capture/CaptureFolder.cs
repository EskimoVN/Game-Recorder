using Odx.Core;

namespace Odx.Capture;

/// <summary>
/// A player's round folder: the episode of the client's Data Delivery Format at the top (<see cref="Episode"/>), every raw
/// log the capture tools and the agent write, and the spec v2 working files (session.json, frames.csv, player.json, ...),
/// in raw/, kept so the session can be built again (odx build) and merged into spec v3 (odx merge).
/// </summary>
public static class CaptureFolder
{
    public const string RawDir = "raw";
    /// <summary>The episode's files at the top of the folder; its subfolders (videos/, streams/, metadata/, annotations/) stay as they are.</summary>
    public static readonly string[] Delivered = [Episode.FileName, Episode.FramesFile, Episode.SensorsFile];

    /// <summary>Where raw files are: raw/ once the folder is tidied, else the folder itself.</summary>
    public static string RawFolder(string dir)
    {
        string raw = Path.Combine(dir, RawDir);
        return Directory.Exists(raw) ? raw : dir;
    }

    public static string Raw(string dir, string name) => Path.Combine(RawFolder(dir), name);

    /// <summary>A file at the top of the folder, else in raw/: a tidied folder keeps the v2 working files there.</summary>
    public static string Find(string dir, string name) =>
        Path.Combine(dir, name) is var top && File.Exists(top) ? top : Raw(dir, name);

    /// <summary>The video: videos/pov.mp4 once tidied, else video.mp4.</summary>
    public static string Video(string dir) =>
        Path.Combine(dir, VideoFinalize.Delivered) is var episode && File.Exists(episode) ? episode : Path.Combine(dir, VideoFinalize.Output);

    /// <summary>Moves video.mp4 to videos/pov.mp4 and every other file but the episode's into raw/, then seals episode.json.</summary>
    public static void Tidy(string dir)
    {
        string raw = Directory.CreateDirectory(Path.Combine(dir, RawDir)).FullName;
        string video = Path.Combine(dir, VideoFinalize.Output);
        if (File.Exists(video))
        {
            string target = Path.Combine(dir, VideoFinalize.Delivered);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(video, target, overwrite: true);
        }
        foreach (string file in Directory.GetFiles(dir))
        {
            string name = Path.GetFileName(file);
            if (!Delivered.Contains(name)) File.Move(file, Path.Combine(raw, name), overwrite: true);
        }
        Episode.Seal(dir);
    }
}
