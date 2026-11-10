using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Odx.Core;
using Odx.Protocol;

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
/// <param name="RoundStartUtcMs">The round's start on the orchestrator's clock: the video's frame 0 is put there
/// (<see cref="GridOrigin"/>, measured against <paramref name="NtpServer"/>). Null outside a room: frame 0 is the first frame.</param>
public sealed record CaptureOptions(string OutputRoot, string? WindowProcess, int Fps, string? GameProfile, string? NtpServer,
    int NtpIntervalS = 15, string? DirName = null, bool Audio = false, PlayerIdentity? Player = null, ObsConnection? Obs = null,
    double? RoundStartUtcMs = null);

/// <param name="Address">host:port of OBS's WebSocket server.</param>
public sealed record ObsConnection(string Address, string? Password);

/// <param name="Recording">odx record's final status (frame counts); null if it never reported one.</param>
/// <param name="ToolErrors">stderr lines of the capture tools (warnings such as an unreachable NTP server included).</param>
/// <param name="Build">Null when no game profile was selected.</param>
/// <param name="Failure">Why the capture failed, in one line: a tool that had to be killed, the recorder's own error, or
/// its exit code. Null when <paramref name="CaptureOk"/>.</param>
/// <param name="Finalize">video_raw.mp4 + audio.wav → video.mp4, cut to the round; null when it did not run (OBS, no raw video).</param>
/// <param name="Report">The recording's numbers and verdict, for the orchestrator and every machine in the room; null from a fake.</param>
public sealed record RunResult(string SessionDir, bool CaptureOk, RecorderStatus? Recording, int InputEvents,
    IReadOnlyList<string> ToolErrors, BuildResult? Build, string? Failure = null, VideoFinalize.Result? Finalize = null,
    CaptureReport? Report = null)
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
    /// <summary>What is going wrong with the recording, from the recorder's counters (<see cref="CaptureReport.LiveProblem"/>); null while all is well.</summary>
    string? Problem { get; }
    /// <summary>Raised off the caller's thread when <see cref="Problem"/> changes.</summary>
    event Action<string?>? ProblemChanged;
    /// <param name="roundEndUtcMs">The round's end on the orchestrator's clock: the video is cut there; null keeps every frame.</param>
    void Stop(double? roundEndUtcMs = null);
}

/// <summary>
/// One recording: `odx input` and `odx record` run as child processes into the same session folder, both stopped through
/// a shared named event. If either child exits on its own (error, captured window closed), the other is stopped too.
/// In a room the recorder's grid starts at the round's start (<see cref="GridOrigin"/>). Once both have exited (the
/// recorder's stop has its own limits and a watchdog), the video is finalized here (video_raw.mp4 + audio.wav →
/// video.mp4, cut to the round's end: <see cref="VideoFinalize.RunIfNeeded"/>), the folder is built into a spec v2 session
/// with the chosen game profile and validated, then tidied (<see cref="CaptureFolder"/>): the delivered files at the top,
/// the raw logs in raw/.
/// </summary>
public sealed class CaptureRun : ICaptureRun
{
    /// <summary>Until both tools have exited: the recorder's own limits add up to ~47 s and its watchdog ends it at 55 s (RecordCommand), so this never wins.</summary>
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(75);
    /// <summary>After the recorder's stopped status, which follows its files: a teardown that hangs past this is killed without fault.</summary>
    private static readonly TimeSpan ExitGrace = TimeSpan.FromSeconds(10);

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
    /// <summary>The folder OdxArma3.exe starts the game with (its .rpt holds the camera log); null unless the profile's camera_source is arma3-mod.</summary>
    private readonly string? _arma3ProfileDir;
    private readonly double? _roundStartUtcMs, _originRttMs;
    private readonly string? _gridOrigin;
    private double _roundEndUtcMs = double.NaN;
    private readonly double _fps;
    private RecorderStatus? _lastStatus;
    private string? _problem;

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
    public string? Problem => Volatile.Read(ref _problem);
    public event Action<string?>? ProblemChanged;

