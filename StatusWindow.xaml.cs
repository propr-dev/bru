using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using ClickyWindows.Models;
using ClickyWindows.Services;
using Color = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace ClickyWindows;

/// <summary>
/// The Bru panel — a slim companion docked to the top-right of the screen.
/// A miniature living core in the header carries the state (breathe / sonar /
/// orbit / pulse); below it, an editorial transcript of the latest exchange and
/// the Allow/Deny permission strip. One click collapses the whole thing into a
/// small breathing square so it stays glanceable without eating screen space.
/// Close hides to tray; the app never quits from here.
/// </summary>
public partial class StatusWindow : Window
{
    private readonly CompanionManager _companion;
    private bool _expanded = true;

    private const double ExpandedW = 332, ExpandedH = 472;
    private const double CollapsedW = 64, CollapsedH = 64;
    private const double EdgeMargin = 12;

    private static readonly Color Cyan  = Color.FromRgb(0x00, 0xE5, 0xFF);
    private static readonly Color Amber = Color.FromRgb(0xFF, 0xB0, 0x20);
    private static readonly Color Rose  = Color.FromRgb(0xFF, 0x6E, 0x9C);

    public StatusWindow(CompanionManager companion)
    {
        InitializeComponent();
        _companion = companion;

        // Dock to the top-right of the working area (respects the taskbar).
        var wa = SystemParameters.WorkArea;
        Left = wa.Right - ExpandedW - EdgeMargin;
        Top  = wa.Top + EdgeMargin;

        // Windows can quietly drop Topmost when other apps open/maximise —
        // re-pin whenever we lose focus so the panel never disappears behind them.
        Deactivated += (_, _) => { Topmost = false; Topmost = true; };

        TitleBar.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        CollapseButton.Click += (_, _) => SetExpanded(false);
        CollapsedRoot.MouseLeftButtonUp += (_, _) => SetExpanded(true);
        HideButton.Click += (_, _) => Hide();

        AllowButton.Click += (_, _) => _companion.Files.ConfirmPending(true);
        DenyButton.Click  += (_, _) => _companion.Files.ConfirmPending(false);
        StopButton.Click  += (_, _) => _companion.StopSpeaking();

        _companion.StateChanged       += s => Dispatcher.Invoke(() => SetState(s));
        _companion.UserTranscript     += t => Dispatcher.Invoke(() => UserText.Text = t);
        _companion.AssistantReply     += r => Dispatcher.Invoke(() => BruText.Text = r);
        _companion.Files.CopyStaged   += d => Dispatcher.Invoke(() => ShowPermission(d));
        _companion.Files.CopyResolved += m => Dispatcher.Invoke(() => ClearPermission(m));

        SetState(AppState.Idle);
    }

    // ── collapse / expand ────────────────────────────────────────────────────

