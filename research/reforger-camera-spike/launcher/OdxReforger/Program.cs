using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.Win32;

namespace OdxReforger;

static class Program
{
    const string AddonName = "OdxCapture";
    const string GameExe = "ArmaReforgerSteam.exe";

    [STAThread]
    static void Main()
    {
        Application.EnableVisualStyles();
        var settings = Settings.Load();
        var game = new[] { settings["game"], AppDomain.CurrentDomain.BaseDirectory, FindSteamGame() }.FirstOrDefault(IsGame);
        AddDesktopShortcut(game);
        Application.Run(new LauncherForm(settings, game));
    }

    public static bool IsGame(string? dir) => !string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, GameExe));

    static string? FindSteamGame()
    {
        if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) is not string steam) return null;
        var libraries = new List<string> { steam };
        var vdf = Path.Combine(steam, @"steamapps\libraryfolders.vdf");
        if (File.Exists(vdf))
            libraries.AddRange(Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\"")
                .Cast<Match>().Select(m => m.Groups[1].Value.Replace(@"\\", @"\")));
        return libraries.Select(lib => Path.Combine(lib, @"steamapps\common\Arma Reforger")).FirstOrDefault(IsGame);
    }

    static void AddDesktopShortcut(string? game)
    {
        var lnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Odx Reforger.lnk");
        if (File.Exists(lnk)) return;
        try
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
            var s = shell.CreateShortcut(lnk);
            s.TargetPath = Application.ExecutablePath;
            s.WorkingDirectory = Path.GetDirectoryName(Application.ExecutablePath);
            if (game != null) s.IconLocation = Path.Combine(game, GameExe);
            s.Save();
        }
        catch (Exception) { }  // A missing shortcut only costs the user a trip to the exe.
    }

    public static void InstallAddon(string game)
    {
        var target = Path.Combine(game, "addons", AddonName);
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        var assembly = Assembly.GetExecutingAssembly();
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith("addon/")))
        {
            var path = Path.Combine(target, name.Substring("addon/".Length).Replace('/', '\\'));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var source = assembly.GetManifestResourceStream(name)!;
            using var file = File.Create(path);
            source.CopyTo(file);
        }
    }

    public static void StartGame(string game, string server, bool window, string profile)
    {
        var args = $"{(window ? "-window " : "")}-addonsDir \"{Path.Combine(game, "addons")}\" -addons {AddonName} " +
                   $"-profile \"{profile}\" -client {server}";
        Process.Start(new ProcessStartInfo(Path.Combine(game, GameExe), args) { WorkingDirectory = game, UseShellExecute = false });
    }
}

sealed class Settings
{
    static readonly string FilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "OdxReforger", "settings.txt");
    readonly Dictionary<string, string> _values = new();

    public string? this[string key]
    {
        get => _values.TryGetValue(key, out var v) ? v : null;
        set => _values[key] = value ?? "";
    }

    public static Settings Load()
    {
        var s = new Settings();
        if (File.Exists(FilePath))
            foreach (var line in File.ReadAllLines(FilePath))
                if (line.IndexOf('=') is var i and > 0) s._values[line.Substring(0, i)] = line.Substring(i + 1);
        return s;
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllLines(FilePath, _values.Select(kv => $"{kv.Key}={kv.Value}"));
    }
}

sealed class LauncherForm : Form
{
    readonly Settings _settings;
    readonly TextBox _game, _server, _profile;
    readonly RadioButton _window, _fullscreen;
    readonly Label _status;

    public LauncherForm(Settings settings, string? game)
    {
        _settings = settings;
        Text = "Odx Reforger " + Application.ProductVersion;
        ClientSize = new Size(520, 210);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;

        AddLabel("Game folder", 15);
        _game = AddTextBox(15, game ?? "");
        AddBrowseButton(15, _game);

        AddLabel("Server", 50);
        _server = AddTextBox(50, settings["server"] ?? "", 150);

        AddLabel("Display", 85);
        _window = new RadioButton { Text = "Window", Location = new Point(110, 84), AutoSize = true };
        _fullscreen = new RadioButton { Text = "Fullscreen (game setting; only borderless records)", Location = new Point(200, 84), AutoSize = true };
        (settings["fullscreen"] == "true" ? _fullscreen : _window).Checked = true;
        Controls.AddRange(new Control[] { _window, _fullscreen });

        AddLabel("Profile folder", 120);
        _profile = AddTextBox(120, settings["profile"] ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), @"OdxReforger\profile"));
        AddBrowseButton(120, _profile);

        _status = new Label { Location = new Point(12, 170), Size = new Size(400, 30) };
        if (game == null) _status.Text = "Arma Reforger not found: choose its game folder.";
        Controls.Add(_status);

        var launch = new Button { Text = "Launch", Location = new Point(428, 165), Size = new Size(80, 30) };
        launch.Click += (_, _) => Launch();
        Controls.Add(launch);
        AcceptButton = launch;
    }

    void Launch()
    {
        var game = _game.Text.Trim();
        var server = _server.Text.Trim();
        var profile = _profile.Text.Trim();
        if (!Program.IsGame(game)) { Fail("ArmaReforgerSteam.exe not found in the game folder."); return; }
        if (!Regex.IsMatch(server, @"^[\w.-]+:\d+$")) { Fail("Server must be ip:port, e.g. 13.228.16.146:2001."); return; }
        try
        {
            Program.InstallAddon(game);
            _settings["game"] = game;
            _settings["server"] = server;
            _settings["fullscreen"] = _fullscreen.Checked ? "true" : "false";
            _settings["profile"] = profile;
            _settings.Save();
            Program.StartGame(game, server, _window.Checked, profile);
            Close();
        }
        catch (Exception e) { Fail(e.Message); }
    }

    void Fail(string message)
    {
        _status.ForeColor = Color.Firebrick;
        _status.Text = message;
    }

    void AddLabel(string text, int y) => Controls.Add(new Label { Text = text, Location = new Point(12, y + 3), AutoSize = true });

    TextBox AddTextBox(int y, string text, int width = 310)
    {
        var t = new TextBox { Text = text, Location = new Point(110, y), Width = width };
        Controls.Add(t);
        return t;
    }

    void AddBrowseButton(int y, TextBox target)
    {
        var b = new Button { Text = "Browse...", Location = new Point(428, y - 1), Width = 80 };
        b.Click += (_, _) =>
        {
            using var d = new FolderBrowserDialog { SelectedPath = target.Text };
            if (d.ShowDialog() == DialogResult.OK) target.Text = d.SelectedPath;
        };
        Controls.Add(b);
    }
}
