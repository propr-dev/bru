using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using WpfColor = System.Windows.Media.Color;
using WpfRect = System.Windows.Shapes.Rectangle;
using System.Windows.Threading;
using ClickyWindows.Helpers;
using ClickyWindows.Models;

namespace ClickyWindows;

public partial class OverlayWindow : Window
{
    // ── Dot state machine ───────────────────────────────────────────────────
    private enum DotState { FollowingCursor, FlyingToTarget, PointingAtTarget }
    private DotState _dotState = DotState.FollowingCursor;
    private AppState _appState = AppState.Idle;

    // ── Spring physics (cursor trailing) ────────────────────────────────────
    // Dot trails the cursor with a spring: responsive but with a pleasant lag.
    // Constants match original Clicky: response ~0.2 s, slight underdamp for a subtle bounce.
    private double _dotX, _dotY;    // Current dot center in WPF coords relative to this window
    private double _dotVX, _dotVY;  // Velocity
    private bool   _initialized;

    // +35 px right, 25 px above the actual cursor tip (matching macOS Clicky offsets)
    private const double CursorOffsetX =  35.0;
    private const double CursorOffsetY = -25.0;

    // Spring: tuned for a smooth, lightly-trailing follow (ζ≈0.85 — silky, barely any bounce).
    private const double SpringK    = 230.0;
    private const double SpringDamp =  26.0;

    // ── Flight animation (buddy flies a quadratic bezier arc to the target) ──
    private double _flightStartX, _flightStartY;
    private double _flightTargetX, _flightTargetY;
    private double _flightControlX, _flightControlY; // bezier control point (arc apex)
    private double _flightElapsed, _flightDuration;
    private string _flightLabel = "";
    private bool   _isReturningToCursor;              // true on the flight back
    private double _returnStartCursorX, _returnStartCursorY; // cancel-if-moved reference

    // ── Speech bubble streaming (chars appear one by one, like speech) ───────
    private string _bubbleFullText = "";
    private int    _bubbleShownChars;
    private double _nextCharAtSeconds;   // render-clock time for the next character
    private double _bubbleHideAtSeconds; // render-clock time to fade + fly back
    private bool   _welcomeActive;       // "hey! i'm bru" follows the buddy while shown
    private bool   _welcomePlayed;
    private double _firstFrameSeconds = -1;
    private double _nextTopmostCheck;    // re-assert HWND_TOPMOST twice a second
    private IntPtr _hwnd;

    // ── Audio waveform (Listening state) ────────────────────────────────────
    // Bar relative heights from original Clicky: center bar tallest.
    private static readonly double[] BarProfiles = [0.4, 0.7, 1.0, 0.7, 0.4];
    private WpfRect[] _waveformBars = null!;
    private float  _audioLevel;
    private double _wavePhase;

    // ── Utterance phrases (same pool as original Clicky) ───────────────────
    private static readonly string[] Utterances =
    [
        "right here!", "this one!", "over here!", "click this!", "here it is!", "found it!"
    ];
    private static readonly Random Rng = new();

    // ── Frame-synced render loop ────────────────────────────────────────────
    // CompositionTarget.Rendering fires once per composition frame (60/120/144 Hz),
    // perfectly aligned with the compositor — far smoother than a DispatcherTimer,
    // which fires at best-effort intervals and visibly micro-stutters.
    private double _lastRenderSeconds = -1;

    public OverlayWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;

