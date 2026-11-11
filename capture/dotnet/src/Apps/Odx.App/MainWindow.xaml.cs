using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Odx.Agent;
using Odx.Capture;
using Odx.Core;
using Odx.Protocol;
using Forms = System.Windows.Forms;

namespace Odx.App;

public sealed record SourceItem(string Label, string? Process)
{
    public override string ToString() => Label;  // what screen readers announce
}

public sealed record GameItem(string Label, string? Path, string? WindowProcess = null)
{
    public override string ToString() => Label;
}

/// <summary>What the OBS test found: OBS's output against spec v3 §2 (1080p target, 720p/30 floor).</summary>
public sealed record ObsCheck(string Version, int Width, int Height, double Fps, bool Recording)
{
    public bool BelowFloor => Width < 1280 || Height < 720 || Fps < 30;
    public bool BelowTarget => Width < 1920 || Height < 1080;
}

public partial class MainWindow : Window
{
    // A low-level keyboard hook, not RegisterHotKey: Raw Input games (Arma 3's RIDEV_NOHOTKEYS) stop RegisterHotKey hotkeys
    // reaching us while they are in the foreground, but a WH_KEYBOARD_LL hook sits below Raw Input and still fires.
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104, WM_KEYUP = 0x0101, WM_SYSKEYUP = 0x0105;
    private const int CountdownSeconds = 3;
    private const int MaxVideosPerBatch = 15;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    private readonly AppSettings _settings = AppSettings.Load();
    private readonly CaptureConfig _config;
    private readonly DailyLogFile _log = new(PackageFolders.Logs);
    private readonly bool _obsRows;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    // Separate from _clock, which SetState stops outside RunState.Recording (a room stays in RunState.Agent).
    private readonly DispatcherTimer _recordTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    /// <summary>What the recording time counts from while it runs; null once stopped. In a room: the end of the 3-2-1.</summary>
    private DateTime? _recordFrom;
    /// <summary>The task ID the host asked a round to start with; the video number advances once the room shows it started.</summary>
    private string? _pendingTask;
    private static readonly System.Windows.Media.SolidColorBrush
        RecordTimerRunning = new(System.Windows.Media.Color.FromRgb(0xF2, 0x54, 0x50)),
        RecordTimerStopped = new(System.Windows.Media.Color.FromRgb(0x8A, 0x91, 0xA0));
    private readonly Forms.NotifyIcon _tray = new();
    private readonly Forms.ToolStripMenuItem _trayStartStop = new(), _trayShow = new(), _trayExit = new();
    private Strings _t = Strings.En;
    /// <summary>Renders the status line; kept so a language switch re-renders it.</summary>
    private Func<Strings, string> _status = t => t.Ready;
    private IntPtr _hook;
    private LowLevelKeyboardProc? _hookProc;
    /// <summary>Set while the hotkey box is being rebound, so the pressed combination assigns instead of toggling.</summary>
    private bool _hotkeySuspended;
    /// <summary>The hotkey is down, so auto-repeat keydowns don't toggle more than once per press.</summary>
    private bool _hotkeyDown;
    private RunState _state = RunState.Idle;
    private CaptureRun? _run;
    private DispatcherTimer? _countdown;
    private CountdownOverlay? _overlay;
    private int _countdownLeft;
    private DateTime _runStarted;
    private bool _closeWhenIdle;
    /// <summary>Windows that can be recorded, listed off the UI thread (~200 ms) once the window is up and on Refresh.</summary>
    private List<SourceItem> _windows = [];
    private bool _windowsListed;
    /// <summary>The room this player is in; null outside one.</summary>
    private RoomSession? _room;
    private AgentClient? _agent => _room?.Agent;
    /// <summary>The last round's outcome for the status line; null before any.</summary>
    private Func<Strings, string>? _roundResult;
    private readonly ElevatedPackets _packets = new();

    /// <summary>
    /// Idle → Countdown → Starting (odx record warming up) → Recording → Stopping (incl. build + validate) → Idle.
    /// With an orchestrator: Idle → Agent (the orchestrator starts and stops recordings) → Idle, or Stopping if recording.
    /// </summary>
    private enum RunState { Idle, Countdown, Starting, Recording, Stopping, Agent }

