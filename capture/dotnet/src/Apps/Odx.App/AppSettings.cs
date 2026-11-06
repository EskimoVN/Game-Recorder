using System.IO;
using System.Text.Json;
using Odx.Capture;
using Odx.Core;

namespace Odx.App;

/// <summary>Persisted in %APPDATA%\Odx\odx-app.json.</summary>
public sealed class AppSettings
{
    private static readonly string FilePath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Odx", "odx-app.json");

    /// <summary>Process name passed to odx record --window; null records the primary monitor.</summary>
    public string? WindowProcess { get; set; }
    public int Fps { get; set; } = 30;
    public bool RecordAudio { get; set; } = true;
    public bool RecordWithObs { get; set; }
    public string ObsAddress { get; set; } = "127.0.0.1:4455";
    /// <summary>OBS's WebSocket password, kept in clear like OBS keeps it in its own settings.</summary>
    public string? ObsPassword { get; set; }

    public ObsConnection? Obs => RecordWithObs ? new ObsConnection(ObsAddress, ObsPassword) : null;
    /// <summary>"en" or "vi"; null follows the Windows display language.</summary>
    public string? Language { get; set; }
    /// <summary>Game profile JSON used to build the session after recording; null skips the build.</summary>
    public string? GameProfile { get; set; }
    public Hotkey Hotkey { get; set; } = Hotkey.Default;
    /// <summary>Both typed by the player (spec v3 §2.1 player.json names the player): no default, so a machine name or a
    /// Windows user name never stands in for them.</summary>
    public string PlayerId { get; set; } = "";
    public string PlayerName { get; set; } = "";

    /// <param name="timeServer">time_server of odx-capture.json; null uses the Windows clock only.</param>
    /// <remarks>Recordings go to the package's data\captures\ (<see cref="PackageFolders"/>).</remarks>
    public CaptureOptions ToCaptureOptions(string? timeServer) =>
        new(PackageFolders.Captures, WindowProcess, Fps, GameProfile, timeServer, Audio: RecordAudio, Obs: Obs);

    public static AppSettings Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings()
                : new AppSettings();
        }
        catch (JsonException)
        {
            return new AppSettings();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
