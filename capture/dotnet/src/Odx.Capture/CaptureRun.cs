using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Odx.Core;

namespace Odx.Capture;

/// <param name="WindowProcess">Process name passed to odx record --window; null records the primary monitor.</param>
/// <param name="GameProfile">Game profile JSON used to build the session after recording; null skips the build.</param>
/// <param name="NtpServer">Passed to odx input --ntp; null or empty measures nothing (Windows clock only).</param>
/// <param name="NtpIntervalS">Seconds between NTP samples; agents use 1 s against the orchestrator on the LAN.</param>
/// <param name="DirName">The capture folder's name: in a room the round's session id, which the session takes (spec v3 §1);
/// null names the folder after the start time.</param>
/// <param name="Audio">Also record the game process's own sound (the window's, else the profile's window_process), or
/// everything the PC plays when neither is known, as video.mp4's audio track (odx record --audio).</param>
/// <param name="Player">Named in player.json; null outside a room.</param>
/// <param name="Obs">Record the video with the player's OBS (odx record --obs) instead of Windows.Graphics.Capture; OBS's
/// own frame rate and audio settings apply.</param>
public sealed record CaptureOptions(string OutputRoot, string? WindowProcess, int Fps, string? GameProfile, string? NtpServer,
    int NtpIntervalS = 15, string? DirName = null, bool Audio = false, PlayerIdentity? Player = null, ObsConnection? Obs = null);

/// <param name="Address">host:port of OBS's WebSocket server.</param>
public sealed record ObsConnection(string Address, string? Password);

/// <param name="Recording">odx record's final status (frame counts); null if it never reported one.</param>
/// <param name="ToolErrors">stderr lines of the capture tools (warnings such as an unreachable NTP server included).</param>
/// <param name="Build">Null when no game profile was selected.</param>
/// <param name="Failure">Why the capture failed, in one line: a tool that had to be killed, the recorder's own error, or
/// its exit code. Null when <paramref name="CaptureOk"/>.</param>
public sealed record RunResult(string SessionDir, bool CaptureOk, RecorderStatus? Recording, int InputEvents,
    IReadOnlyList<string> ToolErrors, BuildResult? Build, string? Failure = null)
{
    public bool Ok => CaptureOk && (Build is null || (Build.Error is null && Build.Issues.All(i => i.Severity != Issue.ErrorSeverity)));
}

/// <param name="Error">Why the session could not be built; null when it was built and validated.</param>
public sealed record BuildResult(BuildReport? Report, IReadOnlyList<Issue> Issues, string? Error);

/// <summary>One recording in progress; the agent and odx-app drive it, tests substitute a fake.</summary>
public interface ICaptureRun
{
    string SessionDir { get; }
    Task Started { get; }
    Task CaptureStopped { get; }
    Task<RunResult> Completion { get; }
    void Stop();
}