    public MainWindow()
    {
        InitializeComponent();
        Overlay.Log = _log.Write;
        _obsRows = _settings.RecordWithObs;
        _t = Strings.For(_settings.Language ?? Strings.DefaultLanguage);
        string? configError = null;
        try
        {
            _config = CaptureConfig.Load();
        }
        catch (InvalidDataException e)
        {
            _config = new CaptureConfig((string?)null);
            configError = e.Message;
        }
        LanguageBox.ItemsSource = Strings.Languages;
        LanguageBox.SelectedItem = Strings.Languages.First(l => l.Code == (_settings.Language ?? Strings.DefaultLanguage));
        FpsBox.ItemsSource = new[] { 30, 60 };
        FpsBox.SelectedItem = _settings.Fps;
        AudioBox.IsChecked = _settings.RecordAudio;
        ObsBox.IsChecked = _settings.RecordWithObs;
        ObsAddressBox.Text = _settings.ObsAddress;
        ObsPasswordBox.Password = _settings.ObsPassword ?? "";
        HotkeyBox.Text = _settings.Hotkey.ToString();
        PlayerBox.Text = _settings.PlayerId;
        NameBox.Text = _settings.PlayerName;
        VideoBox.ItemsSource = Enumerable.Range(1, MaxVideosPerBatch).Select(n => n.ToString("00")).ToList();
        TaskBox.Text = _settings.TaskBatch;  // TaskBatch_Changed picks the batch's next video
        var roomRows = _config.Orchestrator is null ? Visibility.Collapsed : Visibility.Visible;
        PlayerLabel.Visibility = PlayerPanel.Visibility = RoomLabel.Visibility = RoomPanel.Visibility = roomRows;
        Loaded += async (_, _) => await RefreshWindowsAsync();
        Loaded += async (_, _) => await CheckEncoderAsync();
        _clock.Tick += (_, _) => ShowRecordingStatus();
        _recordTimer.Tick += (_, _) => ShowRecordTimer();

        _trayStartStop.Click += (_, _) => Toggle();
        using (var icon = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/odx.ico")).Stream)
            _tray.Icon = new System.Drawing.Icon(icon, Forms.SystemInformation.SmallIconSize);
        _tray.ContextMenuStrip = new Forms.ContextMenuStrip();
        _trayShow.Click += (_, _) => RestoreFromTray();
        _trayExit.Click += (_, _) => Close();
        _tray.ContextMenuStrip.Items.AddRange([_trayStartStop, _trayShow, _trayExit]);
        _tray.DoubleClick += (_, _) => RestoreFromTray();
        _tray.Visible = true;
        ApplyLanguage();
        if (configError is not null) SetStatus(t => t.ConfigInvalid(configError));
    }

    private void ApplyLanguage()
    {
        SectionSourceText.Text = _t.SectionSource;
        SectionRoomText.Text = _t.SectionRoom;
        SectionRecordText.Text = _t.SectionRecord;
        // "0.3.13+<commit>": the commit stays in recording.json, the window shows the version only.
        VersionText.Text = Odx.Core.BuildInfo.Version.Split('+')[0];
        LanguageLabel.Content = _t.Language;
        SourceLabel.Content = _t.Source;
        GameLabel.Content = _t.GameProfile;
        FpsLabel.Content = _t.Fps;
        HotkeyLabel.Content = _t.Hotkey;
        PlayerLabel.Content = _t.PlayerId;
        PlayerBox.ToolTip = _t.PlayerIdTooltip;
        NameLabel.Content = _t.PlayerName;
        NameBox.ToolTip = _t.PlayerNameTooltip;
        RoomLabel.Content = _t.Room;
        TaskLabel.Text = _t.TaskId;
        ShowTaskProblem();
        SyncClockButton.Content = _t.SyncClock;
        AudioBox.Content = _t.AudioOption;
        AudioBox.ToolTip = _t.AudioTooltip;
        ObsLabel.Content = _t.Video;
        ObsBox.Content = _t.ObsOption;
        ObsBox.ToolTip = _t.ObsTooltip;
        ObsAddressBox.ToolTip = _t.ObsAddressTooltip;
        ObsPasswordBox.ToolTip = _t.ObsPasswordTooltip;
        ObsTestButton.Content = _t.ObsTest;
        ObsConnectionLabel.Content = _t.ObsConnection;
        ObsAddressLabel.Text = _t.ObsAddress;
        ObsPasswordLabel.Text = _t.ObsPassword;
        ObsTestButton.ToolTip = _t.ObsTestTooltip;
        RoomBox.ToolTip = _t.RoomTooltip;
        RefreshButton.Content = _t.Refresh;
        GameBox.ToolTip = _t.GameTooltip;
        HotkeyBox.ToolTip = _t.HotkeyTooltip;
        RecordTimerPanel.ToolTip = _t.RecordTimerTooltip;
        System.Windows.Automation.AutomationProperties.SetHelpText(RecordTimerText, _t.RecordTimerTooltip);
        _trayShow.Text = _t.TrayShow;
        _trayExit.Text = _t.TrayExit;
        // Rebuild the pickers in the new language, keeping what is selected.
        if (SourceBox.SelectedItem is SourceItem source) _settings.WindowProcess = source.Process;
        if (GameBox.SelectedItem is GameItem game) _settings.GameProfile = game.Path;
        LoadSources();
        LoadGames();
        SetState(_state);
        SetStatus(_status);
    }

    private void Language_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        // Also fires while the constructor fills the box; the constructor applies the language itself.
        if (!IsLoaded || LanguageBox.SelectedItem is not Strings.LanguageOption option) return;
        _settings.Language = option.Code;
        _t = Strings.For(option.Code);
        ApplyLanguage();
    }

