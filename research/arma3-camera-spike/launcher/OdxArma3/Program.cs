using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.Win32;

namespace OdxArma3;

static class Program
{
    const string ModFolder = "@OdxCapture";
    const string AddonName = "odx_capture";
    const string GameExe = "arma3_x64.exe";

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
        return libraries.Select(lib => Path.Combine(lib, @"steamapps\common\Arma 3")).FirstOrDefault(IsGame);
    }

    static void AddDesktopShortcut(string? game)
    {
        var lnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Odx Arma 3.lnk");
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

    /// <summary>
    /// The embedded addon source, packed into &lt;game&gt;\@OdxCapture\addons\odx_capture.pbo (the folder replaced as a
    /// whole), and the extension the addon calls each frame, written to &lt;game&gt;\@OdxCapture\odx_capture_x64.dll.
    /// </summary>
    public static void InstallAddon(string game)
    {
        var mod = Path.Combine(game, ModFolder);
        if (Directory.Exists(mod)) Directory.Delete(mod, recursive: true);
        Directory.CreateDirectory(Path.Combine(mod, "addons"));
        var assembly = Assembly.GetExecutingAssembly();
        var files = assembly.GetManifestResourceNames().Where(n => n.StartsWith("addon/")).OrderBy(n => n, StringComparer.Ordinal)
            .Select(name =>
            {
                using var source = assembly.GetManifestResourceStream(name)!;
                using var buffer = new MemoryStream();
                source.CopyTo(buffer);
                return (Name: name.Substring("addon/".Length).Replace('/', '\\'), Data: buffer.ToArray());
            }).ToList();
        using (var pbo = File.Create(Path.Combine(mod, "addons", AddonName + ".pbo")))
            Pbo.Write(pbo, AddonName, files);
        using var dll = assembly.GetManifestResourceStream("odx_capture_x64.dll")!;
        using var dllFile = File.Create(Path.Combine(mod, "odx_capture_x64.dll"));
        dll.CopyTo(dllFile);
    }

    /// <summary>The game itself, BattlEye's wrapper left out (the Odx servers run BattlEye = 0): -mod loads the addon, -profiles puts the .rpt where odx-app reads the camera log.</summary>
    public static void StartGame(string game, string host, int port, string password, bool window, string profile)
    {
        var args = $"{(window ? "-window " : "")}-noLauncher -noSplash -skipIntro -mod=\"{ModFolder}\" -profiles=\"{profile}\" -name=odx " +
                   $"-connect={host} -port={port} -password=\"{password}\"";
        Directory.CreateDirectory(profile);
        Process.Start(new ProcessStartInfo(Path.Combine(game, GameExe), args) { WorkingDirectory = game, UseShellExecute = false });
    }
}

/// <summary>
/// The PBO container as capture/deploy/aws/arma3/pack_pbo.py writes it: a "Vers" product entry carrying the prefix, one
/// header entry per file (name with backslashes, packing method 0, original size, reserved, timestamp, data size as
/// uint32 LE), an empty entry, the file data in header order, then a zero byte and the SHA-1 of everything before it.
/// </summary>
static class Pbo
{
    public static void Write(Stream output, string prefix, IReadOnlyList<(string Name, byte[] Data)> files)
    {
        using var body = new MemoryStream();
        var w = new BinaryWriter(body, Encoding.UTF8, leaveOpen: true);
        Entry(w, "", 0x56657273, 0, 0, 0, 0);
        Ascii(w, "prefix");
        Ascii(w, prefix);
        w.Write((byte)0);
        var timestamp = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var (name, data) in files) Entry(w, name, 0, (uint)data.Length, 0, timestamp, (uint)data.Length);
        Entry(w, "", 0, 0, 0, 0, 0);
        foreach (var (_, data) in files) w.Write(data);
        w.Flush();
        var bytes = body.ToArray();
        output.Write(bytes, 0, bytes.Length);
        output.WriteByte(0);
        using var sha1 = SHA1.Create();
        var hash = sha1.ComputeHash(bytes);
        output.Write(hash, 0, hash.Length);
    }

    static void Entry(BinaryWriter w, string name, uint method, uint original, uint reserved, uint timestamp, uint size)
    {
        Ascii(w, name);
        w.Write(method);
        w.Write(original);
        w.Write(reserved);
        w.Write(timestamp);
        w.Write(size);
    }

    static void Ascii(BinaryWriter w, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        w.Write(bytes);
        w.Write((byte)0);
    }
}

