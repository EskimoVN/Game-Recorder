using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Odx.Capture;
using Odx.Core;

namespace Odx.App;

/// <summary>
/// Eskimo: this machine says ready, and the host starts a round, only with an exact 16:9 game frame and the profile's
/// keys (RecordingGate). Checked when ready is pressed, every 2 s while ready between rounds (a resized window or a
/// rebound key takes the player out of ready, so the room's own rule then holds the host's start), and on the host's start.
/// While recording only notices; once the video is kept, the team's quick checks.
/// </summary>
public partial class MainWindow
{
    private readonly DispatcherTimer _gateTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private (int Width, int Height)? _gateWindowSize;
    private bool _gateBusy;

    private sealed record GateProblem(string Message, bool Blocks);

    private void StartReadyGate()
    {
        _gateTimer.Tick += (_, _) => CheckReadyGate();
        _gateTimer.Start();
    }

    /// <summary>
    /// Shows why this machine may not record now, in the status line and on the game's screen; false when that blocks it,
    /// or while a check already runs (a second press during the ~2 s probe would toggle twice).
    /// </summary>
    private async Task<bool> PassesGateAsync(RoomSession room, string title)
    {
        if (_gateBusy) return false;
        _gateBusy = true;
        GateProblem? problem;
        try
        {
            problem = await GateProblemAsync(room);
        }
        finally
        {
            _gateBusy = false;
        }
        if (problem is null) return true;
        ToastOverlay.Notify(false, title, problem.Message);
        if (!problem.Blocks) return true;
        string message = problem.Message;
        SetStatus(_ => message);
        return false;
    }

    /// <summary>The frame of a fresh capture probe (the size the video would have), then the game's keys.</summary>
    private async Task<GateProblem?> GateProblemAsync(RoomSession room)
    {
        var probe = await room.Agent.CheckCaptureAsync();
        string? process = probe is null ? null : RecordingGate.WindowProcess(probe.Source);
        _gateWindowSize = process is null ? null : ClientSize(process);
        if (probe is { Ok: true } p && !RecordingGate.Is16By9(p.Width, p.Height))
            return new GateProblem(_t.FrameNot16By9(p.Width, p.Height, p.WindowStyle == "windowed"), true);
        if (probe is not null && AudioProblem(probe) is { } audio) return new GateProblem(audio, true);
        return await Task.Run(() => KeysProblem(process));
    }

    /// <summary>The video must carry the game's own sound only: the whole device's would bring in voices (Discord).</summary>
    private string? AudioProblem(CaptureProbe probe)
    {
        if (_settings.RecordWithObs) return null;  // OBS records the sound itself
        if (!_settings.RecordAudio) return _t.AudioOff;
        return probe.Audio switch
        {
            null or { Source: AudioStatus.NotRecorded } => _t.AudioMissing(probe.Audio?.Error),
            { Source: AudioStatus.FromProcess } => null,
            var a => _t.AudioNotGameOnly(a.FallbackReason),
        };
    }

    private GateProblem? KeysProblem(string? process)
    {
        if (process == RecordingGate.Arma3Process && RecordingGate.Arma3ReboundActionsNow() is { } actions)
            return new GateProblem(_t.Arma3KeysRebound(string.Join(", ", actions)), true);
        // Which entries this file holds with the default keys is not yet seen on a player's machine: a notice, no block.
        if (process == RecordingGate.ReforgerProcess && RecordingGate.ReforgerReboundFileNow() is { } file)
            return new GateProblem(_t.ReforgerKeysRebound(file), false);
        return null;
    }

    private async void CheckReadyGate()
    {
        if (_gateBusy || _run is not null || _room is not { } room ||
            room.Agent is not { IsReady: true, IsHost: false, Room.CurrentSession: null })
            return;
        _gateBusy = true;
        try
        {
            string? process = room.Agent.Capture is { } capture ? RecordingGate.WindowProcess(capture.Source) : null;
            // The window's client size only tells it changed (this process may see it DPI-scaled); the probe measures it.
            bool resized = process is not null && _gateWindowSize is { } was && ClientSize(process) is { } now && now != was;
            var problem = resized ? await GateProblemAsync(room) : await Task.Run(() => KeysProblem(process));
            if (problem is not { Blocks: true } || _run is not null ||
                room.Agent is not { IsReady: true, Room.CurrentSession: null })
                return;
            ToastOverlay.Notify(false, _t.NotReadyTitle, problem.Message);
            await room.ToggleReadyAsync();
            string message = problem.Message;
            SetStatus(_ => message);
        }
        finally
        {
            _gateBusy = false;
        }
    }

    private Func<Strings, string>? _quickCheck;

    /// <summary>
    /// Notices over the game while recording only where they cannot get into the video: Windows 11 excludes overlays from
    /// capture, and a window source never holds other windows (Windows 10 overlays stay capturable, see Overlay).
    /// </summary>
    private bool NoticeSafeWhileRecording() =>
        OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) ||
        (_room?.Agent.Capture is { } capture && RecordingGate.WindowProcess(capture.Source) is not null);

    /// <summary>A round's capture started: its live problems (CaptureReport.LiveProblem) show over the game.</summary>
    private void WatchRecording(ICaptureRun run)
    {
        _quickCheck = null;
        run.ProblemChanged += problem => Dispatcher.BeginInvoke(() =>
        {
            if (problem is not null && _run == run && NoticeSafeWhileRecording())
                ToastOverlay.Notify(false, _t.RecordingWarning, _t.LiveProblem(problem));
        });
    }

    /// <summary>The team's quick checks of this machine's video (RoundQuickCheck), once it is kept; the next round need not wait.</summary>
    private async void QuickCheck(RunResult result)
    {
        var room = _room;
        IReadOnlyList<QuickItem> items;
        try
        {
            items = await Task.Run(() => RoundQuickCheck.Run(result, ExternalTools.Ffmpeg()));
        }
        catch (Exception e)
        {
            _log.Write($"quick check of {result.SessionDir} failed: {e.Message}");
            return;
        }
        if (_run is not null || _room != room || room is null) return;  // the next round already records, or the room was left
        int problems = items.Count(i => i.Level != QuickLevel.Ok);
        _quickCheck = t => t.QuickTitle(problems) + "\n" + string.Join("\n", items.Select(t.QuickLine));
        ToastOverlay.Notify(problems == 0, _t.QuickTitle(problems),
            problems == 0 ? null : string.Join("\n", items.Where(i => i.Level != QuickLevel.Ok).Select(_t.QuickLine)));
        if (_state == RunState.Agent) ShowAgentStatus();
    }

    private static (int Width, int Height)? ClientSize(string process)
    {
        var running = Process.GetProcessesByName(process);
        try
        {
            foreach (var p in running)
                if (p.MainWindowHandle != IntPtr.Zero && GetClientRect(p.MainWindowHandle, out var r))
                    return (r.Right - r.Left, r.Bottom - r.Top);
            return null;
        }
        finally
        {
            foreach (var p in running) p.Dispose();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ClientRect
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hwnd, out ClientRect rect);
}
