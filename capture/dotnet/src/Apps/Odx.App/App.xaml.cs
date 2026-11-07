using System.Windows;

namespace Odx.App;

/// <summary>
/// Single instance: a second start (e.g. the launcher clicked again while the app sits in the tray) only brings the
/// running window back, so two apps never record at the same time. --headless runs <see cref="HeadlessAgent"/> instead, as
/// many as wanted (several players on one test machine).
/// </summary>
public partial class App : Application
{
    private const string InstanceName = @"Local\OdxCapture.odx-app", ShowEventName = @"Local\OdxCapture.odx-app.show";
    private Mutex? _instance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (HeadlessAgent.Wanted(e.Args))
        {
            Shutdown(HeadlessAgent.Run(e.Args));
            return;
        }
        _instance = new Mutex(true, InstanceName, out bool first);
        if (!first)
        {
            if (EventWaitHandle.TryOpenExisting(ShowEventName, out var show)) show.Set();
            Shutdown();
            return;
        }
        // An error here used to end the app silently after the splash (WinExe, no console): it is logged and shown.
        DispatcherUnhandledException += (_, args) => Report("unhandled error", args.Exception);
        MainWindow window;
        try
        {
            window = new MainWindow();
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            Report("startup failed", ex);
            Shutdown(1);
            return;
        }
        var showRequests = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        new Thread(() =>
        {
            while (showRequests.WaitOne()) Dispatcher.Invoke(window.RestoreFromTray);
        }) { IsBackground = true }.Start();
    }

    /// <summary>Into the app's daily log (data\logs), and on screen.</summary>
    private static void Report(string what, Exception ex)
    {
        try
        {
            new Odx.Core.DailyLogFile(Odx.Core.PackageFolders.Logs).Write($"{what}: {ex}");
        }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException)
        {
            // The dialog below still shows the error.
        }
        MessageBox.Show($"Odx Capture: {what}\n\n{ex.Message}\n\nDetails: {Odx.Core.PackageFolders.Logs}",
            "Odx Capture - error", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
