using System.Globalization;
using Odx.Agent;
using Odx.Capture;
using Odx.Core;

namespace Odx.App;

/// <summary>
/// Every UI text of odx-app in English and Vietnamese. Validator issue messages stay in English: they quote spec terms
/// and column names.
/// </summary>
public sealed class Strings
{
    public const string English = "en", Vietnamese = "vi";

    public sealed record LanguageOption(string Code, string Name)
    {
        public override string ToString() => Name;  // what screen readers announce
    }

    public static readonly IReadOnlyList<LanguageOption> Languages = [new(English, "English"), new(Vietnamese, "Tiếng Việt")];

    /// <summary>The Windows display language if it is Vietnamese, otherwise English.</summary>
    public static string DefaultLanguage =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == Vietnamese ? Vietnamese : English;

    public static Strings For(string? code) => code == Vietnamese ? Vi : En;

    public required string Language { get; init; }
    public required string Source { get; init; }
    public required string GameProfile { get; init; }
    public required string Fps { get; init; }
    public required string Hotkey { get; init; }
    public required string Refresh { get; init; }
    public required string GameTooltip { get; init; }
    public required string HotkeyTooltip { get; init; }
    public required string PlayerId { get; init; }
    public required string PlayerIdTooltip { get; init; }
    public required string PlayerName { get; init; }
    public required string PlayerNameTooltip { get; init; }
    public required string Room { get; init; }
    public required string RoomTooltip { get; init; }
    public required string EnterInviteCode { get; init; }
    public required string CancelDialog { get; init; }
    public required string JoinRoom { get; init; }
    public required string CreateRoom { get; init; }
    public required string LeaveRoom { get; init; }
    public required string CloseRoom { get; init; }
    public required string AudioOption { get; init; }
    public required string AudioTooltip { get; init; }
    public required string Video { get; init; }
    public required string ObsOption { get; init; }
    public required string ObsTooltip { get; init; }
    public required string ObsAddressTooltip { get; init; }
    public required string ObsPasswordTooltip { get; init; }
    public required string ObsTest { get; init; }
    public required string ObsConnection { get; init; }
    public required string ObsAddress { get; init; }
    public required string ObsPassword { get; init; }
    public required string ObsTestTooltip { get; init; }
    public required Func<string, string> ObsTesting { get; init; }
    public required Func<ObsCheck, string> ObsConnected { get; init; }
    public required Func<string, string> ObsTestFailed { get; init; }
    public required string ObsNoAnswer { get; init; }
    public required Func<LanState, string> LanStatus { get; init; }
    public required string LanInstallTitle { get; init; }
    public required string LanInstallQuestion { get; init; }
    public required string LanInstalling { get; init; }
    public required string LanInstallFailed { get; init; }
    public required string PacketsOn { get; init; }
    public required string PacketsOff { get; init; }

    public required string PrimaryMonitor { get; init; }
    public required Func<string, string> NotRunning { get; init; }
    public required string NoGameProfile { get; init; }
    public required Func<string, string, string> SkippedProfile { get; init; }

    public required string Start { get; init; }
    public required Func<string, string> Stop { get; init; }
    public required Func<string, string> Cancel { get; init; }
    public required string Stopping { get; init; }
    public required string TrayShow { get; init; }
    public required string TrayExit { get; init; }
    public required string TrayCancel { get; init; }
    public required string TrayStop { get; init; }

