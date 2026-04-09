using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ClickyWindows.Helpers;
using ClickyWindows.Models;

namespace ClickyWindows;

public partial class OverlayWindow : Window
{
    private readonly DispatcherTimer _cursorTimer;
    private readonly DispatcherTimer _pointerHideTimer;
    private AppState _appState = AppState.Idle;

    // Offset so cursor follower center sits on the actual cursor, not its top-left
    private const double CursorFollowerOffset = 16;

    public OverlayWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;

        _cursorTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16), // ~60fps
        };
        _cursorTimer.Tick += OnCursorTick;

        // Auto-hides the target pointer after 12 seconds
        _pointerHideTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(12),
        };
        _pointerHideTimer.Tick += (_, _) =>
        {
            _pointerHideTimer.Stop();
            HideTargetPointer();
        };
    }

    // ── Win32 overlay setup ─────────────────────────────────────────────────

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;

        // Add WS_EX_LAYERED and WS_EX_TRANSPARENT so mouse events pass through
        int exStyle = Win32.GetWindowLong(hwnd, Win32.GWL_EXSTYLE);
        exStyle |= Win32.WS_EX_LAYERED | Win32.WS_EX_TRANSPARENT | Win32.WS_EX_NOACTIVATE;
        Win32.SetWindowLong(hwnd, Win32.GWL_EXSTYLE, exStyle);

        // Force always-on-top at the highest non-exclusive z-order
        Win32.SetWindowPos(
            hwnd,
            Win32.HWND_TOPMOST,
            0, 0, 0, 0,
            Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);

        // Expand overlay to cover the entire primary screen
        var screen = System.Windows.Forms.Screen.PrimaryScreen!;
        var dpiScale = CoordinateHelper.GetDpiScale();
        Left = screen.Bounds.Left / dpiScale;
        Top = screen.Bounds.Top / dpiScale;
        Width = screen.Bounds.Width / dpiScale;
        Height = screen.Bounds.Height / dpiScale;

        _cursorTimer.Start();
    }

    // ── Cursor follower (60fps) ─────────────────────────────────────────────

    private void OnCursorTick(object? sender, EventArgs e)
    {
        Win32.GetCursorPos(out var pt);
        var dpiScale = CoordinateHelper.GetDpiScale();

        // Convert physical pixel cursor pos to WPF coordinates relative to this window
        double wpfX = pt.X / dpiScale - Left - CursorFollowerOffset;
        double wpfY = pt.Y / dpiScale - Top - CursorFollowerOffset;

        System.Windows.Controls.Canvas.SetLeft(CursorFollower, wpfX);
        System.Windows.Controls.Canvas.SetTop(CursorFollower, wpfY);

        // Keep listening indicator near cursor
        if (ListeningIndicator.Visibility == Visibility.Visible)
        {
            System.Windows.Controls.Canvas.SetLeft(ListeningIndicator, wpfX + 28);
            System.Windows.Controls.Canvas.SetTop(ListeningIndicator, wpfY - 10);
        }
    }

    // ── Public API called by CompanionManager ───────────────────────────────

    public void SetState(AppState state)
    {
        _appState = state;
        Dispatcher.Invoke(() =>
        {
            ListeningIndicator.Visibility = state == AppState.Listening
                ? Visibility.Visible : Visibility.Collapsed;

            if (state == AppState.Idle)
                HideTargetPointer();
        });
    }

    /// <summary>
    /// Animates the target pointer to physical screen coordinates returned by Claude.
    /// </summary>
    public void ShowTargetAt(double physX, double physY, string label)
    {
        Dispatcher.Invoke(() =>
        {
            var dpiScale = CoordinateHelper.GetDpiScale();
            double wpfX = physX / dpiScale - Left;
            double wpfY = physY / dpiScale - Top;

            // Center the 48×48 pointer on the target
            System.Windows.Controls.Canvas.SetLeft(TargetPointer, wpfX - 24);
            System.Windows.Controls.Canvas.SetTop(TargetPointer, wpfY - 24);

            // Position label to the right (with edge clamping TODO)
            System.Windows.Controls.Canvas.SetLeft(TargetLabel, wpfX + 30);
            System.Windows.Controls.Canvas.SetTop(TargetLabel, wpfY - 12);

            TargetPointer.Visibility = Visibility.Visible;

            if (!string.IsNullOrWhiteSpace(label))
            {
                TargetLabelText.Text = label;
                TargetLabel.Visibility = Visibility.Visible;
            }

            // Animate pulse ring
            AnimatePulse();

            // Reset the auto-hide timer
            _pointerHideTimer.Stop();
            _pointerHideTimer.Start();
        });
    }

    public void HideTargetPointer()
    {
        Dispatcher.Invoke(() =>
        {
            TargetPointer.Visibility = Visibility.Collapsed;
            TargetLabel.Visibility = Visibility.Collapsed;
        });
    }

    private void AnimatePulse()
    {
        var scaleAnim = new DoubleAnimation(0.5, 2.0, TimeSpan.FromSeconds(1.2))
        {
            RepeatBehavior = new RepeatBehavior(3),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        var opacityAnim = new DoubleAnimation(1.0, 0.0, TimeSpan.FromSeconds(1.2))
        {
            RepeatBehavior = new RepeatBehavior(3),
        };

        PulseScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, scaleAnim);
        PulseScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, scaleAnim);
        PulseRing.BeginAnimation(UIElement.OpacityProperty, opacityAnim);
    }
}