    private void SetExpanded(bool expanded)
    {
        if (_expanded == expanded) return;
        _expanded = expanded;

        // Keep the RIGHT edge anchored where the user has the window.
        double rightEdge = Left + Width;
        double w = expanded ? ExpandedW : CollapsedW;
        double h = expanded ? ExpandedH : CollapsedH;

        if (expanded)
        {
            CollapsedRoot.Visibility = Visibility.Collapsed;
            ExpandedRoot.Visibility  = Visibility.Visible;
            ExpandedRoot.Opacity     = 0;
        }
        else
        {
            ExpandedRoot.Visibility  = Visibility.Collapsed;
            CollapsedRoot.Visibility = Visibility.Visible;
            CollapsedRoot.Opacity    = 0;
        }

        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        var dur  = TimeSpan.FromMilliseconds(240);

        BeginAnimation(WidthProperty,  new DoubleAnimation(w, dur) { EasingFunction = ease });
        BeginAnimation(HeightProperty, new DoubleAnimation(h, dur) { EasingFunction = ease });
        BeginAnimation(LeftProperty,   new DoubleAnimation(rightEdge - w, dur) { EasingFunction = ease });

        var target = expanded ? (UIElement)ExpandedRoot : CollapsedRoot;
        target.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { BeginTime = TimeSpan.FromMilliseconds(140) });
    }

    // ── state → words, colour, core motion ──────────────────────────────────

    private void SetState(AppState state)
    {
        (string word, string hint, Color c) = state switch
        {
            AppState.Listening  => ("listening", "i'm listening — go ahead", Cyan),
            AppState.Processing => ("thinking",  "reading your screen…", Amber),
            AppState.Speaking   => ("speaking",  "talking to you now", Rose),
            _                   => ("idle",      "hold ctrl + space and talk to me", Cyan),
        };

        StateWord.Text = word;
        StateWord.Foreground = new SolidColorBrush(c);
        StatusLine.Text = hint;
        StopButton.Visibility = state == AppState.Speaking ? Visibility.Visible : Visibility.Collapsed;

        var coreBrush = new SolidColorBrush(c);
        Core.Fill = coreBrush;
        MiniCore.Fill = coreBrush;

        switch (state)
        {
            case AppState.Listening:  Breathe(1.25, 1.2); SonarOn();  ArcOff(); break;
            case AppState.Processing: Breathe(1.10, 0.8); SonarOff(); ArcOn();  break;
            case AppState.Speaking:   Breathe(1.30, 0.5); SonarOff(); ArcOff(); break;
            default:                  Breathe(1.16, 2.8); SonarOff(); ArcOff(); break;
        }
    }

    // ── living-core motion (drives BOTH the header core and the mini square) ─

    private void Breathe(double max, double seconds)
    {
        var a = new DoubleAnimation(1.0, max, TimeSpan.FromSeconds(seconds))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        CoreScale.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        CoreScale.BeginAnimation(ScaleTransform.ScaleYProperty, a);
        MiniCoreScale.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        MiniCoreScale.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }

    private void SonarOn()
    {
        var scale = new DoubleAnimation(0.5, 1.05, TimeSpan.FromSeconds(1.4)) { RepeatBehavior = RepeatBehavior.Forever };
        var fade  = new DoubleAnimation(0.55, 0.0, TimeSpan.FromSeconds(1.4)) { RepeatBehavior = RepeatBehavior.Forever };
        SonarScale.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
        SonarScale.BeginAnimation(ScaleTransform.ScaleYProperty, scale);
        Sonar.BeginAnimation(OpacityProperty, fade);
        MiniSonarScale.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
        MiniSonarScale.BeginAnimation(ScaleTransform.ScaleYProperty, scale);
        MiniSonar.BeginAnimation(OpacityProperty, fade);
    }

    private void SonarOff()
    {
        foreach (var (s, e) in new[] { (SonarScale, Sonar), (MiniSonarScale, MiniSonar) })
        {
            s.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            s.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            e.BeginAnimation(OpacityProperty, null);
            e.Opacity = 0;
        }
    }

    private void ArcOn()
    {
        Arc.BeginAnimation(OpacityProperty, null);
        Arc.Opacity = 1;
        ArcRotate.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.1)) { RepeatBehavior = RepeatBehavior.Forever });
    }

    private void ArcOff()
    {
        ArcRotate.BeginAnimation(RotateTransform.AngleProperty, null);
        Arc.BeginAnimation(OpacityProperty, null);
        Arc.Opacity = 0;
    }

    // ── permission flow ──────────────────────────────────────────────────────

    private void ShowPermission(string description)
    {
        PermissionText.Text          = description;
        PermissionCard.Visibility    = Visibility.Visible;
        NoPermissionPanel.Visibility = Visibility.Collapsed;

        // Approvals need eyes: surface the panel wherever it's hiding.
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        SetExpanded(true);
        Activate();
    }

    private void ClearPermission(string resultMessage)
    {
        PermissionCard.Visibility    = Visibility.Collapsed;
        NoPermissionPanel.Visibility = Visibility.Visible;
        NoPermissionText.Text        = string.IsNullOrWhiteSpace(resultMessage)
            ? "no actions waiting for approval"
            : resultMessage;
    }
}