        _waveformBars = [Bar0, Bar1, Bar2, Bar3, Bar4];
    }

    // ── Win32 overlay setup ─────────────────────────────────────────────────

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        var hwnd = _hwnd;

        int exStyle = Win32.GetWindowLong(hwnd, Win32.GWL_EXSTYLE);
        exStyle |= Win32.WS_EX_LAYERED | Win32.WS_EX_TRANSPARENT | Win32.WS_EX_NOACTIVATE;
        Win32.SetWindowLong(hwnd, Win32.GWL_EXSTYLE, exStyle);

        Win32.SetWindowPos(
            hwnd,
            Win32.HWND_TOPMOST,
            0, 0, 0, 0,
            Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);

        var screen = System.Windows.Forms.Screen.PrimaryScreen!;
        var dpiScale = CoordinateHelper.GetDpiScale();
        Left   = screen.Bounds.Left   / dpiScale;
        Top    = screen.Bounds.Top    / dpiScale;
        Width  = screen.Bounds.Width  / dpiScale;
        Height = screen.Bounds.Height / dpiScale;

        CompositionTarget.Rendering += OnRendering;

        // Gentle 2-second fade-in of the buddy on launch (original Clicky charm).
        CursorFollower.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromSeconds(2.0)));
    }

    // ── Per-frame update: spring physics + flight animation + waveform bars ──

    private void OnRendering(object? sender, EventArgs e)
    {
        // Real elapsed time from the compositor clock → frame-rate-independent motion.
        double now = (e as RenderingEventArgs)?.RenderingTime.TotalSeconds ?? 0;
        double dt = _lastRenderSeconds < 0 ? 0.016 : now - _lastRenderSeconds;
        _lastRenderSeconds = now;
        if (dt <= 0) return;             // duplicate frame — skip
        if (dt > 0.05) dt = 0.05;        // clamp after a stall so the spring can't explode

        Win32.GetCursorPos(out var pt);
        var dpiScale = CoordinateHelper.GetDpiScale();

        // Target position for the dot: cursor + trailing offset
        double cursorWpfX = pt.X / dpiScale - Left;
        double cursorWpfY = pt.Y / dpiScale - Top;
        double targetX = cursorWpfX + CursorOffsetX;
        double targetY = cursorWpfY + CursorOffsetY;

        // Snap to cursor on very first frame so the dot doesn't fly in from (0,0)
        if (!_initialized)
        {
            _dotX = targetX;
            _dotY = targetY;
            _initialized = true;
            _firstFrameSeconds = now;
        }

        // Windows lets newly opened/maximised apps (Excel, browsers, games) push
        // past a topmost window, and the flag can silently drop — the buddy would
        // vanish behind them. Re-assert HWND_TOPMOST twice a second so Bru is
        // ALWAYS in front, exactly like every serious screen-overlay tool does.
        if (now >= _nextTopmostCheck && _hwnd != IntPtr.Zero)
        {
            _nextTopmostCheck = now + 0.5;
            Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, 0, 0, 0, 0,
                Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
        }

        // First-launch charm (ported from the original): "hey! i'm bru" streams
        // into a bubble beside the buddy shortly after startup.
        if (!_welcomePlayed && _appState == AppState.Idle && now - _firstFrameSeconds > 0.9)
        {
            _welcomePlayed = true;
            _welcomeActive = true;
            StartBubble("hey! i'm bru", holdSeconds: 2.0, now);
        }

        // ── Advance dot based on state ──────────────────────────────────────
        switch (_dotState)
        {
            case DotState.FollowingCursor:
                // Sub-step the spring at a fixed 240 Hz so integration stays smooth and
                // identical whether the display runs at 60, 120 or 144 Hz.
                double remaining = dt;
                const double h = 1.0 / 240.0;
                while (remaining > 1e-6)
                {
                    double step = Math.Min(h, remaining);
                    _dotVX += ((targetX - _dotX) * SpringK - _dotVX * SpringDamp) * step;
                    _dotVY += ((targetY - _dotY) * SpringK - _dotVY * SpringDamp) * step;
                    _dotX  += _dotVX * step;
                    _dotY  += _dotVY * step;
                    remaining -= step;
                }
                break;

            case DotState.FlyingToTarget:
                // On the return flight, moving the mouse >100px cancels the animation
                // and snaps straight back to cursor following (ported from the original).
                if (_isReturningToCursor)
                {
                    double moved = Math.Sqrt(
                        (cursorWpfX - _returnStartCursorX) * (cursorWpfX - _returnStartCursorX) +
                        (cursorWpfY - _returnStartCursorY) * (cursorWpfY - _returnStartCursorY));
                    if (moved > 100)
                    {
                        ResumeFollowingCursor(cancelPointer: true);
                        break;
                    }
                }

                _flightElapsed += dt;
                double p = Math.Min(_flightElapsed / _flightDuration, 1.0);

                // Smoothstep easing: ease = 3t²−2t³
                double ease = p * p * (3.0 - 2.0 * p);

                // Quadratic bezier arc: B(t) = (1−t)²·P0 + 2(1−t)t·P1 + t²·P2 —
                // the buddy swoops in a parabola instead of a straight line.
                double omt = 1.0 - ease;
                _dotX = omt * omt * _flightStartX + 2 * omt * ease * _flightControlX + ease * ease * _flightTargetX;
                _dotY = omt * omt * _flightStartY + 2 * omt * ease * _flightControlY + ease * ease * _flightTargetY;

                // Rotate to face the direction of travel: tangent of the bezier,
                // B'(t) = 2(1−t)(P1−P0) + 2t(P2−P1). +90° because the triangle's
                // tip points up at 0°.
                double tanX = 2 * omt * (_flightControlX - _flightStartX) + 2 * ease * (_flightTargetX - _flightControlX);
                double tanY = 2 * omt * (_flightControlY - _flightStartY) + 2 * ease * (_flightTargetY - _flightControlY);
                DotRotate.Angle = Math.Atan2(tanY, tanX) * (180.0 / Math.PI) + 90.0;

                // Scale pulse: grows to 1.3× at midpoint, back to 1× on landing
                double scale = 1.0 + Math.Sin(p * Math.PI) * 0.3;
                DotScale.ScaleX = scale;
                DotScale.ScaleY = scale;

                if (p >= 1.0)
                {
                    DotScale.ScaleX = DotScale.ScaleY = 1.0;
                    DotRotate.Angle = -35.0; // back to the resting cursor angle
                    _dotVX = _dotVY = 0;

                    if (_isReturningToCursor)
                    {
                        // Landed back at the cursor — resume normal following.
                        _isReturningToCursor = false;
                        _dotState = DotState.FollowingCursor;
                    }
                    else
                    {
                        // Arrived at the target — the buddy STAYS visible, pointing
                        // at the element (original behaviour), and the speech bubble
                        // starts streaming in.
                        _dotState = DotState.PointingAtTarget;
                        StartBubble(Utterances[Rng.Next(Utterances.Length)], holdSeconds: 3.0, now);
                    }
                }
                break;

            case DotState.PointingAtTarget:
                // Buddy stays put at the target — no position update needed
                break;
        }

        // ── Speech bubble: character streaming + hold + fly-back ─────────────
        UpdateBubble(now);

        // ── Update element positions ────────────────────────────────────────

        // Move the dot with a RenderTransform (composition-level) rather than
        // Canvas.SetLeft/Top — no per-frame layout/arrange pass, which is the
        // single biggest smoothness win for cursor following in WPF.
        DotTranslate.X = _dotX - 16;
        DotTranslate.Y = _dotY - 16;

        // Waveform bars: bottom of 20 px container sits at _dotY so bars grow upward from there
        if (WaveformBars.Visibility == Visibility.Visible)
        {
            System.Windows.Controls.Canvas.SetLeft(WaveformBars, _dotX - 9);
            System.Windows.Controls.Canvas.SetTop(WaveformBars,  _dotY - 20);
            UpdateWaveformBars(dt);
        }

        // Spinner: centered on _dotX, _dotY
        if (ProcessingSpinner.Visibility == Visibility.Visible)
        {
            System.Windows.Controls.Canvas.SetLeft(ProcessingSpinner, _dotX - 11);
            System.Windows.Controls.Canvas.SetTop(ProcessingSpinner,  _dotY - 11);
        }
    }

    // ── Waveform bar animation ──────────────────────────────────────────────
    // Replicates original Clicky's formula: sine idle pulse + exponential audio response.

    public void SetAudioLevel(float level) => _audioLevel = level;

    private void UpdateWaveformBars(double dt)
    {
        _wavePhase += dt * 3.6; // advances phase at ~3.6 rad/s

        double normalized = Math.Max(_audioLevel - 0.008f, 0);
        double eased = Math.Pow(Math.Min(normalized * 2.85, 1.0), 0.76);

        for (int i = 0; i < 5; i++)
        {
            double reactive = eased * 10.0 * BarProfiles[i];
            double idle     = (Math.Sin(_wavePhase + i * 0.35) + 1.0) / 2.0 * 1.5;
            _waveformBars[i].Height = Math.Max(2, Math.Min(18, 3 + reactive + idle));
        }
    }

    // ── Processing spinner ──────────────────────────────────────────────────

    private void StartSpinner()
    {
        var anim = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.8))
        {
            RepeatBehavior = RepeatBehavior.Forever,
        };
        SpinnerRotate.BeginAnimation(RotateTransform.AngleProperty, anim);
    }

    private void StopSpinner() =>
        SpinnerRotate.BeginAnimation(RotateTransform.AngleProperty, null);

    // ── Public API called by CompanionManager ───────────────────────────────

    public void SetState(AppState state)
    {
        _appState = state;
        Dispatcher.Invoke(() =>
        {
            switch (state)
            {
                case AppState.Idle:
                    WaveformBars.Visibility      = Visibility.Collapsed;
                    ProcessingSpinner.Visibility = Visibility.Collapsed;
                    StopSpinner();
                    // An in-progress pointing sequence (bubble hold → return arc)
                    // self-resolves back to cursor following — don't cut it short.
                    CursorFollower.Visibility = Visibility.Visible;
                    // Drawings linger a few seconds after Bru stops talking, then fade.
                    ScheduleAnnotationLinger();
                    break;

                case AppState.Listening:
                    ProcessingSpinner.Visibility = Visibility.Collapsed;
                    StopSpinner();
                    // New question: clear drawings instantly, cancel the welcome
                    // bubble and any in-progress pointing/flight
                    ClearAnnotations(instant: true);
                    _welcomeActive = false;
                    HideBubble();
                    if (_dotState != DotState.FollowingCursor)
                        ResumeFollowingCursor(cancelPointer: true);
                    // Replace dot with animated waveform bars
                    CursorFollower.Visibility = Visibility.Collapsed;
                    WaveformBars.Visibility   = Visibility.Visible;
                    break;

                case AppState.Processing:
                    WaveformBars.Visibility      = Visibility.Collapsed;
                    CursorFollower.Visibility    = Visibility.Collapsed;
                    ProcessingSpinner.Visibility = Visibility.Visible;
                    StartSpinner();
                    break;

                case AppState.Speaking:
                    WaveformBars.Visibility      = Visibility.Collapsed;
                    ProcessingSpinner.Visibility = Visibility.Collapsed;
                    StopSpinner();
                    // Show dot unless it's currently pointing (TargetPointer center dot fills that role)
                    if (_dotState != DotState.PointingAtTarget)
                        CursorFollower.Visibility = Visibility.Visible;
                    break;
            }
        });
    }

    /// <summary>
    /// Flies the buddy along a bezier arc to the target screen coordinates.
    /// It lands beside the element (+8,+12 offset like the original), streams a
    /// speech bubble, holds, then arcs back to the cursor.
    /// </summary>
    public void ShowTargetAt(double physX, double physY, string label)
    {
        Dispatcher.Invoke(() =>
        {
            var dpiScale = CoordinateHelper.GetDpiScale();
            double wpfX = physX / dpiScale - Left;
            double wpfY = physY / dpiScale - Top;

            // Land beside the element rather than on top of it, clamped to screen.
            wpfX = Math.Clamp(wpfX + 8, 20, Math.Max(20, ActualWidth - 20));
            wpfY = Math.Clamp(wpfY + 12, 20, Math.Max(20, ActualHeight - 20));

            _flightLabel = label;
            _isReturningToCursor = false;
            _welcomeActive = false;
            HideBubble();
            BeginFlightTo(wpfX, wpfY);
            CursorFollower.Visibility = Visibility.Visible;
        });
    }

    /// <summary>Sets up a quadratic-bezier arc flight from the buddy's current position.</summary>
    private void BeginFlightTo(double targetX, double targetY)
    {
        _flightStartX  = _dotX;
        _flightStartY  = _dotY;
        _flightTargetX = targetX;
        _flightTargetY = targetY;
        _flightElapsed = 0;

        double dist = Math.Sqrt(
            (targetX - _dotX) * (targetX - _dotX) +
            (targetY - _dotY) * (targetY - _dotY));

        // Duration scales with distance: short hops quick, long flights dramatic.
        _flightDuration = Math.Clamp(dist / 800.0, 0.6, 1.4);

        // Control point: midpoint raised so the buddy swoops in a parabolic arc.
        double arcHeight = Math.Min(dist * 0.2, 80.0);
        _flightControlX = (_dotX + targetX) / 2.0;
        _flightControlY = (_dotY + targetY) / 2.0 - arcHeight;

        _dotState = DotState.FlyingToTarget;
    }

    // ── Speech bubble (character streaming, ported from the original) ────────

    private double _bubbleHoldSeconds = 3.0;

    /// <summary>
    /// Starts streaming a phrase into the bubble character by character with a
    /// pop-in scale bounce. After the hold, the bubble hides and (when pointing)
    /// the buddy flies back to the cursor.
    /// </summary>
    private void StartBubble(string text, double holdSeconds, double now)
    {
        _bubbleFullText      = text;
        _bubbleShownChars    = 0;
        _nextCharAtSeconds   = now;
        _bubbleHideAtSeconds = double.MaxValue; // set once fully streamed
        _bubbleHoldSeconds   = holdSeconds;

        TargetLabelText.Text = "";
        TargetLabel.BeginAnimation(UIElement.OpacityProperty, null);
        TargetLabel.Opacity    = 1;
        TargetLabel.Visibility = Visibility.Visible;

        System.Windows.Controls.Canvas.SetLeft(TargetLabel, _dotX + 12);
        System.Windows.Controls.Canvas.SetTop(TargetLabel,  _dotY + 14);

        // Pop-in: 0.5 → 1.0 with a springy overshoot (the "materializing" bounce).
        var pop = new DoubleAnimation(0.5, 1.0, TimeSpan.FromSeconds(0.35))
        {
            EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.6 },
        };
        LabelScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        LabelScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
    }

    /// <summary>Per-frame bubble driver: streaming rhythm, hold countdown, hide + return.</summary>
    private void UpdateBubble(double now)
    {
        if (TargetLabel.Visibility != Visibility.Visible || _bubbleFullText.Length == 0)
            return;

        // The welcome bubble follows the buddy while it trails the cursor.
        if (_welcomeActive)
        {
            System.Windows.Controls.Canvas.SetLeft(TargetLabel, _dotX + 12);
            System.Windows.Controls.Canvas.SetTop(TargetLabel,  _dotY + 14);
        }

        // Stream the next character at a natural speaking rhythm (30–60 ms/char).
        if (_bubbleShownChars < _bubbleFullText.Length && now >= _nextCharAtSeconds)
        {
            _bubbleShownChars++;
            TargetLabelText.Text = _bubbleFullText[.._bubbleShownChars];
            _nextCharAtSeconds = now + 0.03 + Rng.NextDouble() * 0.03;

            if (_bubbleShownChars == _bubbleFullText.Length)
                _bubbleHideAtSeconds = now + _bubbleHoldSeconds;
        }

        if (now >= _bubbleHideAtSeconds)
        {
            HideBubble();
            if (_welcomeActive)
                _welcomeActive = false;
            else if (_dotState == DotState.PointingAtTarget)
                StartReturnFlight();
        }
    }

    private void HideBubble()
    {
        _bubbleFullText      = "";
        _bubbleShownChars    = 0;
        _bubbleHideAtSeconds = double.MaxValue;
        TargetLabelText.Text = "";
        TargetLabel.Visibility = Visibility.Collapsed;
    }

    /// <summary>Arcs the buddy from the target back to the cursor's current position.</summary>
    private void StartReturnFlight()
    {
        Win32.GetCursorPos(out var pt);
        var dpiScale = CoordinateHelper.GetDpiScale();
        double cx = pt.X / dpiScale - Left;
        double cy = pt.Y / dpiScale - Top;

        _returnStartCursorX = cx;
        _returnStartCursorY = cy;
        _isReturningToCursor = true;
        BeginFlightTo(cx + CursorOffsetX, cy + CursorOffsetY);
    }

    /// <summary>
    /// Cancels any pointing/flight in progress and resumes spring cursor following.
    /// The spring naturally animates the buddy back from wherever it sits.
    /// </summary>
    private void ResumeFollowingCursor(bool cancelPointer)
    {
        if (cancelPointer)
        {
            TargetPointer.Visibility = Visibility.Collapsed;
            TargetLabel.BeginAnimation(UIElement.OpacityProperty, null);
            HideBubble();
        }

        _isReturningToCursor = false;
        _welcomeActive = false;
        DotRotate.Angle = -35.0;
        DotScale.ScaleX = DotScale.ScaleY = 1.0;
        _dotVX    = 0;
        _dotVY    = 0;
        _dotState = DotState.FollowingCursor;

        // Show the buddy only if the app isn't in a state with its own indicator
        if (_appState != AppState.Listening && _appState != AppState.Processing)
            CursorFollower.Visibility = Visibility.Visible;
    }

    // ── Screen drawings (highlight boxes, arrows, lines, notes) ─────────────
    // Each annotation "sketches itself" in via a stroke-dash animation, staggered
    // so Bru appears to draw step by step. Everything runs on the composition
    // clock (BeginTime offsets + keyframes) — no starvable timers.

    private int _annotationGeneration; // guards stale fade-out completions

    private static readonly System.Windows.Media.Brush AnnotationBrush = CreateAnnotationBrush();

    private static System.Windows.Media.Brush CreateAnnotationBrush()
    {
        var b = new SolidColorBrush(WpfColor.FromRgb(0x00, 0xE5, 0xFF));
        b.Freeze();
        return b;
    }

    public void ShowAnnotations(IReadOnlyList<ScreenAnnotation> annotations)
    {
        Dispatcher.Invoke(() =>
        {
            ClearAnnotations(instant: true);
            _annotationGeneration++;

            var dpi = CoordinateHelper.GetDpiScale();
            int index = 0;
            foreach (var a in annotations.Take(8))
            {
                double delay = index * 0.45; // Bru draws one thing at a time
                switch (a.Kind)
                {
                    case AnnotationKind.Box:
                        AddSketchedShape(BuildBoxGeometry(a, dpi), 2 * ((a.Bx + a.By) / dpi), delay);
                        if (a.Label.Length > 0)
                            AddChip(a.Label, a.Ax / dpi - Left, a.Ay / dpi - Top - 26, delay + 0.25);
                        break;

                    case AnnotationKind.Arrow:
                        AddSketchedShape(BuildArrowGeometry(a, dpi, out double arrowLen), arrowLen, delay);
                        if (a.Label.Length > 0)
                            AddChip(a.Label, a.Bx / dpi - Left + 10, a.By / dpi - Top + 6, delay + 0.25);
                        break;

                    case AnnotationKind.Line:
                        AddSketchedShape(BuildLineGeometry(a, dpi, out double lineLen), lineLen, delay);
                        break;

                    case AnnotationKind.Note:
                        AddChip(a.Label, a.Ax / dpi - Left, a.Ay / dpi - Top, delay);
                        break;
                }
                index++;
            }
        });
    }

    /// <summary>Fades out (or instantly removes) all current drawings.</summary>
    public void ClearAnnotations(bool instant = false)
    {
        Dispatcher.Invoke(() =>
        {
            _annotationGeneration++;
            if (instant || AnnotationCanvas.Children.Count == 0)
            {
                AnnotationCanvas.Children.Clear();
                AnnotationCanvas.BeginAnimation(OpacityProperty, null);
                AnnotationCanvas.Opacity = 1;
                return;
            }

            int gen = _annotationGeneration;
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromSeconds(0.4));
            fade.Completed += (_, _) =>
            {
                if (gen != _annotationGeneration) return;
                AnnotationCanvas.Children.Clear();
                AnnotationCanvas.BeginAnimation(OpacityProperty, null);
                AnnotationCanvas.Opacity = 1;
            };
            AnnotationCanvas.BeginAnimation(OpacityProperty, fade);
        });
    }

    /// <summary>After Bru finishes talking, drawings linger then fade on their own.</summary>
    private void ScheduleAnnotationLinger()
    {
        if (AnnotationCanvas.Children.Count == 0) return;

        int gen = _annotationGeneration;
        var anim = new DoubleAnimationUsingKeyFrames();
        anim.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        anim.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(8.0))));
        anim.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(8.6))));
        anim.Completed += (_, _) =>
        {
            if (gen != _annotationGeneration) return;
            AnnotationCanvas.Children.Clear();
            AnnotationCanvas.BeginAnimation(OpacityProperty, null);
            AnnotationCanvas.Opacity = 1;
        };
        AnnotationCanvas.BeginAnimation(OpacityProperty, anim);
    }

    // ── Geometry builders (physical px → WPF units, window-relative) ────────

    private System.Windows.Media.Geometry BuildBoxGeometry(ScreenAnnotation a, double dpi)
    {
        var rect = new Rect(a.Ax / dpi - Left, a.Ay / dpi - Top, a.Bx / dpi, a.By / dpi);
        return new RectangleGeometry(rect, 8, 8);
    }

    private System.Windows.Media.Geometry BuildLineGeometry(ScreenAnnotation a, double dpi, out double length)
    {
        var p1 = new System.Windows.Point(a.Ax / dpi - Left, a.Ay / dpi - Top);
        var p2 = new System.Windows.Point(a.Bx / dpi - Left, a.By / dpi - Top);
        length = (p2 - p1).Length;
        return new LineGeometry(p1, p2);
    }

    private System.Windows.Media.Geometry BuildArrowGeometry(ScreenAnnotation a, double dpi, out double length)
    {
        var tail = new System.Windows.Point(a.Ax / dpi - Left, a.Ay / dpi - Top);
        var tip  = new System.Windows.Point(a.Bx / dpi - Left, a.By / dpi - Top);
        var dir  = tip - tail;
        length = dir.Length;
        if (length < 1) { length = 1; dir = new Vector(1, 0); }
        dir.Normalize();

        // Arrowhead: two 14px barbs at ±28° off the shaft direction
        const double headLen = 14;
        var back = -dir;
        var left  = RotateVector(back, +28) * headLen;
        var right = RotateVector(back, -28) * headLen;

        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(tail, false, false);
            ctx.LineTo(tip, true, true);
            ctx.BeginFigure(tip + left, false, false);
            ctx.LineTo(tip, true, true);
            ctx.LineTo(tip + right, true, true);
        }
        g.Freeze();
        length += headLen * 2;
        return g;
    }

    private static Vector RotateVector(Vector v, double degrees)
    {
        double r = degrees * Math.PI / 180.0;
        return new Vector(
            v.X * Math.Cos(r) - v.Y * Math.Sin(r),
            v.X * Math.Sin(r) + v.Y * Math.Cos(r));
    }

    // ── Element factories ────────────────────────────────────────────────────

    /// <summary>Adds a stroke that draws itself in (dash-offset animation) after a delay.</summary>
    private void AddSketchedShape(System.Windows.Media.Geometry geometry, double strokeLength, double delaySeconds)
    {
        const double thickness = 2.5;
        double dashUnits = Math.Max(1, strokeLength / thickness);

        var path = new System.Windows.Shapes.Path
        {
            Data = geometry,
            Stroke = AnnotationBrush,
            StrokeThickness = thickness,
            StrokeDashArray = new DoubleCollection { dashUnits, dashUnits },
            StrokeDashOffset = dashUnits,
            StrokeDashCap = PenLineCap.Round,
            Opacity = 0,
            Effect = new DropShadowEffect { Color = WpfColor.FromRgb(0x00, 0xE5, 0xFF), BlurRadius = 8, ShadowDepth = 0, Opacity = 0.55 },
            IsHitTestVisible = false,
        };
        AnnotationCanvas.Children.Add(path);

        var begin = TimeSpan.FromSeconds(delaySeconds);
        path.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.12)) { BeginTime = begin });
        path.BeginAnimation(System.Windows.Shapes.Shape.StrokeDashOffsetProperty,
            new DoubleAnimation(dashUnits, 0, TimeSpan.FromSeconds(0.55))
            {
                BeginTime = begin,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
    }

    /// <summary>Adds a small cyan text chip (labels, notes, step numbers).</summary>
    private void AddChip(string text, double x, double y, double delaySeconds)
    {
        var chip = new System.Windows.Controls.Border
        {
            Background = AnnotationBrush,
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(7, 3, 7, 3),
            Opacity = 0,
            IsHitTestVisible = false,
            RenderTransformOrigin = new System.Windows.Point(0, 0.5),
            RenderTransform = new ScaleTransform(0.6, 0.6),
            Effect = new DropShadowEffect { Color = WpfColor.FromRgb(0x00, 0xE5, 0xFF), BlurRadius = 7, ShadowDepth = 0, Opacity = 0.5 },
            Child = new System.Windows.Controls.TextBlock
            {
                Text = text,
                Foreground = new SolidColorBrush(WpfColor.FromRgb(0x06, 0x20, 0x28)),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
                MaxWidth = 240,
                TextWrapping = TextWrapping.Wrap,
            },
        };
        System.Windows.Controls.Canvas.SetLeft(chip, Math.Max(2, x));
        System.Windows.Controls.Canvas.SetTop(chip, Math.Max(2, y));
        AnnotationCanvas.Children.Add(chip);

        var begin = TimeSpan.FromSeconds(delaySeconds);
        chip.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.18)) { BeginTime = begin });
        var pop = new DoubleAnimation(0.6, 1.0, TimeSpan.FromSeconds(0.3))
        {
            BeginTime = begin,
            EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 },
        };
        ((ScaleTransform)chip.RenderTransform).BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        ((ScaleTransform)chip.RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty, pop);
    }

    // ── Feedback bubble (silent failures, error messages) ───────────────────

    /// <summary>
    /// Shows a transient message bubble near the cursor dot — used for silent failures
    /// so the user always knows what happened. Reuses the same TargetLabel element.
    /// </summary>
    public void ShowFeedback(string message)
    {
        Dispatcher.Invoke(() =>
        {
            // Cancel any pending label timer (e.g. from a prior pointing label).

            // Position near current dot location
            System.Windows.Controls.Canvas.SetLeft(TargetLabel, _dotX + 20);
            System.Windows.Controls.Canvas.SetTop(TargetLabel,  _dotY + 12);

            TargetLabelText.Text    = message;
            TargetLabel.Opacity     = 0;
            TargetLabel.Visibility  = Visibility.Visible;

            // Drive fade-in → hold → fade-out as ONE keyframe animation on the
            // composition clock. A DispatcherTimer at default (Background) priority
            // can be starved indefinitely by the Render-priority cursor-follow timer,
            // which left this label stuck on screen. Keyframes can't be starved.
            const double holdSeconds = 3.0;
            var anim = new DoubleAnimationUsingKeyFrames();
            anim.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            anim.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.25))));
            anim.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.25 + holdSeconds))));
            anim.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.75 + holdSeconds))));
            anim.Completed += (_, _) =>
            {
                TargetLabel.Visibility = Visibility.Collapsed;
                TargetLabel.BeginAnimation(UIElement.OpacityProperty, null);
            };
            TargetLabel.BeginAnimation(UIElement.OpacityProperty, anim);
        });
    }

    // ── Spinner transcript-confirmation pulse ───────────────────────────────

    /// <summary>
    /// Brief scale-throb + pink color flash on the spinner to signal "I heard you."
    /// Called the moment AssemblyAI delivers the final transcript.
    /// </summary>
    public void PulseSpinner()
    {
        Dispatcher.Invoke(() =>
        {
            if (ProcessingSpinner.Visibility != Visibility.Visible) return;

            // Scale: 1.0 → 1.45 → 1.0 over 0.5 s
            var scaleAnim = new DoubleAnimationUsingKeyFrames();
            scaleAnim.KeyFrames.Add(new LinearDoubleKeyFrame(1.0,  KeyTime.FromTimeSpan(TimeSpan.Zero)));
            scaleAnim.KeyFrames.Add(new EasingDoubleKeyFrame(1.45, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.18)))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            scaleAnim.KeyFrames.Add(new EasingDoubleKeyFrame(1.0,  KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.5)))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } });

            SpinnerScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
            SpinnerScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);

            // Color: blue → light pink → blue over the same duration
            var colorAnim = new ColorAnimationUsingKeyFrames();
            colorAnim.KeyFrames.Add(new LinearColorKeyFrame(
                WpfColor.FromRgb(0x00, 0xE5, 0xFF), KeyTime.FromTimeSpan(TimeSpan.Zero)));
            colorAnim.KeyFrames.Add(new EasingColorKeyFrame(
                WpfColor.FromRgb(0xFF, 0x8A, 0xBA), KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.18)))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            colorAnim.KeyFrames.Add(new LinearColorKeyFrame(
                WpfColor.FromRgb(0x00, 0xE5, 0xFF), KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.5))));

            SpinnerStrokeBrush.BeginAnimation(SolidColorBrush.ColorProperty, colorAnim);
        });
    }

    // ── Pulse ring animation ────────────────────────────────────────────────

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