    private CaptureRun(CaptureOptions options)
    {
        SessionDir = Path.Combine(options.OutputRoot, options.DirName ?? DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        _gameProfile = options.GameProfile;
        _player = options.Player;
        _sessionId = options.DirName;
        _fps = options.Fps;
        Directory.CreateDirectory(SessionDir);
        var profile = TryLoadProfile(options.GameProfile);
        if (profile?.CameraSource == Arma3Camera.Source) _arma3ProfileDir = Arma3Camera.ProfileDir();
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

        double? originQpcMs = null;
        if (options.RoundStartUtcMs is double roundStart)
        {
            _roundStartUtcMs = roundStart;
            if (string.IsNullOrWhiteSpace(options.NtpServer)) _gridOrigin = "no time service";
            else (originQpcMs, _originRttMs, _gridOrigin) = GridOrigin.Measure(options.NtpServer.Trim(), roundStart);
            if (originQpcMs is null) _stderr.Add($"video not aligned to round_start: {_gridOrigin}");
        }

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
        if (originQpcMs is double origin) recordArgs.AddRange(["--grid-origin-qpc-ms", origin.ToString("R", CultureInfo.InvariantCulture)]);
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
                    default:
                        Volatile.Write(ref _lastStatus, status);
                        string? problem = CaptureReport.LiveProblem(status.Frames, status.Duplicates, status.Starved, status.LeadingBlack, _fps);
                        if (problem != Volatile.Read(ref _problem))
                        {
                            Volatile.Write(ref _problem, problem);
                            ProblemChanged?.Invoke(problem);
                        }
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

    public void Stop(double? roundEndUtcMs = null)
    {
        if (roundEndUtcMs is double end) Volatile.Write(ref _roundEndUtcMs, end);
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
        bool recordKilledLate = false;
        // The capture stops: both tools exit (the recorder after its stop path, bounded by its watchdog).
        using (var timeout = new CancellationTokenSource(StopTimeout))
        {
            try
            {
                var exits = Task.WhenAll(input.WaitForExitAsync(timeout.Token), recordExited.WaitAsync(timeout.Token));
                if (await Task.WhenAny(exits, _recorderStopped.Task) != exits)
                {
                    // The stopped status is out, so the files are complete (recording.json goes before it): the capture
                    // teardown after it may hang in WGC, D3D or a driver (a readback thread the stop abandoned, 2026-11-09)
                    // and is given ExitGrace, then killed without fault.
                    if (await Task.WhenAny(exits, Task.Delay(ExitGrace, timeout.Token)) != exits && !record.HasExited)
                    {
                        record.Kill();
                        recordKilledLate = true;
                        lock (_stderr) _stderr.Add($"odx record did not exit within {ExitGrace.TotalSeconds:0} s of its stopped status; killed (its files were complete)");
                    }
                }
                await exits;
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
        await Task.WhenAll(input.WaitForExitAsync(), recordExited);

        var status = LastStatus is { Event: RecorderStatus.Stopped } stopped ? stopped : null;
        bool captureOk = input.ExitCode == 0 && (record.ExitCode == 0 || recordKilledLate && status is { Error: null });
        if (!captureOk)
            failure ??= status?.Error ?? (record.ExitCode != 0 ? $"odx record exited with {record.ExitCode}" : $"odx input exited with {input.ExitCode}");
        // A killed recorder leaves its ffmpeg finishing video_raw.mp4; then the round's frames become video.mp4.
        await SettledAsync(Path.Combine(SessionDir, VideoFinalize.RawVideo));
        long? roundFrames = WriteRoundTimes();
        long total = roundFrames ?? status?.Frames ?? 0;
        var finalize = VideoFinalize.RunIfNeeded(SessionDir, ExternalTools.Ffmpeg(),
            n => SetPhase(CapturePhase.Finalizing, total > 0 ? (int)Math.Clamp(n * 100 / total, 0, 100) : 0));
        if (finalize is { Ok: false })
        {
            captureOk = false;
            failure ??= $"video not finalized: {finalize.Error}";
            lock (_stderr) _stderr.Add(failure);
        }
        string inputCsv = Path.Combine(SessionDir, "input_events.csv");
        int inputEvents = File.Exists(inputCsv) ? Math.Max(0, File.ReadLines(inputCsv).Count() - 1) : 0;
        if (_reforgerDir is not null &&
            ReforgerCamera.Collect(_reforgerDir, Path.GetFileName(SessionDir), SessionDir, TimeSpan.FromSeconds(3)) is { } warning)
            lock (_stderr) _stderr.Add(warning);
        if (_arma3ProfileDir is not null && Arma3Camera.Collect(_arma3ProfileDir, SessionDir, TimeSpan.FromSeconds(3)) is { } arma3Warning)
            lock (_stderr) _stderr.Add(arma3Warning);
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
        // Frames the recorder wrote after round_end are cut by the finalize: standing-in frames among them cost nothing.
        long framesCut = finalize is { Ok: true } f ? Math.Max(0, (status?.Frames ?? 0) - f.Frames) : 0;
        var report = new CaptureReport(finalize?.Frames ?? status?.Frames ?? 0, roundFrames, _fps, status?.Duplicates ?? 0,
            status?.Starved ?? 0, status?.LeadingBlack ?? 0, File.Exists(Path.Combine(SessionDir, VideoFinalize.Output)),
            status?.Audio?.Source, _gridOrigin, build?.Report?.GridOriginOffsetMs, failure, status?.Audio?.Error,
            status?.Stop?.Note, Math.Max(0, (status?.Stop?.TailFrames ?? 0) - framesCut));
        var result = new RunResult(SessionDir, captureOk, status, inputEvents, errors, build, failure, finalize, report);
        SetPhase(CapturePhase.Done, 100);
        return result;
    }

    /// <summary>
    /// The round's times and how the grid was placed, into recording.json, where <see cref="VideoFinalize.RunIfNeeded"/>
    /// takes the frames to keep and the build takes the origin to check. Null outside a room or without the recorder's file.
    /// </summary>
    private long? WriteRoundTimes()
    {
        string path = Path.Combine(SessionDir, "recording.json");
        if (_roundStartUtcMs is not double start || !File.Exists(path)) return null;
        double end = Volatile.Read(ref _roundEndUtcMs);
        try
        {
            var recording = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            recording["round_start_utc_ms"] = start;
            recording["round_end_utc_ms"] = double.IsNaN(end) ? null : end;
            recording["grid_origin"] = _gridOrigin;
            recording["grid_origin_rtt_ms"] = _originRttMs;
            File.WriteAllText(path, recording.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }));
            return VideoFinalize.RoundFrames(recording);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            lock (_stderr) _stderr.Add($"recording.json: {e.Message}");
            return null;
        }
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