    public required string Ready { get; init; }
    public required Func<string, string> ReadyWithEncoder { get; init; }
    public required Func<int, string> CountdownStatus { get; init; }
    public required string Cancelled { get; init; }
    public required string StartingCapture { get; init; }
    public required Func<TimeSpan, RecorderStatus?, string, string> RecordingStatus { get; init; }
    public required string StoppingStatus { get; init; }
    public required Func<int, string> FinalizingStatus { get; init; }
    public required string BuildingStatus { get; init; }
    public required Func<string, string> CouldNotStart { get; init; }
    public required Func<string, string> HotkeyInUse { get; init; }
    public required string PressKeys { get; init; }
    public required Func<string, string> HostHint { get; init; }
    public required Func<string, string, string> RoundKept { get; init; }
    public required Func<string, string, string> RoundCaptureFailed { get; init; }
    public required string PlayerIdentityRequired { get; init; }
    public required string JoiningRoom { get; init; }
    public required Func<string, bool, string, string> RoomStatus { get; init; }
    /// <summary>What a member is doing, after their name: starting, recording, saving; empty when idle.</summary>
    public required Func<string, string> MemberState { get; init; }
    /// <summary>The round's outcome for the whole room: the players to record again (failed) and those to check (degraded), each with their problems.</summary>
    public required Func<string, IReadOnlyList<string>, IReadOnlyList<string>, string> RoundVerdict { get; init; }
    public required Func<string, string> StartRound { get; init; }
    public required Func<int, int, string> WaitingReady { get; init; }
    public required Func<int, string> WaitingSaving { get; init; }
    public required string TaskId { get; init; }
    public required string TaskIdTooltip { get; init; }
    public required string TaskIdRequired { get; init; }
    public required string RecordTimerTooltip { get; init; }
    public required string TaskBatchMissing { get; init; }
    public required string TaskBatchInvalid { get; init; }
    public required Func<string, string> TaskBatchFull { get; init; }
    public required string TaskNotStarted { get; init; }
    public required Func<string, string, string> TaskRetake { get; init; }
    public required Func<string, string> EndRound { get; init; }
    public required Func<string, string> ReadyButton { get; init; }
    public required Func<string, string> UnreadyButton { get; init; }
    public required string InRound { get; init; }
    public required string LeftRoom { get; init; }
    public required string RoomClosed { get; init; }
    public required Func<string, string> ReadyHint { get; init; }
    public required Func<string, string> ReadyOn { get; init; }
    public required Func<string, string> RoundToggleFailed { get; init; }
    public required Func<string, string> TrayTooltip { get; init; }
    public required string EndOverlay { get; init; }
    public required Func<int, string> FinalizingOverlay { get; init; }
    public required string BuildingOverlay { get; init; }
    public required string NotifySavedPass { get; init; }
    public required string NotifySavedRaw { get; init; }
    public required string NotifyFailed { get; init; }

    public required Func<string, string> Saved { get; init; }
    public required Func<string, string> StoppedWithErrors { get; init; }
    public required Func<long, int, int, double, long, long, string> FramesSummary { get; init; }
    public required Func<int, string> InputEvents { get; init; }
    public required string NoProfileNoBuild { get; init; }
    public required Func<string, string> BuildFailed { get; init; }
    public required Func<int, int, string> ValidationPass { get; init; }
    public required Func<int, int, string> ValidationFail { get; init; }
    public required Func<string, string> UnboundKeys { get; init; }
    public required Func<string, double, double, string> ClockNtp { get; init; }
    public required string ClockSystem { get; init; }
    public required Func<string, string> ConfigInvalid { get; init; }
    public required Func<double, string> ClockWarning { get; init; }
    /// <summary>One line on the audio track of a recording (odx record --audio).</summary>
    public required Func<AudioStatus, string> AudioLine { get; init; }
    /// <summary>The room status line for the capture probe (what a round would record, and whether it can be).</summary>
    public required Func<CaptureProbe, string> CaptureLine { get; init; }
    /// <summary>The room status line for the pre-round clock check (null: still measuring).</summary>
    public required Func<ClockCheck?, string> ClockLine { get; init; }
    public required string ClockSyncing { get; init; }
    public required string SyncClock { get; init; }

