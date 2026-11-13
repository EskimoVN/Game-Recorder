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

static class EskimoProgram
{
    const string CaptureMod = "@OdxCapture";
    const string EskimoMod = "@OdxEskimo";
    const string EskimoAddon = "eskimo_tweaks";

    [STAThread]
    static void Main()
    {
        Application.EnableVisualStyles();
        var settings = Settings.Load();
        var game = new[] { settings["game"], AppDomain.CurrentDomain.BaseDirectory, FindSteamGame() }.FirstOrDefault(Program.IsGame);
        Application.Run(new EskimoForm(settings, game));
    }

    /// <summary>Same lookup as Program.FindSteamGame (private there).</summary>
    static string? FindSteamGame()
    {
        if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) is not string steam) return null;
        var libraries = new List<string> { steam };
        var vdf = Path.Combine(steam, @"steamapps\libraryfolders.vdf");
        if (File.Exists(vdf))
            libraries.AddRange(Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\"")
                .Cast<Match>().Select(m => m.Groups[1].Value.Replace(@"\\", @"\")));
        return libraries.Select(lib => Path.Combine(lib, @"steamapps\common\Arma 3")).FirstOrDefault(Program.IsGame);
    }

    /// <summary>&lt;game&gt;\@OdxEskimo\addons\eskimo_tweaks.pbo, a mod folder of its own next to the company's @OdxCapture.</summary>
    public static void InstallEskimoMod(string game)
    {
        var addons = Path.Combine(game, EskimoMod, "addons");
        Directory.CreateDirectory(addons);
        var assembly = Assembly.GetExecutingAssembly();
        var files = assembly.GetManifestResourceNames().Where(n => n.StartsWith("eskimo/")).OrderBy(n => n, StringComparer.Ordinal)
            .Select(n =>
            {
                using var source = assembly.GetManifestResourceStream(n)!;
                using var bytes = new MemoryStream();
                source.CopyTo(bytes);
                return (Name: n.Substring("eskimo/".Length).Replace('/', '\\'), Data: bytes.ToArray());
            }).ToList();
        File.WriteAllBytes(Path.Combine(addons, EskimoAddon + ".pbo"), Pbo(EskimoAddon, files));
    }

    /// <summary>Same layout as Program.Pbo (private there): unsigned, uncompressed, "Vers" header with the prefix, SHA-1 trailer.</summary>
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

    /// <summary>Program.StartGame's arguments with both mods: arma3_x64.exe directly, BattlEye off (private servers only).</summary>
    public static void StartGame(string game, string server, string password, bool window)
    {
        var args = $"\"-mod={CaptureMod};{EskimoMod}\" -noSplash -skipIntro -showScriptErrors{(window ? " -window" : "")}";
        if (server.Length > 0)
        {
            var hostPort = server.Split(':');
            args += $" -connect={hostPort[0]} -port={hostPort[1]}";
            if (password.Length > 0) args += $" \"-password={password}\"";
        }
        Process.Start(new ProcessStartInfo(Path.Combine(game, Program.GameExe), args) { WorkingDirectory = game, UseShellExecute = false });
    }
}

sealed class EskimoForm : Form
{
    readonly Settings _settings;
    readonly TextBox _game, _server, _password;
    readonly RadioButton _window, _fullscreen;
    readonly Label _status;

    public EskimoForm(Settings settings, string? game)
    {
        _settings = settings;
        Text = "Odx Arma 3 (Eskimo) " + Application.ProductVersion;
        ClientSize = new Size(520, 240);
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

        Controls.Add(new Label
        {
            Text = "God mode, infinite magazines, no HUD. Eskimo-hosted games only (server name starts with \"Eskimo\").",
            Location = new Point(12, 150), Size = new Size(500, 20), ForeColor = Color.DimGray,
        });

        _status = new Label { Location = new Point(12, 190), Size = new Size(410, 40) };
        _status.Text = game == null ? "Arma 3 not found: choose its game folder."
            : Program.VpnAddress() is { } ip ? $"To join a game hosted on this PC, the others enter {ip}:{Program.GamePort} as Server."
            : "No Radmin VPN or ZeroTier adapter is up on this PC.";
        Controls.Add(_status);

        var launch = new Button { Text = "Launch", Location = new Point(428, 185), Size = new Size(80, 30) };
        launch.Click += (_, _) => Launch();
        Controls.Add(launch);
        AcceptButton = launch;
    }

    /// <summary>LauncherForm.Launch (0.2.3) with the Eskimo mod installed and both mods on the command line.</summary>
    void Launch()
    {
        var server = _server.Text.Trim();
        var password = _password.Text;
        string game;
        try
        {
            game = Path.GetFullPath(_game.Text.Trim());
        }
        catch (ArgumentException)
        {
            Fail("The game folder is not a valid path.");
            return;
        }
        if (!Program.IsGame(game)) { Fail("arma3_x64.exe not found in the game folder."); return; }
        if (Process.GetProcessesByName("arma3_x64").Length > 0)
        {
            Fail("Arma 3 is running: close it first (it holds the addon and mission files).");
            return;
        }
        if (server.Length > 0 && !Regex.IsMatch(server, @"^[\w.-]+:\d+$")) { Fail("Server must be ip:port, e.g. 10.0.0.5:2302."); return; }
        try
        {
            Program.InstallMod(game);
            Program.InstallMission(game);
            EskimoProgram.InstallEskimoMod(game);
            string? firewall = Program.AllowThroughFirewall(Path.Combine(game, Program.GameExe), _settings);
            _settings["game"] = game;
            _settings["server"] = server;
            _settings["password"] = password;
            _settings["fullscreen"] = _fullscreen.Checked ? "true" : "false";
            _settings.Save();
            EskimoProgram.StartGame(game, server, password, _window.Checked);
            if (firewall is null) Close();
            else Fail(firewall);
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
