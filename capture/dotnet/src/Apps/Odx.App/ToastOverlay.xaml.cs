using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace Odx.App;

/// <summary>
/// A notice in the top right corner of the main screen that closes by itself (<see cref="Overlay"/>), in place of tray
/// notifications, which Windows hides while a game runs full screen.
/// </summary>
public partial class ToastOverlay : Window
{
    private static ToastOverlay? _shown;

    private ToastOverlay() => InitializeComponent();

    /// <summary>Replaces the notice on screen, if any.</summary>
    public static void Notify(bool ok, string title, string? body = null)
    {
        _shown?.Close();
        var toast = new ToastOverlay();
        toast.TitleText.Text = title;
        toast.BodyText.Text = body ?? "";
        toast.BodyText.Visibility = body is null ? Visibility.Collapsed : Visibility.Visible;
        toast.Accent.Background = new SolidColorBrush(ok ? Color.FromRgb(0x5D, 0xBB, 0x7E) : Color.FromRgb(0xE0, 0x4B, 0x4B));
        toast.Loaded += (_, _) =>
        {
            var area = SystemParameters.WorkArea;
            toast.Left = area.Right - toast.ActualWidth - 24;
            toast.Top = area.Top + 24;
        };
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(ok ? 6 : 10) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            toast.Close();
        };
        toast.Closed += (_, _) =>
        {
            timer.Stop();
            if (_shown == toast) _shown = null;
        };
        _shown = toast;
        toast.Show();
        timer.Start();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Overlay.Apply(this);
    }
}