    public static readonly Strings En = new()
    {
        Language = "Language",
        Source = "Source",
        GameProfile = "Game profile",
        Fps = "FPS",
        Hotkey = "Start/stop hotkey",
        Refresh = "Refresh",
        GameTooltip = "Builds frames.csv + session.json after recording and validates them",
        HotkeyTooltip = "Click, then press the new key combination",
        PlayerId = "Player ID",
        PlayerIdTooltip = "Names your folder in the delivered session: 1-32 lowercase letters, digits, - or _ (e.g. p1, thanh)",
        PlayerName = "Name",
        PlayerNameTooltip = "Your name as written in player.json, so the client can tell the players apart",
        Room = "Room",
        RoomTooltip = "This room's invite code: send it to the other players",
        EnterInviteCode = "Invite code from the room's host (6 characters):",
        CancelDialog = "Cancel",
        JoinRoom = "Join room",
        CreateRoom = "Create room",
        LeaveRoom = "Leave room",
        CloseRoom = "Close room",
        AudioOption = "Record audio",
        AudioTooltip = "Also record the game's sound into the video (only the game, no voice chat or other apps, when the window or the game profile names its process; otherwise everything this PC plays)",
        Video = "Video",
        ObsOption = "Record with OBS",
        ObsTooltip = "OBS records the video with its own scene, output and audio settings (set it to constant frame rate, 1080p, 30 or 60 FPS); Odx Capture starts and stops it. OBS must be open, with Tools > WebSocket Server Settings > Enable WebSocket server",
        ObsAddressTooltip = "Address of OBS's WebSocket server (Tools > WebSocket Server Settings), usually 127.0.0.1:4455",
        ObsPasswordTooltip = "OBS's WebSocket server password (Tools > WebSocket Server Settings > Show Connect Info); empty if authentication is off",
        ObsTest = "Test",
        ObsConnection = "OBS WebSocket",
        ObsAddress = "Host",
        ObsPassword = "Password",
        ObsTestTooltip = "Check that OBS is open, its WebSocket server is on and the password is right, and show what it will record",
        ObsTesting = address => $"Connecting to OBS at {address}…",
        ObsConnected = c => $"OBS {c.Version} connected: records {c.Width}x{c.Height} at {c.Fps:0.##} FPS" +
            (c.BelowFloor ? ". Below the 1280x720, 30 FPS minimum: raise it in OBS > Settings > Video" :
             c.BelowTarget ? ". The target is 1920x1080 (OBS > Settings > Video > Output Resolution)" : "") +
            (c.Recording ? ". OBS is recording now: stop it before you start" : ""),
        ObsTestFailed = error => $"OBS not reachable: {error}. Open OBS and turn on Tools > WebSocket Server Settings > Enable WebSocket server",
        ObsNoAnswer = "no answer within 5 s",
        LanStatus = lan => lan.Status switch
        {
            "ok" => $"LAN: {lan.Address} (in-game, host or join through LAN)",
            "joining" => "LAN: connecting…",
            "zerotier_missing" => "LAN: ZeroTier is needed on this machine",
            _ => $"LAN: not connected ({lan.Address})",
        },
        LanInstallTitle = "Virtual LAN",
        LanInstallQuestion = "This room plays over a virtual LAN, which needs ZeroTier on this machine. Install it now? (Windows asks for permission once.)",
        LanInstalling = "Installing ZeroTier…",
        LanInstallFailed = "ZeroTier could not be installed or started; install it from zerotier.com, then leave and join the room again.",
        PacketsOn = "Network packets: recorded with each round",
        PacketsOff = "Network packets: not recorded (administrator permission was declined; restart the app to be asked again)",
        PrimaryMonitor = "Primary monitor",
        NotRunning = exe => $"{exe} (not running)",
        NoGameProfile = "None (keep raw capture only)",
        SkippedProfile = (file, error) => $"Skipped game profile {file}: {error}",
        Start = "Start recording",
        Stop = hotkey => $"Stop recording ({hotkey})",
        Cancel = hotkey => $"Cancel ({hotkey})",
        Stopping = "Stopping…",
        TrayShow = "Show",
        TrayExit = "Exit",
        TrayCancel = "Cancel",
        TrayStop = "Stop recording",
        Ready = "Ready",
        ReadyWithEncoder = encoder => encoder switch
        {
            "nvenc" => "Ready · video encoder: NVENC (GPU)",
            "mf" => "Ready · video encoder: Media Foundation (GPU)",
            _ => "Ready · video encoder: x264 (CPU; no GPU encoder available here)",
        },
        CountdownStatus = n => $"Recording starts in {n}…",
        Cancelled = "Cancelled",
        StartingCapture = "Starting capture…",
        RecordingStatus = (elapsed, c, dir) =>
            $"Recording {elapsed:hh\\:mm\\:ss}{(c is { } x ? $" · {x.Frames} frames, {x.Dropped} dropped" : "")}\n→ {dir}",
        StoppingStatus = "Stopping, then building and validating the session…",
        FinalizingStatus = percent => $"Finalizing the video ({percent} %)…",
        BuildingStatus = "Building and validating the session…",
        CouldNotStart = error => $"Could not start: {error}",
        HotkeyInUse = hotkey => $"Hotkey {hotkey} is already used by another application; choose another one.",
        PressKeys = "Press a key combination…",
        HostHint = hotkey => $"Host: once everyone is ready (✓), press {hotkey} to start the round; again to end it.",
        RoundKept = (session, dir) => $"✓ Round {session}: recorded, kept in {dir}",
        RoundCaptureFailed = (session, dir) => $"✗ Round {session}: capture failed (what was recorded stays in {dir})",
        PlayerIdentityRequired = "Fill in your Player ID (1-32 lowercase letters, digits, - or _) and your Name first.",
        JoiningRoom = "Joining the room…",
        RoomStatus = (code, host, members) => $"Room {code}{(host ? " · you are the host: share this invite code" : "")}\nPlayers: {members}",
        MemberState = state => state switch { "starting" => " · starting", "recording" => " · recording", "saving" => " · saving…", _ => "" },
        RoundVerdict = (session, failed, degraded) =>
            failed.Count == 0 && degraded.Count == 0 ? $"✓ Round {session}: all OK" :
            (failed.Count > 0 ? $"✗ Round {session}: RECORD AGAIN" : $"⚠ Round {session}: saved, check") +
            string.Concat(failed.Select(f => $"\n  {f}")) + string.Concat(degraded.Select(d => $"\n  {d}")),
        StartRound = hotkey => $"Start round ({hotkey})",
        WaitingReady = (ready, others) => $"Waiting for players to be ready ({ready}/{others})",
        WaitingSaving = saving => $"Waiting for {saving} player(s) to save the last round…",
        TaskId = "Task ID",
        TaskIdTooltip = "The round's name: every player's recording and the delivered session go into a folder of this name (letters, digits, _ . -; one per round)",
        RecordTimerTooltip = "Recording time: runs from the first recorded frame (after the 3-2-1) until the recording stops",
        TaskBatchMissing = "Type the batch code (e.g. AMRF-001) before starting the round",
        TaskBatchInvalid = "Batch code: letters, digits, _ . - only, starting with a letter or digit, not ending with a dot",
        TaskBatchFull = batch => $"Batch {batch} has its 15 videos: type the next batch code",
        TaskNotStarted = "Round not started",
        TaskRetake = (task, retake) => $"{task} was already used on the server: recording as {retake}. Its folder takes the name {task} on each PC where no folder has it.",
        TaskIdRequired = "Enter the task ID (the round's folder name: letters, digits, _ . -) before starting the round",
        EndRound = hotkey => $"End round ({hotkey})",
        ReadyButton = hotkey => $"I'm ready ({hotkey})",
        UnreadyButton = hotkey => $"Not ready ({hotkey})",
        InRound = "Round in progress…",
        LeftRoom = "Left the room",
        RoomClosed = "The host closed the room",
        ReadyHint = hotkey => $"Press {hotkey} when you are ready; the round starts when everyone is.",
        ReadyOn = hotkey => $"READY: waiting for the others ({hotkey} to cancel).",
        RoundToggleFailed = error => $"Could not start/end the round: {error}",
        TrayTooltip = state => $"Odx Capture: {state}",
        EndOverlay = "END",
        FinalizingOverlay = percent => $"Video {percent} %",
        BuildingOverlay = "Session…",
        NotifySavedPass = "Recording saved · validation PASS",
        NotifySavedRaw = "Recording saved (raw capture)",
        NotifyFailed = "Recording stopped with errors",
        Saved = dir => $"Saved: {dir}",
        StoppedWithErrors = dir => $"Stopped with errors: {dir}",
        FramesSummary = (frames, w, h, fps, duplicates, dropped) =>
            $"{frames} frames ({w}x{h} @ {fps} fps), {duplicates} duplicated, {dropped} dropped",
        InputEvents = n => $"{n} input events",
        NoProfileNoBuild = "No game profile selected: session not built (run odx build later).",
        BuildFailed = error => $"Could not build the session: {error}",
        ValidationPass = (errors, warnings) => $"Validation PASS: {errors} errors, {warnings} warnings",
        ValidationFail = (errors, warnings) => $"Validation FAIL: {errors} errors, {warnings} warnings",
        UnboundKeys = keys => $"Keys not bound in the game profile (left out): {keys}",
        ClockNtp = (server, rtt, offset) =>
            $"Clock: NTP {server}, RTT {rtt:F0} ms; Windows clock off by {offset / 1000:+0.000;-0.000} s",
        ClockSystem = "Clock: Windows clock only (NTP not measured — check time_server in odx-capture.json)",
        ConfigInvalid = error => $"Settings file not valid, the Windows clock is used: {error}",
        ClockWarning = offset => $"Warning: the Windows clock is {Math.Abs(offset) / 1000:F1} s off; fix Windows time sync.",
        CaptureLine = c => c.Ok
            ? $"Capture: {c.Source} {c.Width}x{c.Height}{(c.WindowStyle is { } s ? $" ({s})" : "")} ✓"
            : c.Frames == 0 ? $"Capture: {c.Source} gives no frames — is the game window open and not minimized?"
            : $"Capture: {c.Source} is BLACK — exclusive fullscreen cannot be recorded: set the game to borderless or windowed (or pick the monitor as source), then press Ready again",
        AudioLine = a => (a.Source switch
            {
                AudioStatus.FromProcess => "Audio: the game's own sound",
                AudioStatus.FromDevice => $"Audio: everything this PC plays ({a.Device})" +
                                      (a.FallbackReason is null ? "" : $" — the game's own sound was not available: {a.FallbackReason}"),
                _ => $"Audio: NOT recorded ({a.Error})",
            }) + (a.SilentRatio > 0.99 ? " — WARNING: the track is silent" : "") + (a.Discontinuities > 0 ? $" — {a.Discontinuities} gap(s)" : ""),
        ClockLine = c => (c is null ? "Clock: measuring against NTP…"
                : c.NtpOffsetMs is double o
                    ? $"Clock: Windows clock {o:+0;-0} ms from NTP {c.NtpServer}" +
                      (c.Ok ? " ✓" : $" — more than {ClockCheck.MaxOffsetMs} ms: press Sync clock before Ready")
                    : $"Clock: NTP {c.NtpServer} did not answer ({c.NtpError}); check the internet connection") +
            (c is { OrchestratorNtpOffsetMs: null } ? " · orchestrator: NTP not measured" : ""),
        ClockSyncing = "Clock: setting the Windows clock from NTP (allow the administrator prompt)…",
        SyncClock = "Sync clock",
    };