/// <summary>
/// One recording: `odx input` and `odx record` run as child processes into the same session folder, both stopped through
/// a shared named event. If either child exits on its own (error, captured window closed), the other is stopped too.
/// After a clean stop the folder is built into a spec v2 session with the chosen game profile and validated, then tidied
/// (<see cref="CaptureFolder"/>): the delivered files at the top, the raw logs in raw/.
/// </summary>
public sealed class CaptureRun : ICaptureRun
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(60);  // ffmpeg may still be draining frames

    private readonly EventWaitHandle _stop;
    private readonly List<string> _stderr = [];
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _captureStopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string? _gameProfile;
    private readonly PlayerIdentity? _player;
    /// <summary>The round's session id when the agent runs this capture (its DirName); null for a capture outside a room.</summary>
    private readonly string? _sessionId;
    private readonly string? _reforgerDir;
    private RecorderStatus? _lastStatus;

    public string SessionDir { get; }
    /// <summary>Completes when odx record reports that capture is running; it can take seconds on a cold start.</summary>
    public Task Started => _started.Task;
    /// <summary>Completes when both capture tools have exited (before the session is built and validated).</summary>
    public Task CaptureStopped => _captureStopped.Task;
    /// <summary>Latest odx record status (frame counters update every second); null before capture starts.</summary>
    public RecorderStatus? LastStatus => Volatile.Read(ref _lastStatus);
    public Task<RunResult> Completion { get; }

    private CaptureRun(CaptureOptions options)
    {
        SessionDir = Path.Combine(options.OutputRoot, options.DirName ?? DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        _gameProfile = options.GameProfile;
        _player = options.Player;
        _sessionId = options.DirName;
        Directory.CreateDirectory(SessionDir);
        var profile = TryLoadProfile(options.GameProfile);
        if (profile?.CameraSource == ReforgerCamera.Source)
        {
            _reforgerDir = ReforgerCamera.OdxDir();
            try
            {
                ReforgerCamera.Begin(_reforgerDir, Path.GetFileName(SessionDir));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _stderr.Add($"cannot start the Arma Reforger camera log: {e.Message}");
                _reforgerDir = null;
            }
        }
        string stopName = $@"Local\odx-stop-{Guid.NewGuid():N}";
        _stop = new EventWaitHandle(false, EventResetMode.ManualReset, stopName);

        var inputArgs = new List<string> { "--out", SessionDir, "--stop-event", stopName };
        if (!string.IsNullOrWhiteSpace(options.NtpServer))
            inputArgs.AddRange(["--ntp", options.NtpServer.Trim(), "--ntp-interval-s", options.NtpIntervalS.ToString(CultureInfo.InvariantCulture)]);
        var input = Launch("input", inputArgs);
        var recordArgs = new List<string> { "--out", SessionDir, "--fps", options.Fps.ToString(CultureInfo.InvariantCulture),
            "--stop-event", stopName, "--json-status" };
        recordArgs.AddRange(options.WindowProcess is string p ? ["--window", p] : ["--monitor"]);
        if (options.Audio) recordArgs.Add("--audio");
        // Recording the monitor: the profile still names the game, so only its sound is recorded.
        if (options.Audio && options.WindowProcess is null && profile?.WindowProcess is string game)
            recordArgs.AddRange(["--audio-process", game]);
        if (options.Obs is { } obs) recordArgs.AddRange(["--obs", obs.Address]);
        Process record;
        try
        {
            record = Launch("record", recordArgs, env: options.Obs?.Password, onStdout: line =>
            {
                if (RecorderStatus.TryParse(line) is not { } status) return;
                Volatile.Write(ref _lastStatus, status);
                if (status.Event == RecorderStatus.Started) _started.TrySetResult();
            });
        }
        catch
        {
            _stop.Set();
            throw;
        }
        Completion = Task.Run(() => WaitAsync(input, record));
    }

    public static CaptureRun Start(CaptureOptions options) => new(options);

    /// <summary>
    /// <c>odx record --probe</c> on the source a round would record: the window of <paramref name="windowProcess"/>, else
    /// the monitor of <paramref name="gameProcess"/>'s window (the primary without one). Null when odx could not run.
    /// </summary>
    public static async Task<CaptureProbe?> ProbeAsync(string? windowProcess, string? gameProcess)
    {
        var psi = new ProcessStartInfo(ExternalTools.Odx())
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
        };
        var args = new List<string> { "record", "--probe", "--out", Path.Combine(Path.GetTempPath(), "odx-probe") };
        args.AddRange(windowProcess is string p ? ["--window", p] : ["--monitor"]);
        if (windowProcess is null && gameProcess is string g) args.AddRange(["--audio-process", g]);
        foreach (string a in args) psi.ArgumentList.Add(a);
        try
        {
            using var process = Process.Start(psi)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await process.WaitForExitAsync(timeout.Token);
            await errors;
            return (await output).Split('\n').Select(line => CaptureProbe.TryParse(line.Trim())).FirstOrDefault(r => r is not null);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or OperationCanceledException or IOException or InvalidOperationException)
        {
            return null;
        }
    }

    public void Stop() => _stop.Set();

    /// <param name="env">The OBS password, passed in the environment so that no other process sees it on a command line.</param>
    private Process Launch(string command, IEnumerable<string> args, string? env = null, Action<string>? onStdout = null)
    {
        var psi = new ProcessStartInfo(ExternalTools.Odx())
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = onStdout is not null,
        };
        psi.ArgumentList.Add(command);
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (!string.IsNullOrEmpty(env)) psi.Environment["ODX_OBS_PASSWORD"] = env;
        var proc = Process.Start(psi)!;
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (_stderr) _stderr.Add($"odx {command}: {e.Data}");
        };
        proc.BeginErrorReadLine();
        if (onStdout is not null)
        {
            proc.OutputDataReceived += (_, e) => { if (e.Data is not null) onStdout(e.Data); };
            proc.BeginOutputReadLine();
        }
        return proc;
    }

    private async Task<RunResult> WaitAsync(Process input, Process record)
    {
        await Task.WhenAny(input.WaitForExitAsync(), record.WaitForExitAsync());
        _stop.Set();
        using var timeout = new CancellationTokenSource(StopTimeout);
        string? failure = null;
        try
        {
            await Task.WhenAll(input.WaitForExitAsync(timeout.Token), record.WaitForExitAsync(timeout.Token));
        }
        catch (OperationCanceledException)
        {
            foreach (var (name, p) in new[] { ("odx input", input), ("odx record", record) })
            {
                if (p.HasExited) continue;
                p.Kill();
                string last = p == record && LastStatus is { } s ? $" (last status: {s.Event}, {s.Frames} frames)" : "";
                failure ??= $"{name} did not stop within {StopTimeout.TotalSeconds:0} s{last}; killed";
                lock (_stderr) _stderr.Add(failure);
            }
        }
        _stop.Dispose();
        _captureStopped.TrySetResult();

        bool captureOk = input.ExitCode == 0 && record.ExitCode == 0;
        var status = LastStatus is { Event: RecorderStatus.Stopped } stopped ? stopped : null;
        if (!captureOk)
            failure ??= status?.Error ?? (record.ExitCode != 0 ? $"odx record exited with {record.ExitCode}" : $"odx input exited with {input.ExitCode}");
        string inputCsv = Path.Combine(SessionDir, "input_events.csv");
        int inputEvents = File.Exists(inputCsv) ? Math.Max(0, File.ReadLines(inputCsv).Count() - 1) : 0;
        if (_reforgerDir is not null &&
            ReforgerCamera.Collect(_reforgerDir, Path.GetFileName(SessionDir), SessionDir, TimeSpan.FromSeconds(3)) is { } warning)
            lock (_stderr) _stderr.Add(warning);
        List<string> errors;
        lock (_stderr) errors = [.. _stderr];
        var build = captureOk ? BuildSession() : null;
        try
        {
            CaptureFolder.Tidy(SessionDir);
        }
        catch (IOException e)
        {
            errors.Add($"raw logs not moved into {CaptureFolder.RawDir}/: {e.Message}");
        }
        return new RunResult(SessionDir, captureOk, status, inputEvents, errors, build, failure);
    }

    /// <summary>Null without a profile; an unreadable profile is reported when the session is built.</summary>
    private static GameProfile? TryLoadProfile(string? profile)
    {
        if (profile is null) return null;
        try
        {
            return GameProfile.Load(profile);
        }
        catch (Exception e) when (e is IOException or FormatException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private BuildResult? BuildSession()
    {
        if (_gameProfile is null) return null;
        try
        {
            var report = SessionBuilder.Build(SessionDir, GameProfile.Load(_gameProfile), player: _player, sessionId: _sessionId);
            return new BuildResult(report, SessionValidator.ValidateSession(SessionDir), null);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or FormatException or JsonException)
        {
            return new BuildResult(null, [], e.Message);
        }
    }
}
