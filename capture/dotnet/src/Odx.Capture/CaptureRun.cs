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

/// <summary>Where a recording stands after the stop was asked for, for the player's processing screen.</summary>
public enum CapturePhase { Recording, Stopping, Finalizing, Building, Done }

/// <summary>One recording in progress; the agent and odx-app drive it, tests substitute a fake.</summary>
public interface ICaptureRun
{
    string SessionDir { get; }
    Task Started { get; }
    /// <summary>Completes once the capture itself has ended (the recorder reported stopped, or both tools exited).</summary>
    Task CaptureStopped { get; }
    Task<RunResult> Completion { get; }
    CapturePhase Phase { get; }
    /// <summary>Of the finalize phase: frames muxed into video.mp4 so far, out of the frames recorded (0 before).</summary>
    int FinalizePercent { get; }
    /// <summary>Raised off the caller's thread when <see cref="Phase"/> or <see cref="FinalizePercent"/> changes.</summary>
    event Action? PhaseChanged;
    void Stop();
}

/// <summary>
/// One recording: `odx input` and `odx record` run as child processes into the same session folder, both stopped through
/// a shared named event. If either child exits on its own (error, captured window closed), the other is stopped too.
/// The recorder stops its capture (status stopped) and then finalizes the video (video_raw.mp4 + audio.wav → video.mp4,
/// status finalizing/finalized); each gets its own time limit, and a recorder killed in either leaves a capture that
/// <see cref="VideoFinalize.RunIfNeeded"/> turns into a video here. After a clean stop the folder is built into a spec v2
/// session with the chosen game profile and validated, then tidied (<see cref="CaptureFolder"/>): the delivered files at
/// the top, the raw logs in raw/.
/// </summary>
public sealed class CaptureRun : ICaptureRun
{
    /// <summary>Until the recorder reports stopped: its own limits add up to ~31 s (RecordCommand), so this never wins.</summary>
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(60);
    /// <summary>Added to the finalize's own limit (<see cref="VideoFinalize.Limit"/>) before the recorder is killed.</summary>
    private static readonly TimeSpan FinalizeMargin = TimeSpan.FromSeconds(30);

    private readonly EventWaitHandle _stop;
    private readonly List<string> _stderr = [];
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _captureStopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<RecorderStatus?> _recorderStopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile CapturePhase _phase = CapturePhase.Recording;
    private volatile int _finalizePercent;
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
    public CapturePhase Phase => _phase;
    public int FinalizePercent => _finalizePercent;
    public event Action? PhaseChanged;

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
                switch (status.Event)
                {
                    case RecorderStatus.Started:
                        Volatile.Write(ref _lastStatus, status);
                        _started.TrySetResult();
                        break;
                    case RecorderStatus.Stopped:
                        Volatile.Write(ref _lastStatus, status);
                        _recorderStopped.TrySetResult(status);
                        _captureStopped.TrySetResult();
                        SetPhase(CapturePhase.Finalizing, 0);
                        break;
                    case RecorderStatus.Finalizing:
                        long total = LastStatus?.Frames ?? 0;
                        SetPhase(CapturePhase.Finalizing, total > 0 ? (int)Math.Clamp(status.Frames * 100 / total, 0, 100) : 0);
                        break;
                    case RecorderStatus.Finalized:
                        break;  // the exit code and recording.json carry the result
                    default:
                        Volatile.Write(ref _lastStatus, status);
                        break;
                }
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
    /// the monitor of <paramref name="gameProcess"/>'s window (the primary without one), and with <paramref name="audio"/>
    /// the sound as a recording would take it. Null when odx could not run.
    /// </summary>
    public static async Task<CaptureProbe?> ProbeAsync(string? windowProcess, string? gameProcess, bool audio = false)
    {
        var psi = new ProcessStartInfo(ExternalTools.Odx())
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
        };
        var args = new List<string> { "record", "--probe", "--out", Path.Combine(Path.GetTempPath(), "odx-probe") };
        args.AddRange(windowProcess is string p ? ["--window", p] : ["--monitor"]);
        if (audio) args.Add("--audio");
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

    public void Stop()
    {
        if (_phase == CapturePhase.Recording) SetPhase(CapturePhase.Stopping, 0);
        _stop.Set();
    }

