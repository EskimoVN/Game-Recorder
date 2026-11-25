using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Odx.App;

/// <summary>What a countdown leads to: the recording starting (green) or stopping by itself (red).</summary>
public enum CountdownTone { Start, Stop }

/// <summary>The countdown, "END" and the processing phases after it in the middle of the screen (<see cref="Overlay"/>).</summary>
public partial class CountdownOverlay : Window
{
    private const double ProgressWidth = 220;
    /// <summary>The ring's Grid in CountdownOverlay.xaml.</summary>
    private const double RingSize = 150;

    private static readonly Brush StopGlass = Frozen(Color.FromArgb(0xE6, 0x2A, 0x0E, 0x12));
    private static readonly Brush StopLabel = Frozen(Color.FromRgb(0xFF, 0x8A, 0x86));
    private static readonly FontFamily MonoFont = new("Consolas");

    public CountdownOverlay()
    {
        InitializeComponent();
        // SizeToContent changes the size with the content (countdown, END, saving): keep it centred.
        SizeChanged += (_, _) =>
        {
            var area = SystemParameters.WorkArea;
            Left = area.Left + (area.Width - ActualWidth) / 2;
            Top = area.Top + (area.Height - ActualHeight) / 2;
        };
    }

    /// <summary>A countdown: <paramref name="label"/> over the seconds left, the ring full at <paramref name="total"/>.</summary>
    public void ShowCount(CountdownTone tone, string label, int seconds, int total)
    {
        CountPanel.Visibility = Visibility.Visible;
        BadgePanel.Visibility = Visibility.Collapsed;
        bool start = tone == CountdownTone.Start;
        var color = (Brush)FindResource(start ? "MatchaSoftBrush" : "RecordBrush");
        Frame.BorderBrush = color;
        Frame.Background = start ? (Brush)FindResource("OverlayGlassBrush") : StopGlass;
        CountLabel.Foreground = start ? color : StopLabel;
        CountLabel.Text = label;
        Number.Text = seconds.ToString(CultureInfo.InvariantCulture);
        Ring.Stroke = color;
        SetRing(total <= 0 ? 0 : Math.Clamp((double)seconds / total, 0, 1));
    }

    /// <summary>"END" with a red dot, and what was recorded under it.</summary>
    public void ShowEnd(string title, string detail) => ShowBadge("", title, detail, percent: null, end: true);

    /// <summary>A step after the stop: its label over the percent done, or "…" while it has none.</summary>
    public void ShowProgress(string label, int? percent) =>
        ShowBadge(label, percent is { } p ? $"{p} %" : "…", "", percent, end: false);

    private void ShowBadge(string label, string title, string detail, int? percent, bool end)
    {
        CountPanel.Visibility = Visibility.Collapsed;
        BadgePanel.Visibility = Visibility.Visible;
        Frame.BorderBrush = (Brush)FindResource(end ? "RecordBrush" : "HairlineBrush");
        Frame.Background = (Brush)FindResource("OverlayGlassBrush");
        BadgeLabel.Text = label;
        BadgeLabelBox.Visibility = label.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        EndDot.Visibility = end ? Visibility.Visible : Visibility.Collapsed;
        BadgeTitle.Text = title;
        BadgeTitle.FontSize = end ? 52 : 40;
        BadgeTitle.FontFamily = end ? FontFamily : MonoFont;
        BadgeDetail.Text = detail;
        BadgeDetailBox.Visibility = detail.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        Progress.Visibility = end ? Visibility.Collapsed : Visibility.Visible;
        // No percent (building the session): a dim full bar, the step has no measure.
        ProgressFill.Width = ProgressWidth * (percent is { } p ? Math.Clamp(p, 0, 100) / 100.0 : 1);
        ProgressFill.Opacity = percent is null ? 0.35 : 1;
    }

    /// <summary>The ring's arc as a dash: WPF dash lengths count in stroke thicknesses, along the stroke's centre line.</summary>
    private void SetRing(double fraction)
    {
        Ring.Visibility = fraction <= 0 ? Visibility.Hidden : Visibility.Visible;
        double units = Math.PI * (RingSize - Ring.StrokeThickness) / Ring.StrokeThickness;
        Ring.StrokeDashArray = new DoubleCollection { units * fraction, units };
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Overlay.Apply(this);
    }
}