sealed class Settings
{
    static readonly string FilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "OdxArma3", "settings.txt");
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
    readonly TextBox _game, _server, _password, _profile;
    readonly RadioButton _window, _fullscreen;
    readonly Label _status;

    public LauncherForm(Settings settings, string? game)
    {
        _settings = settings;
        Text = "Odx Arma 3 " + Application.ProductVersion;
        ClientSize = new Size(520, 245);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;

        AddLabel("Game folder", 15);
        _game = AddTextBox(15, game ?? "");
        AddBrowseButton(15, _game);

        AddLabel("Server", 50);
        _server = AddTextBox(50, settings["server"] ?? "", 150);
        AddLabel("Password", 50, 270);
        _password = AddTextBox(50, settings["password"] ?? "odx", 90, 340);

        AddLabel("Display", 85);
        _window = new RadioButton { Text = "Window", Location = new Point(110, 84), AutoSize = true };
        _fullscreen = new RadioButton { Text = "Fullscreen (game setting; only borderless records)", Location = new Point(200, 84), AutoSize = true };
        (settings["fullscreen"] == "true" ? _fullscreen : _window).Checked = true;
        Controls.AddRange(new Control[] { _window, _fullscreen });

        AddLabel("Profile folder", 120);
        _profile = AddTextBox(120, settings["profile"] ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), @"OdxArma3\profile"));
        AddBrowseButton(120, _profile);

        _status = new Label { Location = new Point(12, 160), Size = new Size(496, 40) };
        _status.Text = game == null
            ? "Arma 3 not found: choose its game folder."
            : "Starts arma3_x64.exe without BattlEye with the Odx camera addon; the profile folder must not be in OneDrive.";
        Controls.Add(_status);

        var launch = new Button { Text = "Launch", Location = new Point(428, 205), Size = new Size(80, 30) };
        launch.Click += (_, _) => Launch();
        Controls.Add(launch);
        AcceptButton = launch;
    }

    void Launch()
    {
        var game = _game.Text.Trim();
        var server = _server.Text.Trim();
        var profile = _profile.Text.Trim();
        if (!Program.IsGame(game)) { Fail("arma3_x64.exe not found in the game folder."); return; }
        var m = Regex.Match(server, @"^([\w.-]+)(?::(\d+))?$");
        if (!m.Success) { Fail("Server must be ip or ip:port, e.g. 13.214.255.147:2302."); return; }
        int port = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 2302;
        if (profile.IndexOf("OneDrive", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            Fail("The profile folder is inside OneDrive, which locks the game's files while syncing: choose another folder.");
            return;
        }
        try
        {
            Program.InstallAddon(game);
            _settings["game"] = game;
            _settings["server"] = server;
            _settings["password"] = _password.Text;
            _settings["fullscreen"] = _fullscreen.Checked ? "true" : "false";
            _settings["profile"] = profile;
            _settings.Save();
            Program.StartGame(game, m.Groups[1].Value, port, _password.Text, _window.Checked, profile);
            Close();
        }
        catch (Exception e) { Fail(e.Message); }
    }

    void Fail(string message)
    {
        _status.ForeColor = Color.Firebrick;
        _status.Text = message;
    }

    void AddLabel(string text, int y, int x = 12) => Controls.Add(new Label { Text = text, Location = new Point(x, y + 3), AutoSize = true });

    TextBox AddTextBox(int y, string text, int width = 310, int x = 110)
    {
        var t = new TextBox { Text = text, Location = new Point(x, y), Width = width };
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