    public static readonly Strings Vi = new()
    {
        Language = "Ngôn ngữ",
        Source = "Nguồn quay",
        GameProfile = "Hồ sơ game",
        Fps = "FPS",
        Hotkey = "Phím tắt bắt đầu/dừng",
        Refresh = "Làm mới",
        GameTooltip = "Sau khi ghi, tạo frames.csv + session.json và kiểm tra hợp lệ",
        HotkeyTooltip = "Bấm vào đây rồi nhấn tổ hợp phím mới",
        PlayerId = "Mã người chơi",
        PlayerIdTooltip = "Tên thư mục của bạn trong session giao khách: 1-32 chữ thường, số, - hoặc _ (vd. p1, thanh)",
        PlayerName = "Tên",
        PlayerNameTooltip = "Tên của bạn ghi trong player.json, để khách phân biệt người chơi",
        Room = "Room",
        RoomTooltip = "Mã mời của room này: gửi cho những người chơi khác",
        EnterInviteCode = "Mã mời do host của room gửi (6 ký tự):",
        CancelDialog = "Hủy",
        JoinRoom = "Vào room",
        CreateRoom = "Tạo room",
        LeaveRoom = "Rời room",
        CloseRoom = "Đóng room",
        AudioOption = "Ghi âm thanh",
        AudioTooltip = "Ghi tiếng game vào video (chỉ tiếng game, không có voice chat hay app khác, khi biết process của game qua cửa sổ hoặc profile game; nếu không thì ghi mọi âm thanh máy này phát ra)",
        Video = "Video",
        ObsOption = "Ghi bằng OBS",
        ObsTooltip = "OBS ghi video theo scene, output và audio đã chỉnh trong OBS (đặt frame rate cố định, 1080p, 30 hoặc 60 FPS); Odx Capture tự bật và tắt ghi. OBS phải đang mở, có bật Tools > WebSocket Server Settings > Enable WebSocket server",
        ObsAddressTooltip = "Địa chỉ WebSocket server của OBS (Tools > WebSocket Server Settings), thường là 127.0.0.1:4455",
        ObsPasswordTooltip = "Mật khẩu WebSocket server của OBS (Tools > WebSocket Server Settings > Show Connect Info); để trống nếu tắt xác thực",
        ObsTest = "Kiểm tra",
        ObsConnection = "OBS WebSocket",
        ObsAddress = "Host",
        ObsPassword = "Mật khẩu",
        ObsTestTooltip = "Kiểm tra OBS đang mở, đã bật WebSocket server và đúng mật khẩu, và xem OBS sẽ ghi gì",
        ObsTesting = address => $"Đang kết nối OBS tại {address}…",
        ObsConnected = c => $"Đã kết nối OBS {c.Version}: ghi {c.Width}x{c.Height}, {c.Fps:0.##} FPS" +
            (c.BelowFloor ? ". Dưới mức tối thiểu 1280x720, 30 FPS: tăng lên trong OBS > Settings > Video" :
             c.BelowTarget ? ". Mục tiêu là 1920x1080 (OBS > Settings > Video > Output Resolution)" : "") +
            (c.Recording ? ". OBS đang ghi: hãy dừng trước khi bắt đầu" : ""),
        ObsTestFailed = error => $"Không kết nối được OBS: {error}. Mở OBS và bật Tools > WebSocket Server Settings > Enable WebSocket server",
        ObsNoAnswer = "không có phản hồi sau 5 giây",
        LanStatus = lan => lan.Status switch
        {
            "ok" => $"LAN: {lan.Address} (trong game tạo/vào trận qua LAN)",
            "joining" => "LAN: đang kết nối…",
            "zerotier_missing" => "LAN: máy này cần ZeroTier",
            _ => $"LAN: chưa kết nối được ({lan.Address})",
        },
        LanInstallTitle = "Mạng LAN ảo",
        LanInstallQuestion = "Room này chơi qua mạng LAN ảo, cần cài ZeroTier trên máy này. Cài ngay bây giờ? (Windows sẽ hỏi quyền một lần.)",
        LanInstalling = "Đang cài ZeroTier…",
        LanInstallFailed = "Không cài hoặc khởi động được ZeroTier; hãy tự cài từ zerotier.com rồi rời và vào lại room.",
        PacketsOn = "Gói mạng: ghi cùng mỗi round",
        PacketsOff = "Gói mạng: không ghi (đã từ chối quyền admin; mở lại app để được hỏi lại)",
        PrimaryMonitor = "Màn hình chính",
        NotRunning = exe => $"{exe} (không chạy)",
        NoGameProfile = "Không có (chỉ giữ dữ liệu thô)",
        SkippedProfile = (file, error) => $"Bỏ qua hồ sơ game {file}: {error}",
        Start = "Bắt đầu ghi",
        Stop = hotkey => $"Dừng ghi ({hotkey})",
        Cancel = hotkey => $"Hủy ({hotkey})",
        Stopping = "Đang dừng…",
        TrayShow = "Hiện cửa sổ",
        TrayExit = "Thoát",
        TrayCancel = "Hủy",
        TrayStop = "Dừng ghi",
        Ready = "Sẵn sàng",
        ReadyWithEncoder = encoder => encoder switch
        {
            "nvenc" => "Sẵn sàng · mã hóa video: NVENC (GPU)",
            "mf" => "Sẵn sàng · mã hóa video: Media Foundation (GPU)",
            _ => "Sẵn sàng · mã hóa video: x264 (CPU; máy này không có bộ mã hóa GPU)",
        },
        CountdownStatus = n => $"Bắt đầu ghi sau {n}…",
        Cancelled = "Đã hủy",
        StartingCapture = "Đang khởi động ghi…",
        RecordingStatus = (elapsed, c, dir) =>
            $"Đang ghi {elapsed:hh\\:mm\\:ss}{(c is { } x ? $" · {x.Frames} khung hình, bỏ {x.Dropped}" : "")}\n→ {dir}",
        StoppingStatus = "Đang dừng, sau đó tạo và kiểm tra session…",
        FinalizingStatus = percent => $"Đang hoàn thiện video ({percent} %)…",
        BuildingStatus = "Đang tạo và kiểm tra session…",
        CouldNotStart = error => $"Không thể bắt đầu: {error}",
        HotkeyInUse = hotkey => $"Phím tắt {hotkey} đang được ứng dụng khác dùng; hãy chọn phím khác.",
        PressKeys = "Nhấn tổ hợp phím…",
        HostHint = hotkey => $"Host: khi mọi người đã sẵn sàng (✓), nhấn {hotkey} để bắt đầu round; nhấn lại để kết thúc.",
        RoundKept = (session, dir) => $"✓ Round {session}: đã ghi, lưu tại {dir}",
        RoundCaptureFailed = (session, dir) => $"✗ Round {session}: ghi lỗi (phần đã ghi vẫn ở {dir})",
        PlayerIdentityRequired = "Hãy điền Mã người chơi (1-32 chữ thường, số, - hoặc _) và Tên trước.",
        JoiningRoom = "Đang vào room…",
        RoomStatus = (code, host, members) => $"Room {code}{(host ? " · bạn là host: gửi mã mời này cho mọi người" : "")}\nNgười chơi: {members}",
        MemberState = state => state switch { "starting" => " · đang khởi động", "recording" => " · đang ghi", "saving" => " · đang lưu…", _ => "" },
        RoundVerdict = (session, failed, degraded) =>
            failed.Count == 0 && degraded.Count == 0 ? $"✓ Round {session}: tất cả OK" :
            (failed.Count > 0 ? $"✗ Round {session}: QUAY LẠI" : $"⚠ Round {session}: đã lưu, cần xem") +
            string.Concat(failed.Select(f => $"\n  {f}")) + string.Concat(degraded.Select(d => $"\n  {d}")),
        StartRound = hotkey => $"Bắt đầu round ({hotkey})",
        WaitingReady = (ready, others) => $"Chờ mọi người sẵn sàng ({ready}/{others})",
        WaitingSaving = saving => $"Chờ {saving} người lưu xong round trước…",
        TaskId = "Task ID",
        TaskIdTooltip = "Tên round: bản ghi của mọi người chơi và session giao đều nằm trong thư mục mang tên này (chữ, số, _ . -; mỗi round một tên)",
        RecordTimerTooltip = "Thời gian quay: tính từ khung hình đầu tiên (sau 3-2-1) đến khi dừng quay",
        TaskBatchMissing = "Nhập mã lô (ví dụ AMRF-001) rồi mới bắt đầu round",
        TaskBatchInvalid = "Mã lô chỉ gồm chữ, số, _ . -, bắt đầu bằng chữ hoặc số, không kết thúc bằng dấu chấm",
        TaskBatchFull = batch => $"Lô {batch} đã đủ 15 video: nhập mã lô tiếp theo",
        TaskNotStarted = "Round chưa bắt đầu",
        TaskRetake = (task, retake) => $"{task} đã dùng trên server: quay với tên {retake}. Máy nào chưa có thư mục {task} thì thư mục sẽ tự đổi về {task}.",
        TaskIdRequired = "Nhập Task ID (tên thư mục của round: chữ, số, _ . -) rồi mới bắt đầu round",
        EndRound = hotkey => $"Kết thúc round ({hotkey})",
        ReadyButton = hotkey => $"Tôi sẵn sàng ({hotkey})",
        UnreadyButton = hotkey => $"Hủy sẵn sàng ({hotkey})",
        InRound = "Đang trong round…",
        LeftRoom = "Đã rời room",
        RoomClosed = "Host đã đóng room",
        ReadyHint = hotkey => $"Nhấn {hotkey} khi bạn sẵn sàng; round bắt đầu khi mọi người đều sẵn sàng.",
        ReadyOn = hotkey => $"ĐÃ SẴN SÀNG: đang chờ người khác (nhấn {hotkey} để hủy).",
        RoundToggleFailed = error => $"Không bắt đầu/kết thúc được round: {error}",
        TrayTooltip = state => $"Odx Capture: {state}",
        EndOverlay = "KẾT THÚC",
        FinalizingOverlay = percent => $"Video {percent} %",
        BuildingOverlay = "Session…",
        NotifySavedPass = "Đã lưu bản ghi · Kiểm tra ĐẠT",
        NotifySavedRaw = "Đã lưu bản ghi (dữ liệu thô)",
        NotifyFailed = "Bản ghi có lỗi",
        Saved = dir => $"Đã lưu: {dir}",
        StoppedWithErrors = dir => $"Đã dừng nhưng có lỗi: {dir}",
        FramesSummary = (frames, w, h, fps, duplicates, dropped) =>
            $"{frames} khung hình ({w}x{h} @ {fps} fps), lặp {duplicates}, bỏ {dropped}",
        InputEvents = n => $"{n} sự kiện bàn phím/chuột",
        NoProfileNoBuild = "Chưa chọn hồ sơ game: chưa tạo session (chạy odx build sau).",
        BuildFailed = error => $"Không tạo được session: {error}",
        ValidationPass = (errors, warnings) => $"Kiểm tra ĐẠT: {errors} lỗi, {warnings} cảnh báo",
        ValidationFail = (errors, warnings) => $"Kiểm tra KHÔNG ĐẠT: {errors} lỗi, {warnings} cảnh báo",
        UnboundKeys = keys => $"Phím chưa gán trong hồ sơ game (đã bỏ qua): {keys}",
        ClockNtp = (server, rtt, offset) =>
            $"Đồng hồ: NTP {server}, RTT {rtt:F0} ms; đồng hồ Windows lệch {offset / 1000:+0.000;-0.000} giây",
        ClockSystem = "Đồng hồ: chỉ dùng đồng hồ Windows (chưa đo được NTP — kiểm tra time_server trong odx-capture.json)",
        ConfigInvalid = error => $"File cấu hình không hợp lệ, dùng đồng hồ Windows: {error}",
        ClockWarning = offset => $"Cảnh báo: đồng hồ Windows lệch {Math.Abs(offset) / 1000:F1} giây; hãy sửa đồng bộ giờ của Windows.",
        CaptureLine = c => c.Ok
            ? $"Ghi hình: {c.Source} {c.Width}x{c.Height}{(c.WindowStyle is { } s ? $" ({s})" : "")} ✓"
            : c.Frames == 0 ? $"Ghi hình: {c.Source} không có frame — cửa sổ game đã mở và không bị thu nhỏ chứ?"
            : $"Ghi hình: {c.Source} TOÀN ĐEN — fullscreen exclusive không ghi được: chuyển game sang borderless hoặc windowed (hoặc chọn nguồn là màn hình), rồi bấm Sẵn sàng lại",
        AudioLine = a => (a.Source switch
            {
                AudioStatus.FromProcess => "Âm thanh: tiếng riêng của game",
                AudioStatus.FromDevice => $"Âm thanh: mọi thứ máy đang phát ({a.Device})" +
                                      (a.FallbackReason is null ? "" : $" — không lấy được tiếng riêng của game: {a.FallbackReason}"),
                _ => $"Âm thanh: KHÔNG ghi được ({a.Error})",
            }) + (a.SilentRatio > 0.99 ? " — CẢNH BÁO: track toàn im lặng" : "") + (a.Discontinuities > 0 ? $" — {a.Discontinuities} chỗ đứt" : ""),
        ClockLine = c => (c is null ? "Đồng hồ: đang đo so với NTP…"
                : c.NtpOffsetMs is double o
                    ? $"Đồng hồ: đồng hồ Windows lệch {o:+0;-0} ms so với NTP {c.NtpServer}" +
                      (c.Ok ? " ✓" : $" — quá {ClockCheck.MaxOffsetMs} ms: bấm Đồng bộ giờ trước khi Sẵn sàng")
                    : $"Đồng hồ: NTP {c.NtpServer} không trả lời ({c.NtpError}); kiểm tra kết nối Internet") +
            (c is { OrchestratorNtpOffsetMs: null } ? " · orchestrator: chưa đo NTP" : ""),
        ClockSyncing = "Đồng hồ: đang đặt đồng hồ Windows theo NTP (hãy cho phép ở hộp thoại quản trị)…",
        SyncClock = "Đồng bộ giờ",
    };

