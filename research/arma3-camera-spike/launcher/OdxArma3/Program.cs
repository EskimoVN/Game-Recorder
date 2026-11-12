using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.Win32;

namespace OdxArma3;

static class Program
{
    const string ModName = "@OdxCapture";
    const string AddonName = "odx_capture";
    public const string GameExe = "arma3_x64.exe";
    const string FirewallRule = "Odx Arma 3";
    public const int GamePort = 2302;

    [STAThread]
    static void Main()
    {
        Application.EnableVisualStyles();
        var settings = Settings.Load();
        var game = new[] { settings["game"], AppDomain.CurrentDomain.BaseDirectory, FindSteamGame() }.FirstOrDefault(IsGame);
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

    /// <summary>&lt;game&gt;\@OdxCapture\: the extension next to the mod's addons\odx_capture.pbo.</summary>
    public static void InstallMod(string game)
    {
        var mod = Path.Combine(game, ModName);
        Directory.CreateDirectory(Path.Combine(mod, "addons"));
        var assembly = Assembly.GetExecutingAssembly();
        using (var dll = assembly.GetManifestResourceStream("odx_capture_x64.dll")!)
        using (var file = File.Create(Path.Combine(mod, "odx_capture_x64.dll")))
            dll.CopyTo(file);
        var files = assembly.GetManifestResourceNames().Where(n => n.StartsWith("addon/")).OrderBy(n => n, StringComparer.Ordinal)
            .Select(n =>
            {
                using var source = assembly.GetManifestResourceStream(n)!;
                using var bytes = new MemoryStream();
                source.CopyTo(bytes);
                return (Name: n.Substring("addon/".Length).Replace('/', '\\'), Data: bytes.ToArray());
            }).ToList();
        File.WriteAllBytes(Path.Combine(mod, "addons", AddonName + ".pbo"), Pbo(AddonName, files));
    }

    /// <summary>
    /// &lt;game&gt;\MPMissions\&lt;mission&gt;.pbo: the folder the game lists packed missions from in Host Server (the editor's
    /// "Export to multiplayer" writes there too), packed being the form a host sends to joining clients. What earlier
    /// versions wrote into Documents\Arma 3\MPMissions (unpacked folders, 0.2.2's .pbo files) is removed.
    /// </summary>
    public static void InstallMission(string game)
    {
        var missions = Path.Combine(game, "MPMissions");
        Directory.CreateDirectory(missions);
        var documents = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Arma 3", "MPMissions");
        var assembly = Assembly.GetExecutingAssembly();
        // MSBuild's %(RecursiveDir) puts a backslash after the mission folder when built on Windows, a slash elsewhere.
        var resources = assembly.GetManifestResourceNames().Where(n => n.StartsWith("mission/"))
            .Select(n => (Resource: n, Path: n.Substring("mission/".Length).Replace('\\', '/')));
        foreach (var mission in resources.GroupBy(r => r.Path.Substring(0, r.Path.IndexOf('/'))))
        {
            var files = mission.OrderBy(r => r.Path, StringComparer.Ordinal).Select(r =>
            {
                using var source = assembly.GetManifestResourceStream(r.Resource)!;
                using var bytes = new MemoryStream();
                source.CopyTo(bytes);
                return (Name: r.Path.Substring(mission.Key.Length + 1).Replace('/', '\\'), Data: bytes.ToArray());
            }).ToList();
            File.WriteAllBytes(Path.Combine(missions, mission.Key + ".pbo"), Pbo(mission.Key, files));
            string old = Path.Combine(documents, mission.Key);
            if (Directory.Exists(old)) Directory.Delete(old, recursive: true);
            if (File.Exists(old + ".pbo")) File.Delete(old + ".pbo");
        }
    }

    /// <summary>
    /// An inbound rule for the game on every network profile, so a game hosted here is reachable over Radmin VPN or ZeroTier:
    /// Windows' own prompt on the first host offers private networks only, and those adapters count as public. netsh needs
    /// administrator rights, hence the UAC prompt; declining it leaves the rule to the player. Returns a warning in that case.
    /// </summary>
    public static string? AllowThroughFirewall(string exe)
    {
        var shown = Process.Start(new ProcessStartInfo("netsh", $"advfirewall firewall show rule name=\"{FirewallRule}\" verbose")
            { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true })!;
        string rules = shown.StandardOutput.ReadToEnd();
        shown.WaitForExit();
        if (rules.IndexOf(exe, StringComparison.OrdinalIgnoreCase) >= 0) return null;
        try
        {
            var add = Process.Start(new ProcessStartInfo("netsh",
                $"advfirewall firewall add rule name=\"{FirewallRule}\" dir=in action=allow program=\"{exe}\" enable=yes profile=any")
                { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden })!;
            add.WaitForExit();
            return add.ExitCode == 0 ? null : "Windows Firewall rule not added: others may not reach a game hosted here.";
        }
        catch (System.ComponentModel.Win32Exception)  // UAC declined
        {
            return "Windows Firewall rule declined: allow arma3_x64.exe on public networks yourself, or others cannot reach a game hosted here.";
        }
    }

    /// <summary>The IPv4 of a Radmin VPN or ZeroTier adapter: what the others enter as Server to reach a game hosted on this PC.</summary>
    public static string? VpnAddress() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up && Regex.IsMatch(n.Name + " " + n.Description, "Radmin|ZeroTier", RegexOptions.IgnoreCase))
        .SelectMany(n => n.GetIPProperties().UnicastAddresses.Select(a => a.Address))
        .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)?.ToString();

    /// <summary>
    /// Unsigned and uncompressed: a "Vers" header entry with the prefix property, one entry per file, an empty terminator
    /// entry, the file data, then a zero byte and the SHA-1 of everything before it. Enough for the editor, single player
    /// and servers with verifySignatures = 0; a server with 2 needs it signed with our key (Arma 3 Tools / DSSignFile).
    /// </summary>
    static byte[] Pbo(string prefix, IReadOnlyList<(string Name, byte[] Data)> files)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        void Z(string s)
        {
            w.Write(Encoding.ASCII.GetBytes(s));
            w.Write((byte)0);
        }
        void Header(uint mime, int size)
        {
            w.Write(mime);
            w.Write((uint)size);
            w.Write(0u);
            w.Write(0u);
            w.Write((uint)size);
        }
        Z("");
        Header(0x56657273, 0);
        Z("prefix");
        Z(prefix);
        Z("");
        foreach (var f in files)
        {
            Z(f.Name);
            Header(0, f.Data.Length);
        }
        Z("");
        Header(0, 0);
        foreach (var f in files) w.Write(f.Data);
        w.Flush();
        using var sha1 = SHA1.Create();
        var hash = sha1.ComputeHash(ms.ToArray());
        w.Write((byte)0);
        w.Write(hash);
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>arma3_x64.exe directly rather than arma3battleye.exe: BattlEye off (arma3.md §5).</summary>
    public static void StartGame(string game, string server, string password, bool window)
    {
        var args = $"-mod={ModName} -noSplash -skipIntro -showScriptErrors{(window ? " -window" : "")}";
        if (server.Length > 0)
        {
            var hostPort = server.Split(':');
            args += $" -connect={hostPort[0]} -port={hostPort[1]}";
            if (password.Length > 0) args += $" \"-password={password}\"";
        }
        Process.Start(new ProcessStartInfo(Path.Combine(game, GameExe), args) { WorkingDirectory = game, UseShellExecute = false });
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
    readonly TextBox _game, _server, _password;
    readonly RadioButton _window, _fullscreen;
    readonly Label _status;

    public LauncherForm(Settings settings, string? game)
    {
        _settings = settings;
        Text = "Odx Arma 3 Launcher";
        ClientSize = new Size(520, 210);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;

        AddLabel("Game folder", 15);
        _game = AddTextBox(15, game ?? "");
        AddBrowseButton(15, _game);

        AddLabel("Server", 50);
        _server = AddTextBox(50, settings["server"] ?? "", 150);
        AddLabel("ip:port, empty for the editor", 50, 270);

        AddLabel("Password", 85);
        _password = AddTextBox(85, settings["password"] ?? "", 150);
        _password.UseSystemPasswordChar = true;

        AddLabel("Display", 120);
        _window = new RadioButton { Text = "Window", Location = new Point(110, 119), AutoSize = true };
        _fullscreen = new RadioButton { Text = "Fullscreen", Location = new Point(200, 119), AutoSize = true };
        (settings["fullscreen"] == "true" ? _fullscreen : _window).Checked = true;
        Controls.AddRange(new Control[] { _window, _fullscreen });

        _status = new Label { Location = new Point(12, 170), Size = new Size(410, 40) };
        _status.Text = game == null ? "Arma 3 not found: choose its game folder."
            : Program.VpnAddress() is { } ip ? $"To join a game hosted on this PC, the others enter {ip}:{Program.GamePort} as Server."
            : "No Radmin VPN or ZeroTier adapter is up on this PC.";
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
        var password = _password.Text;
        if (!Program.IsGame(game)) { Fail("arma3_x64.exe not found in the game folder."); return; }
        if (server.Length > 0 && !Regex.IsMatch(server, @"^[\w.-]+:\d+$")) { Fail("Server must be ip:port, e.g. 10.0.0.5:2302."); return; }
        try
        {
            Program.InstallMod(game);
            Program.InstallMission(game);
            string? firewall = Program.AllowThroughFirewall(Path.Combine(game, Program.GameExe));
            _settings["game"] = game;
            _settings["server"] = server;
            _settings["password"] = password;
            _settings["fullscreen"] = _fullscreen.Checked ? "true" : "false";
            _settings.Save();
            Program.StartGame(game, server, password, _window.Checked);
            if (firewall is null) Close();
            else Fail(firewall);  // the game runs; the window stays for the player to read this
        }
        catch (Exception e) { Fail(e.Message); }
    }

    void Fail(string message)
    {
        _status.ForeColor = Color.Firebrick;
        _status.Text = message;
    }

    void AddLabel(string text, int y, int x = 12) => Controls.Add(new Label { Text = text, Location = new Point(x, y + 3), AutoSize = true });

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