    private void SetPhase(CapturePhase phase, int percent)
    {
        if (_phase == phase && _finalizePercent == percent) return;
        _phase = phase;
        _finalizePercent = percent;
        PhaseChanged?.Invoke();
    }

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
        var recordExited = record.WaitForExitAsync();
        await Task.WhenAny(input.WaitForExitAsync(), recordExited, _recorderStopped.Task);
        _stop.Set();
        if (_phase == CapturePhase.Recording) SetPhase(CapturePhase.Stopping, 0);
        string? failure = null;
        // 1. The capture stops: odx input exits, odx record reports stopped (or exits, when it failed earlier).
        using (var timeout = new CancellationTokenSource(StopTimeout))
        {
            try
            {
                await Task.WhenAll(input.WaitForExitAsync(timeout.Token),
                    Task.WhenAny(recordExited, _recorderStopped.Task).WaitAsync(timeout.Token));
            }
            catch (OperationCanceledException)
            {
                foreach (var (name, p) in new[] { ("odx input", input), ("odx record", record) })
                {
                    if (p.HasExited) continue;
                    p.Kill();  // not its ffmpeg: that one sees the end of its input and finishes video_raw.mp4 by itself
                    string last = p == record && LastStatus is { } s ? $" (last status: {s.Event}, {s.Frames} frames)" : "";
                    failure ??= $"{name} did not stop within {StopTimeout.TotalSeconds:0} s{last}; killed";
                    lock (_stderr) _stderr.Add(failure);
                }
            }
        }
        _stop.Dispose();
        _captureStopped.TrySetResult();
        // 2. The recorder finalizes the video (its own limit, VideoFinalize.Limit); a margin on top, then it is killed.
        if (!record.HasExited)
        {
            var status0 = _recorderStopped.Task.IsCompletedSuccessfully ? _recorderStopped.Task.Result : null;
            double seconds = status0 is { Fps: > 0 } st ? st.Frames / st.Fps : 0;
            using var timeout = new CancellationTokenSource(VideoFinalize.Limit(seconds) + FinalizeMargin);
            try
            {
                await recordExited.WaitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                record.Kill(entireProcessTree: true);
                failure ??= $"odx record did not finish the video within {(VideoFinalize.Limit(seconds) + FinalizeMargin).TotalSeconds:0} s; killed";
                lock (_stderr) _stderr.Add(failure);
            }
        }
        await recordExited;

        bool captureOk = input.ExitCode == 0 && record.ExitCode == 0;
        var status = LastStatus is { Event: RecorderStatus.Stopped } stopped ? stopped : null;
        if (!captureOk)
            failure ??= status?.Error ?? FinalizeError() ?? (record.ExitCode != 0 ? $"odx record exited with {record.ExitCode}" : $"odx input exited with {input.ExitCode}");
        // A recorder killed before it could finalize (or that crashed) still leaves a playable video, once the ffmpeg it
        // left behind has finished writing it.
        await SettledAsync(Path.Combine(SessionDir, VideoFinalize.RawVideo));
        if (VideoFinalize.RunIfNeeded(SessionDir, ExternalTools.Ffmpeg(), n => SetPhase(CapturePhase.Finalizing,
                status is { Frames: > 0 } ? (int)Math.Clamp(n * 100 / status.Frames, 0, 100) : 0)) is { } late)
            lock (_stderr) _stderr.Add(late.Ok
                ? $"video finalized by the capture host: {late.Frames} frames{(late.HasAudio ? " with sound" : "")}, {late.ElapsedMs} ms"
                : $"video not finalized: {late.Error}");
        string inputCsv = Path.Combine(SessionDir, "input_events.csv");
        int inputEvents = File.Exists(inputCsv) ? Math.Max(0, File.ReadLines(inputCsv).Count() - 1) : 0;
        if (_reforgerDir is not null &&
            ReforgerCamera.Collect(_reforgerDir, Path.GetFileName(SessionDir), SessionDir, TimeSpan.FromSeconds(3)) is { } warning)
            lock (_stderr) _stderr.Add(warning);
        List<string> errors;
        lock (_stderr) errors = [.. _stderr];
        SetPhase(CapturePhase.Building, 100);
        var build = captureOk ? BuildSession() : null;
        try
        {
            CaptureFolder.Tidy(SessionDir);
        }
        catch (IOException e)
        {
            errors.Add($"raw logs not moved into {CaptureFolder.RawDir}/: {e.Message}");
        }
        var result = new RunResult(SessionDir, captureOk, status, inputEvents, errors, build, failure);
        SetPhase(CapturePhase.Done, 100);
        return result;
    }

    /// <summary>Waits until the file has not grown for 2 s (20 s at most); returns at once when it does not exist.</summary>
    private static async Task SettledAsync(string path)
    {
        long last = -1;
        for (int i = 0; i < 10; i++)
        {
            if (!File.Exists(path)) return;
            long size = new FileInfo(path).Length;
            if (size == last) return;
            last = size;
            await Task.Delay(2000);
        }
    }

    /// <summary>recording.json → finalize → error, when the recorder's exit code comes from a failed finalize.</summary>
    private string? FinalizeError()
    {
        string path = Path.Combine(SessionDir, "recording.json");
        if (!File.Exists(path)) return null;
        try
        {
            return System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))?["finalize"]?["error"]?.GetValue<string>() is { } e
                ? $"video not finalized: {e}" : null;
        }
        catch (JsonException)
        {
            return null;
        }
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