    private void SetStatus(Func<Strings, string> status)
    {
        _status = status;
        StatusText.Text = status(_t);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Dark title bar to match the dark window (Windows 10 2004+; older builds ignore it).
        int dark = 1;
        DwmSetWindowAttribute(new System.Windows.Interop.WindowInteropHelper(this).Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        InstallHook();
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState == WindowState.Minimized) Hide();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_state == RunState.Countdown) CancelCountdown();
        if (_state == RunState.Agent) LeaveRoom();
        if (_run is not null)
        {
            e.Cancel = true;
            _closeWhenIdle = true;
            if (_state != RunState.Stopping) StopRecording();
            return;
        }
        SaveSettings();
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
        _tray.Dispose();
        _packets.Dispose();
        base.OnClosed(e);
    }

    /// <summary>Also called when the app is started a second time (App: single instance).</summary>
    public void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void LoadSources()
    {
        var items = new List<SourceItem> { new(_t.PrimaryMonitor, null) };
        items.AddRange(_windows);
        if (_settings.WindowProcess is string saved && !items.Any(i => i.Process == saved))
            items.Add(new SourceItem(_windowsListed ? _t.NotRunning($"{saved}.exe") : $"{saved}.exe", saved));
        SourceBox.ItemsSource = items;
        SourceBox.SelectedItem = items.First(i => i.Process == _settings.WindowProcess);
    }

    /// <summary>Game profiles shipped in games/ next to the app, plus a previously chosen file elsewhere.</summary>
    private void LoadGames()
    {
        var items = new List<GameItem> { new(_t.NoGameProfile, null) };
        string dir = Path.Combine(AppContext.BaseDirectory, "games");
        if (Directory.Exists(dir))
            foreach (string file in Directory.GetFiles(dir, "*.json").Order())
            {
                try
                {
                    var profile = GameProfile.Load(file);
                    items.Add(new GameItem(profile.GameTitle, file, profile.WindowProcess));
                }
                catch (Exception e) when (e is IOException or FormatException or System.Text.Json.JsonException)
                {
                    string name = Path.GetFileName(file), error = e.Message;
                    SetStatus(t => t.SkippedProfile(name, error));
                }
            }
        if (_settings.GameProfile is string saved && File.Exists(saved) && !items.Any(i => i.Path == saved))
            items.Add(new GameItem(Path.GetFileNameWithoutExtension(saved), saved));
        GameBox.ItemsSource = items;
        GameBox.SelectedItem = items.FirstOrDefault(i => i.Path == _settings.GameProfile) ?? items[0];
    }

    private void SaveSettings()
    {
        _settings.WindowProcess = (SourceBox.SelectedItem as SourceItem)?.Process;
        _settings.GameProfile = (GameBox.SelectedItem as GameItem)?.Path;
        _settings.Fps = FpsBox.SelectedItem as int? ?? 30;
        _settings.RecordAudio = AudioBox.IsChecked == true;
        _settings.RecordWithObs = ObsBox.IsChecked == true;
        _settings.ObsAddress = ObsAddressBox.Text.Trim();
        _settings.ObsPassword = ObsPasswordBox.Password.Length > 0 ? ObsPasswordBox.Password : null;
        _settings.PlayerId = PlayerBox.Text.Trim();
        _settings.PlayerName = NameBox.Text.Trim();
        _settings.Save();
    }

    private void Obs_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) SetState(_state);  // also fires while the constructor restores the setting
    }

    /// <summary>Connects to OBS as a recording would, and shows what OBS will record.</summary>
    private async void ObsTest_Click(object sender, RoutedEventArgs e)
    {
        string address = ObsAddressBox.Text.Trim();
        string? password = ObsPasswordBox.Password.Length > 0 ? ObsPasswordBox.Password : null;
        ObsTestButton.IsEnabled = false;
        SetStatus(t => t.ObsTesting(address));
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await using var obs = await ObsClient.ConnectAsync(address, password, timeout.Token);
            var version = await obs.RequestAsync("GetVersion", cancel: timeout.Token);
            var video = await obs.RequestAsync("GetVideoSettings", cancel: timeout.Token);
            var record = await obs.RequestAsync("GetRecordStatus", cancel: timeout.Token);
            var check = new ObsCheck((string?)version["obsVersion"] ?? "?", video["outputWidth"]!.GetValue<int>(),
                video["outputHeight"]!.GetValue<int>(),
                video["fpsNumerator"]!.GetValue<double>() / video["fpsDenominator"]!.GetValue<double>(),
                record["outputActive"]?.GetValue<bool>() == true);
            SetStatus(t => t.ObsConnected(check));
        }
        catch (ObsException ex)
        {
            string error = ex.Message;
            SetStatus(t => t.ObsTestFailed(error));
        }
        catch (OperationCanceledException)
        {
            SetStatus(t => t.ObsTestFailed(t.ObsNoAnswer));
        }
        finally
        {
            ObsTestButton.IsEnabled = _state == RunState.Idle;
        }
    }

    private void Toggle()
    {
        switch (_state)
        {
            case RunState.Idle: BeginCountdown(); break;
            case RunState.Agent when _agent is { IsHost: true, Room: { } room } &&
                                     (room.CurrentSession is not null || Saving(room).Count == 0 && WaitingFor(room).Count == 0):
                ToggleRound();
                break;
            case RunState.Agent when _agent?.Room is { CurrentSession: null }: ToggleReady(); break;
            case RunState.Countdown: CancelCountdown(); break;
            case RunState.Starting or RunState.Recording: StopRecording(); break;
        }
    }

    private void SetState(RunState state, Func<Strings, string>? status = null)
    {
        _state = state;
        if (status is not null) SetStatus(status);
        if (state != RunState.Recording) _clock.Stop();
        bool idle = state == RunState.Idle;
        StartStopButton.Content = state switch
        {
            RunState.Idle => _t.Start,
            RunState.Agent => RoomAction(),
            RunState.Countdown => _t.Cancel(_settings.Hotkey.ToString()),
            RunState.Stopping => _t.Stopping,
            _ => _t.Stop(_settings.Hotkey.ToString()),
        };
        StartStopButton.IsEnabled = _trayStartStop.Enabled = state != RunState.Stopping &&
            !(state == RunState.Agent && (_agent?.Room is not { } room ||
                                          (!_agent.IsHost && room.CurrentSession is not null) ||
                                          (_agent.IsHost && room.CurrentSession is null && (Saving(room).Count > 0 || WaitingFor(room).Count > 0))));
        _trayStartStop.Text = state switch
        {
            RunState.Idle => _t.Start,
            RunState.Agent => RoomAction(),
            RunState.Countdown => _t.TrayCancel,
            RunState.Stopping => _t.Stopping,
            _ => _t.TrayStop,
        };
        SourceBox.IsEnabled = GameBox.IsEnabled = HotkeyBox.IsEnabled = ObsBox.IsEnabled = idle;
        // With OBS, its own frame rate and audio settings apply.
        bool obs = ObsBox.IsChecked == true;
        FpsBox.IsEnabled = AudioBox.IsEnabled = idle && !obs;
        ObsAddressBox.IsEnabled = ObsPasswordBox.IsEnabled = ObsTestButton.IsEnabled = idle;
        // OBS is an expert option: its rows show only when it was already on in the saved settings (odx-app.json).
        ObsLabel.Visibility = ObsBox.Visibility = _obsRows ? Visibility.Visible : Visibility.Collapsed;
        ObsConnectionLabel.Visibility = ObsConnectionPanel.Visibility = ObsTestButton.Visibility =
            obs ? Visibility.Visible : Visibility.Collapsed;
        LanguageBox.IsEnabled = PlayerBox.IsEnabled = NameBox.IsEnabled = idle;
        bool inRoom = state == RunState.Agent && _agent?.Room is not null;
        RoomBox.Visibility = inRoom ? Visibility.Visible : Visibility.Collapsed;
        TaskLabel.Visibility = TaskPanel.Visibility = inRoom && _agent!.IsHost ? Visibility.Visible : Visibility.Collapsed;
        TaskPanel.IsEnabled = inRoom && _agent?.Room is { CurrentSession: null };
        JoinButton.Content = inRoom ? _t.LeaveRoom : _t.JoinRoom;
        JoinButton.IsEnabled = idle || inRoom;
        CreateButton.Content = inRoom ? _t.CloseRoom : _t.CreateRoom;
        CreateButton.IsEnabled = idle || inRoom && _agent!.IsHost;
        CreateButton.Visibility = inRoom && !_agent!.IsHost ? Visibility.Collapsed : Visibility.Visible;
        // In a room the orchestrator's game profile and frame rate apply.
        GameLabel.Visibility = GameBox.Visibility = FpsLabel.Visibility = FpsBox.Visibility = inRoom ? Visibility.Collapsed : Visibility.Visible;
        RefreshButton.IsEnabled = idle;
        _tray.Text = idle ? "Odx Capture" : _t.TrayTooltip(_trayStartStop.Text);
    }

    private void BeginCountdown()
    {
        _countdownLeft = CountdownSeconds;
        RecordTimerText.Text = FormatRecordTime(TimeSpan.Zero);
        _overlay = new CountdownOverlay();
        _overlay.Show();
        _countdown = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _countdown.Tick += (_, _) =>
        {
            if (--_countdownLeft > 0)
            {
                ShowCountdown();
                return;
            }
            // The overlay is closed before odx record starts, so it never appears in the video.
            EndCountdown();
            StartRecording();
        };
        _countdown.Start();
        SetState(RunState.Countdown);
        ShowCountdown();
    }

    private void ShowCountdown()
    {
        _overlay!.Seconds = _countdownLeft;
        int left = _countdownLeft;
        SetStatus(t => t.CountdownStatus(left));
    }

    private void CancelCountdown()
    {
        EndCountdown();
        SetState(RunState.Idle, t => t.Cancelled);
    }

    private void EndCountdown()
    {
        _countdown?.Stop();
        _countdown = null;
        _overlay?.Close();
        _overlay = null;
    }

    private void StartRecording()
    {
        SaveSettings();
        CaptureRun run;
        try
        {
            run = CaptureRun.Start(_settings.ToCaptureOptions(_config.TimeServer));
        }
        catch (Exception ex)
        {
            string error = ex.Message;
            SetState(RunState.Idle, t => t.CouldNotStart(error));
            return;
        }
        _run = run;
        SetState(RunState.Starting, t => t.StartingCapture);
        run.Started.ContinueWith(_ => Dispatcher.Invoke(() =>
        {
            if (_run != run || _state != RunState.Starting) return;
            _runStarted = DateTime.Now;
            SetState(RunState.Recording);
            _clock.Start();
            StartRecordTimer(_runStarted);
            ShowRecordingStatus();
        }), TaskContinuationOptions.OnlyOnRanToCompletion);
        // Shown only once the capture has ended, so the badge never ends up in the video.
        run.CaptureStopped.ContinueWith(_ => Dispatcher.Invoke(() =>
        {
            StopRecordTimer();
            ShowProcessingBadge(run);
        }), TaskContinuationOptions.OnlyOnRanToCompletion);
        run.PhaseChanged += () => Dispatcher.BeginInvoke(() => ShowPhaseStatus(run));
        run.Completion.ContinueWith(t => Dispatcher.Invoke(() => OnRunCompleted(t.Result)),
            TaskContinuationOptions.OnlyOnRanToCompletion);
    }

    /// <summary>
    /// "END" in the middle of the screen, then what the recording is doing (finalizing the video with its progress,
    /// building the session) until the run completes: the player sees the stop is being worked on, not hung.
    /// </summary>
    private void ShowProcessingBadge(ICaptureRun run)
    {
        var badge = new CountdownOverlay { Text = _t.EndOverlay };
        badge.Show();
        void Update()
        {
            string? text = _t.PhaseOverlay(run.Phase, run.FinalizePercent);
            if (text is null) badge.Close();
            else badge.Text = text;
        }
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Update();
        };
        timer.Start();
        run.PhaseChanged += () => Dispatcher.BeginInvoke(() =>
        {
            if (!timer.IsEnabled) Update();
        });
        run.Completion.ContinueWith(_ => Dispatcher.BeginInvoke(badge.Close));
    }

    /// <summary>The status line follows the phases of a stop (solo recording, or a round in a room).</summary>
    private void ShowPhaseStatus(ICaptureRun run)
    {
        if (_run != run || run.Phase is CapturePhase.Recording or CapturePhase.Done) return;
        if (_state == RunState.Stopping || (_state == RunState.Agent && _run is not null))
            SetStatus(t => t.PhaseStatus(run.Phase, run.FinalizePercent));
    }

    private void StopRecording()
    {
        StopRecordTimer();
        SetState(RunState.Stopping, t => t.StoppingStatus);
        _run!.Stop();
    }

    private void OnRunCompleted(RunResult result)
    {
        _run = null;
        StopRecordTimer();
        foreach (string line in RoomSession.LogLines(Path.GetFileName(result.SessionDir), result)) _log.Write(line);
        SetState(RunState.Idle, t => t.Describe(result));
        var (title, body) = _t.Notification(result);
        ToastOverlay.Notify(result.Ok, title, body);
        if (_closeWhenIdle) Close();
    }

    private void ShowRecordingStatus()
    {
        if (_run is null || _state is not (RunState.Recording or RunState.Agent)) return;
        var elapsed = DateTime.Now - _runStarted;
        var status = _run.LastStatus;
        string dir = _run.SessionDir;
        SetStatus(t => t.RecordingStatus(elapsed, status, dir));
        _tray.Text = _t.TrayTooltip($"{elapsed:hh\\:mm\\:ss}");
    }

    /// <summary>Shows the recording time from <paramref name="from"/>; 00:00:00 until then (a room's 3-2-1).</summary>
    private void StartRecordTimer(DateTime from)
    {
        _recordFrom = from;
        ShowRecordTimer();
        _recordTimer.Start();
    }

    /// <summary>Freezes the recording time where it is; it stays shown until the next recording.</summary>
    private void StopRecordTimer()
    {
        if (_recordFrom is null) return;
        _recordTimer.Stop();
        ShowRecordTimer();
        _recordFrom = null;
        RecordTimerDot.Visibility = Visibility.Hidden;
        RecordTimerText.Foreground = RecordTimerStopped;
    }

    private void ShowRecordTimer()
    {
        if (_recordFrom is not { } from) return;
        var elapsed = DateTime.Now - from;
        RecordTimerText.Text = FormatRecordTime(elapsed);
        // Red with the dot once it counts; grey at 00:00:00 during a room's 3-2-1.
        bool counting = elapsed >= TimeSpan.Zero;
        RecordTimerDot.Visibility = counting ? Visibility.Visible : Visibility.Hidden;
        RecordTimerText.Foreground = counting ? RecordTimerRunning : RecordTimerStopped;
    }

    private static string FormatRecordTime(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        return $"{(int)elapsed.TotalHours:00}:{elapsed:mm\\:ss}";
    }

    /// <summary>What the main button and the hotkey do in a room: the host starts/ends rounds, the others say they are ready.</summary>
    private string RoomAction()
    {
        string hotkey = _settings.Hotkey.ToString();
        if (_agent?.Room is not { } room) return _t.JoiningRoom;
        if (_agent.IsHost && room.CurrentSession is null && Saving(room) is { Count: > 0 } saving) return _t.WaitingSaving(saving.Count);
        if (_agent.IsHost && room.CurrentSession is null && WaitingFor(room) is { Count: > 0 } waiting)
            return _t.WaitingReady(room.Members.Count - 1 - waiting.Count, room.Members.Count - 1);
        if (_agent.IsHost) return room.CurrentSession is null ? _t.StartRound(hotkey) : _t.EndRound(hotkey);
        if (room.CurrentSession is not null) return _t.InRound;
        return _agent.IsReady ? _t.UnreadyButton(hotkey) : _t.ReadyButton(hotkey);
    }

    /// <summary>The other members the host waits for (the orchestrator checks the same before a round starts).</summary>
    private static List<RoomMember> WaitingFor(RoomUpdate room) =>
        room.Members.Where(m => m.PlayerId != room.HostPlayerId && !(m.Ready && m.State is "idle" or "armed")).ToList();

    /// <summary>Members, the host included, still saving the last round (the orchestrator refuses a new round until they are done).</summary>
    private static List<RoomMember> Saving(RoomUpdate room) => room.Members.Where(m => m.State is "starting" or "recording" or "saving").ToList();

    private void Join_Click(object sender, RoutedEventArgs e)
    {
        if (_state == RunState.Agent)
        {
            LeaveRoom();
            return;
        }
        var dialog = new JoinRoomDialog(_t) { Owner = this };
        if (dialog.ShowDialog() == true) EnterRoom(dialog.Code);
    }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        if (_state == RunState.Agent) CloseRoom();
        else EnterRoom(null);
    }

    /// <param name="code">Invite code to join; null opens a new room with this player as its host.</param>
    private void EnterRoom(string? code, bool lan = false)
    {
        SaveSettings();
        if (_config.Orchestrator is not { } orchestrator || !PlayerIdentity.IsValidId(_settings.PlayerId) || _settings.PlayerName.Length == 0)
        {
            SetStatus(t => t.PlayerIdentityRequired);
            return;
        }
        var room = new RoomSession(new AgentOptions(orchestrator, _settings.PlayerId, _settings.PlayerName, PackageFolders.Captures,
            _settings.WindowProcess, RoomCode: code, CreateRoom: code is null, AccessKey: _config.AccessKey, Lan: lan,
            TimeServer: _config.TimeServer),
            StartAgentCapture, _packets.GetAsync, _log, _settings.RecordAudio && !_settings.RecordWithObs);
        var agent = room.Agent;
        agent.Countdown += (ms, cancelled) => Dispatcher.BeginInvoke(() => ShowAgentCountdown(ms, cancelled));
        room.Changed += () => Dispatcher.BeginInvoke(() =>
        {
            if (_room == room && _run is null && _state == RunState.Agent) ShowAgentStatus();
        });
        room.LanMissing += () => Dispatcher.BeginInvoke(() =>
        {
            if (_room == room) OfferZeroTier(room);
        });
        agent.RoundKept += (session, result) => Dispatcher.BeginInvoke(() =>
        {
            if (_room != room) return;
            bool ok = result.CaptureOk;
            string dir = result.SessionDir;
            _roundResult = ok ? t => t.RoundKept(session, dir) : t => t.RoundCaptureFailed(session, dir);
            ToastOverlay.Notify(ok, _roundResult(_t));
            if (_run is null) ShowAgentStatus();
        });
        // Once every player is done: the whole room's verdict, the same on every machine (who to record again, and why).
        agent.RoundFinished += record => Dispatcher.BeginInvoke(() =>
        {
            if (_room != room) return;
            static string Line(RoundPlayer p) => $"{p.Name}: {p.Verdict ?? "not done"}" +
                                                 (p.Problems is { Count: > 0 } problems ? $", {string.Join("; ", problems)}" : "");
            var failed = record.Players.Where(p => p.Verdict is not (CaptureReport.Ok or CaptureReport.Degraded)).Select(Line).ToList();
            var degraded = record.Players.Where(p => p.Verdict == CaptureReport.Degraded).Select(Line).ToList();
            _roundResult = t => t.RoundVerdict(record.SessionId, failed, degraded);
            ToastOverlay.Notify(failed.Count == 0, _roundResult(_t));
            if (_run is null) ShowAgentStatus();
        });
        agent.RoomChanged += update => Dispatcher.BeginInvoke(() =>
        {
            if (_room != room) return;
            if (update is null)
            {
                Disconnect(t => t.RoomClosed);
                return;
            }
            RoomBox.Text = update.Code;
            if (_pendingTask is { } pending && update.CurrentSession == pending) TaskStarted(pending);
            SetState(_state);
            if (_run is null) ShowAgentStatus();
        });
        agent.RoomError += error => Dispatcher.BeginInvoke(() =>
        {
            if (_room != room) return;
            if (agent.Room is null) Disconnect(_ => error);
            else
            {
                SetStatus(_ => error);
                if (agent.IsHost)
                {
                    _pendingTask = null;
                    ToastOverlay.Notify(false, error);  // e.g. the round refused: the host is in game
                }
            }
        });
        _room = room;
        _roundResult = null;
        SetState(RunState.Agent, t => t.JoiningRoom);
        _ = room.RunAsync();
    }

    /// <summary>
    /// The room plays over a virtual LAN this machine cannot join yet: offer to install ZeroTier (winget, one UAC prompt),
    /// start its tray app (it gives the user a copy of the service's API token), then join again.
    /// </summary>
    private async void OfferZeroTier(RoomSession room)
    {
        if (MessageBox.Show(this, _t.LanInstallQuestion, _t.LanInstallTitle, MessageBoxButton.YesNo, MessageBoxImage.Question)
            != MessageBoxResult.Yes) return;
        SetStatus(t => t.LanInstalling);
        bool installed = await Task.Run(() =>
        {
            try
            {
                if (ZeroTierApi.LocalToken() is null && !File.Exists(ZeroTierTray))
                {
                    using var winget = Process.Start(new ProcessStartInfo("winget",
                        "install --id ZeroTier.ZeroTierOne -e --silent --accept-package-agreements --accept-source-agreements")
                        { UseShellExecute = false, CreateNoWindow = true })!;
                    winget.WaitForExit();
                }
                if (ZeroTierApi.LocalToken() is null && File.Exists(ZeroTierTray)) Process.Start(new ProcessStartInfo(ZeroTierTray) { UseShellExecute = true });
                for (int i = 0; i < 30 && ZeroTierApi.LocalToken() is null; i++) Thread.Sleep(1000);
                return ZeroTierApi.LocalToken() is not null;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return false;
            }
        });
        if (_room != room) return;
        if (!installed)
        {
            SetStatus(t => t.LanInstallFailed);
            return;
        }
        await room.JoinLanAsync();
    }

    private static readonly string ZeroTierTray =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "ZeroTier", "One", "zerotier_desktop_ui.exe");

    private async void LeaveRoom()
    {
        if (_room is { } room)
        {
            _room = null;  // its RoomChanged(null) is ours, not the host's
            await room.LeaveAsync();
            room.Dispose();
        }
        Disconnect(t => t.LeftRoom);
    }

    private async void CloseRoom()
    {
        if (_room is { } room)
        {
            _room = null;
            await room.CloseAsync();
            room.Dispose();
        }
        Disconnect(t => t.LeftRoom);
    }

    private void Disconnect(Func<Strings, string> status)
    {
        _room?.Dispose();
        _room = null;
        if (_run is null) SetState(RunState.Idle, status);
        else StopRecording();
    }

    private void ShowAgentStatus()
    {
        if (_room is not { Agent.Room: { } room } session)
        {
            SetStatus(t => t.JoiningRoom);
            return;
        }
        string log = session.LastLog, hotkey = _settings.Hotkey.ToString();
        bool host = session.Agent.IsHost, ready = session.Agent.IsReady;
        string members = string.Join(" · ", room.Members.Select(m =>
            m.Name + (m.PlayerId == room.HostPlayerId ? " (host)" : "") + (m.Ready ? " ✓" : "") + _t.MemberState(m.State) +
            (m.Problem is { } problem ? $" ⚠ {problem}" : "")));
        var lan = session.Lan;
        bool? packets = session.Packets;
        var round = _roundResult;
        var clock = session.Agent.Clock;
        bool clockOff = _config.TimeServer is not null && clock is { Ok: false, NtpError: null };
        SyncClockButton.Visibility = clockOff && !_syncingClock ? Visibility.Visible : Visibility.Collapsed;
        SetStatus(t => t.RoomStatus(room.Code, host, members) + (round is null ? "" : "\n" + round(t)) +
                       (_config.TimeServer is null ? "" : "\n" + (_syncingClock ? t.ClockSyncing : t.ClockLine(clock))) +
                       (session.Agent.Capture is { } capture ? "\n" + t.CaptureLine(capture) +
                                                               (capture.Audio is { } audio ? "\n" + t.AudioLine(audio) : "") : "") +
                       (lan is null ? "" : "\n" + t.LanStatus(lan)) +
                       (packets is { } on ? "\n" + (on ? t.PacketsOn : t.PacketsOff) : "") + "\n" + log + "\n" +
                       (host ? t.HostHint(hotkey) : ready ? t.ReadyOn(hotkey) : t.ReadyHint(hotkey)));
    }

    private bool _syncingClock;

    /// <summary>Sets the Windows clock from NTP (UAC prompt), then measures again; the status line shows the outcome.</summary>
    private async void SyncClock_Click(object sender, RoutedEventArgs e)
    {
        if (_room is not { } room || _config.TimeServer is not { } ntp || _syncingClock) return;
        _syncingClock = true;
        ShowAgentStatus();
        await ClockSyncTool.RunElevatedAsync(ExternalTools.Odx(), ntp);
        await room.CheckClockAsync();  // the clock line then shows whether it worked
        _syncingClock = false;
        ShowAgentStatus();
    }

    private async void ToggleReady()
    {
        if (_room is not { } room) return;
        await room.ToggleReadyAsync();
        if (_run is null) ShowAgentStatus();
    }

    /// <summary>Why the task ID cannot start a round, or null with <paramref name="task"/> = &lt;batch&gt;-S&lt;video&gt;.</summary>
    private string? TaskProblem(Strings t, out string? task)
    {
        task = null;
        string batch = TaskBox.Text.Trim();
        if (batch.Length == 0) return t.TaskBatchMissing;
        if (VideoBox.SelectedIndex < 0) return t.TaskBatchFull(batch);
        string id = $"{batch}-S{VideoBox.SelectedIndex + 1:00}";
        // Windows drops a trailing dot from a folder name: two task IDs would share one folder.
        if (!Odx.Protocol.ToggleRound.IsValidSessionId(id) || batch.EndsWith('.')) return t.TaskBatchInvalid;
        task = id;
        return null;
    }

    private void ShowTaskProblem()
    {
        if (TaskProblem(_t, out _) is { } problem)
        {
            TaskBox.BorderBrush = RecordTimerRunning;
            TaskPanel.ToolTip = problem;
        }
        else
        {
            TaskBox.ClearValue(BorderBrushProperty);
            TaskPanel.ToolTip = _t.TaskIdTooltip;
        }
    }

    /// <summary>The batch of the last round keeps its next video; any other batch starts at S01.</summary>
    private void TaskBatch_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        int next = TaskBox.Text.Trim() == _settings.TaskBatch ? _settings.TaskVideo : 1;
        VideoBox.SelectedIndex = next is >= 1 and <= MaxVideosPerBatch ? next - 1 : -1;
        ShowTaskProblem();
    }

    private void TaskVideo_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => ShowTaskProblem();

    /// <summary>The round started with the host's task ID: the batch is kept and the next round gets the next video.</summary>
    private void TaskStarted(string started)
    {
        _pendingTask = null;
        // From the started ID, not the boxes: the host may have typed in them since pressing the hotkey.
        int cut = started.LastIndexOf("-S", StringComparison.Ordinal);
        _settings.TaskBatch = started[..cut];
        _settings.TaskVideo = int.Parse(started[(cut + 2)..], System.Globalization.CultureInfo.InvariantCulture) + 1;
        _settings.Save();
        if (TaskBox.Text.Trim() == _settings.TaskBatch)
            VideoBox.SelectedIndex = _settings.TaskVideo <= MaxVideosPerBatch ? _settings.TaskVideo - 1 : -1;
        ShowTaskProblem();
    }

    private async void ToggleRound()
    {
        if (_room is not { } room) return;
        string? task = null;
        if (room.Agent.Room is { CurrentSession: null })
        {
            if (TaskProblem(_t, out task) is { } problem)
            {
                SetStatus(t => TaskProblem(t, out _) ?? t.TaskIdRequired);
                // The host presses the hotkey in game: a notice on the screen (excluded from capture, silent) says why.
                ToastOverlay.Notify(false, _t.TaskNotStarted, problem);
                return;
            }
            _pendingTask = task;
        }
        SendToggleRound(room, task);
    }

    private async void SendToggleRound(RoomSession room, string? task)
    {
        try
        {
            await room.ToggleRoundAsync(CountdownSeconds * 1000, task);
        }
        catch (Exception e) when (e is System.Net.WebSockets.WebSocketException or OperationCanceledException)
        {
            _pendingTask = null;
            string error = e.Message;
            SetStatus(t => t.RoundToggleFailed(error));
        }
    }

    /// <summary>The same 3-2-1 as a solo recording, shown while the capture already runs (the overlay is excluded from it).</summary>
    private void ShowAgentCountdown(int countdownMs, CancellationToken cancelled)
    {
        int left = (int)Math.Ceiling(countdownMs / 1000.0);
        // The delivered video starts at round_start, the end of the 3-2-1: the recording time counts from there too.
        StartRecordTimer(DateTime.Now.AddMilliseconds(countdownMs));
        var overlay = new CountdownOverlay { Seconds = left };
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(countdownMs / (double)left) };
        bool closed = false;
        void End()
        {
            if (closed) return;
            closed = true;
            timer.Stop();
            overlay.Close();
        }
        timer.Tick += (_, _) =>
        {
            if (--left > 0) overlay.Seconds = left;
            else End();
        };
        cancelled.Register(() => Dispatcher.BeginInvoke(() =>
        {
            End();
            if (_run is null) StopRecordTimer();  // the round was called off before any capture started
        }));
        overlay.Show();
        timer.Start();
    }

    /// <summary>AgentClient calls this off the UI thread when the orchestrator starts a round.</summary>
    private ICaptureRun StartAgentCapture(CaptureOptions options)
    {
        var run = CaptureRun.Start(options with { Audio = _settings.RecordAudio, Obs = _settings.Obs });
        Dispatcher.Invoke(() =>
        {
            _run = run;
            _roundResult = null;
            _runStarted = DateTime.Now;
            _clock.Start();
            if (_recordFrom is null) StartRecordTimer(_runStarted);  // else the 3-2-1 already set it
            ShowRecordingStatus();
        });
        run.CaptureStopped.ContinueWith(_ => Dispatcher.Invoke(() =>
        {
            StopRecordTimer();
            ShowProcessingBadge(run);
        }), TaskContinuationOptions.OnlyOnRanToCompletion);
        run.PhaseChanged += () => Dispatcher.BeginInvoke(() => ShowPhaseStatus(run));
        run.Completion.ContinueWith(t => Dispatcher.Invoke(() => OnAgentRunCompleted(t.Result)),
            TaskContinuationOptions.OnlyOnRanToCompletion);
        return run;
    }

    private void OnAgentRunCompleted(RunResult result)
    {
        _run = null;
        _clock.Stop();
        StopRecordTimer();
        // The notice comes once the capture is built (RoundKept).
        if (_state == RunState.Stopping) SetState(RunState.Idle, t => t.LeftRoom);
        else ShowAgentStatus();
        if (_closeWhenIdle) Close();
    }

    /// <summary>Picking a game whose window is open switches the source from the monitor to that window.</summary>
    private void Game_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _state != RunState.Idle || GameBox.SelectedItem is not GameItem { WindowProcess: { } process } ||
            SourceBox.SelectedItem is not SourceItem { Process: null } || !_windows.Any(w => w.Process == process))
            return;
        _settings.WindowProcess = process;
        LoadSources();
    }

    private void StartStop_Click(object sender, RoutedEventArgs e) => Toggle();

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshWindowsAsync();

    /// <summary>Settles which encoder "auto" means (a ~2 s probe, cached) before the first recording needs it.</summary>
    private async Task CheckEncoderAsync()
    {
        string encoder = await Task.Run(() => EncoderChoice.Resolve(ExternalTools.Ffmpeg(), "auto"));
        if (_state == RunState.Idle && StatusText.Text == _t.Ready) SetStatus(t => t.ReadyWithEncoder(encoder));
    }

    private async Task RefreshWindowsAsync()
    {
        int self = Environment.ProcessId;
        _windows = await Task.Run(() =>
        {
            var windows = new List<SourceItem>();
            foreach (var p in Process.GetProcesses())
            {
                using (p)
                {
                    if (p.Id == self || p.MainWindowHandle == IntPtr.Zero || string.IsNullOrWhiteSpace(p.MainWindowTitle))
                        continue;
                    windows.Add(new SourceItem($"{p.MainWindowTitle} ({p.ProcessName}.exe)", p.ProcessName));
                }
            }
            return windows;
        });
        _windowsListed = true;
        if (SourceBox.SelectedItem is SourceItem selected) _settings.WindowProcess = selected.Process;
        LoadSources();
    }

    // While the hotkey box has focus the global hotkey is released, so pressing the current combination re-assigns it
    // instead of toggling a recording.
    private void Hotkey_GotFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _hotkeySuspended = true;
        HotkeyBox.Text = _t.PressKeys;
    }

    private void Hotkey_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        HotkeyBox.Text = _settings.Hotkey.ToString();
        _hotkeySuspended = false;
    }

    private void Hotkey_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt
            or Key.LWin or Key.RWin)
            return;
        _settings.Hotkey = new Hotkey((HotkeyModifiers)Keyboard.Modifiers, KeyInterop.VirtualKeyFromKey(key));
        _settings.Save();
        Keyboard.ClearFocus();
        StartStopButton.Focus();
    }

    private void InstallHook()
    {
        _hookProc = HookProc;  // kept in a field so the delegate is not collected while the hook is live
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) SetStatus(t => t.HotkeyInUse(_settings.Hotkey.ToString()));
    }

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && !_hotkeySuspended)
        {
            int msg = wParam.ToInt32();
            var hk = _settings.Hotkey;
            bool match = Marshal.ReadInt32(lParam) == hk.VirtualKey && CurrentModifiers() == hk.Modifiers;
            if (match && msg is WM_KEYDOWN or WM_SYSKEYDOWN)
            {
                if (!_hotkeyDown)
                {
                    _hotkeyDown = true;
                    Dispatcher.BeginInvoke(Toggle);
                }
                return 1;  // swallow so the key does not reach the game (the hotkey is never a gameplay binding)
            }
            if (match && msg is WM_KEYUP or WM_SYSKEYUP)
            {
                _hotkeyDown = false;
                return 1;
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private static HotkeyModifiers CurrentModifiers()
    {
        static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;
        var mods = HotkeyModifiers.None;
        if (Down(0x11)) mods |= HotkeyModifiers.Ctrl;   // VK_CONTROL
        if (Down(0x10)) mods |= HotkeyModifiers.Shift;  // VK_SHIFT
        if (Down(0x12)) mods |= HotkeyModifiers.Alt;    // VK_MENU
        if (Down(0x5B) || Down(0x5C)) mods |= HotkeyModifiers.Win;  // VK_LWIN / VK_RWIN
        return mods;
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