    /// <summary>Title and text of the Windows notification shown when a recording is finished.</summary>
    /// <summary>The processing badge's text after the round; null once the run is done (the badge closes).</summary>
    public string? PhaseOverlay(CapturePhase phase, int percent) => phase switch
    {
        CapturePhase.Finalizing => FinalizingOverlay(percent),
        CapturePhase.Building => BuildingOverlay,
        CapturePhase.Done => null,
        _ => EndOverlay,
    };

    /// <summary>The status line while a recording is stopped, finalized and built.</summary>
    public string PhaseStatus(CapturePhase phase, int percent) => phase switch
    {
        CapturePhase.Finalizing => FinalizingStatus(percent),
        CapturePhase.Building => BuildingStatus,
        _ => StoppingStatus,
    };

    public (string Title, string Body) Notification(RunResult r) =>
        (!r.Ok ? NotifyFailed : r.Build is null ? NotifySavedRaw : NotifySavedPass, r.SessionDir);

    /// <summary>Multi-line result of one recording in this language.</summary>
    public string Describe(RunResult r)
    {
        var lines = new List<string> { r.Ok ? Saved(r.SessionDir) : StoppedWithErrors(r.SessionDir) };
        if (r.Recording is { } s) lines.Add(FramesSummary(s.Frames, s.Width, s.Height, s.Fps, s.Duplicates, s.Dropped));
        if (r.Recording?.Audio is { } audio) lines.Add(AudioLine(audio));
        lines.Add(InputEvents(r.InputEvents));
        lines.AddRange(r.ToolErrors);
        if (r.CaptureOk && r.Build is null) lines.Add(NoProfileNoBuild);
        if (r.Build is { Error: { } error }) lines.Add(BuildFailed(error));
        if (r.Build is { Report: { } report } build)
        {
            int errors = build.Issues.Count(i => i.Severity == Issue.ErrorSeverity), warnings = build.Issues.Count - errors;
            lines.Add(errors == 0 ? ValidationPass(errors, warnings) : ValidationFail(errors, warnings));
            lines.AddRange(build.Issues.Take(5).Select(i => $"  [{i.Severity}] {i.Rule}: {i.Message}"));
            if (report.UnboundKeys.Count > 0) lines.Add(UnboundKeys(string.Join(", ", report.UnboundKeys.Keys)));
            var c = report.Clock;
            if (c is { Source: "ntp", RttMs: double rtt, SystemOffsetMs: double offset })
            {
                lines.Add(ClockNtp(c.Server!, rtt, offset));
                if (Math.Abs(offset) > ClockWarningMs) lines.Add(ClockWarning(offset));
            }
            else
            {
                lines.Add(ClockSystem);
            }
        }
        return string.Join("\n", lines);
    }

    /// <summary>Beyond this the Windows clock is badly synced; NTP still fixes the session times, but other tools use it.</summary>
    public const double ClockWarningMs = 1000;
}
