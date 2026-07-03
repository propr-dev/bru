using System.Windows.Forms;

namespace ClickyWindows.Helpers;

/// <summary>
/// Converts coordinates between coordinate spaces:
/// - Physical pixels (Win32 / screen capture)
/// - WPF device-independent units (96 DPI base)
/// - Claude Computer Use resolution (1024x768)
/// </summary>
internal static class CoordinateHelper
{
    // Computer Use works best on an image no larger than ~1280×800. We scale the
    // screenshot UNIFORMLY to fit inside this box (never upscaling), preserving aspect
    // ratio so the picture isn't distorted — distortion and detail loss both hurt aim.
    private const double CuMaxWidth  = 1280.0;
    private const double CuMaxHeight = 800.0;

    /// <summary>
    /// Gets the DPI scale factor for the primary screen (physical pixels / WPF DIPs).
    /// </summary>
    public static double GetDpiScale()
    {
        using var g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero);
        return g.DpiX / 96.0;
    }

    /// <summary>
    /// Converts physical pixel coordinates (from screen capture or Win32) to
    /// WPF device-independent units, returned as (x, y) doubles.
    /// </summary>
    public static (double x, double y) PhysicalToWpf(int physX, int physY, double dpiScale)
        => (physX / dpiScale, physY / dpiScale);

    /// <summary>
    /// Converts WPF device-independent units to physical pixel coordinates.
    /// </summary>
    public static (int x, int y) WpfToPhysical(double wpfX, double wpfY, double dpiScale)
        => ((int)(wpfX * dpiScale), (int)(wpfY * dpiScale));

    /// <summary>
    /// Scales Claude Computer Use coordinates (relative to a standard resolution)
    /// to physical screen pixels, then to WPF DIPs.
    /// Returns (wpfX, wpfY).
    /// </summary>
    public static (double x, double y) ComputerUseToWpf(
        int cuX, int cuY,
        int screenWidth, int screenHeight,
        double dpiScale)
    {
        var (cuW, cuH) = DetectComputerUseResolution(screenWidth, screenHeight);

        cuX = Math.Clamp(cuX, 0, cuW - 1);
        cuY = Math.Clamp(cuY, 0, cuH - 1);

        double physX = (double)cuX / cuW * screenWidth;
        double physY = (double)cuY / cuH * screenHeight;

        return (physX / dpiScale, physY / dpiScale);
    }

    internal static (int w, int h) DetectComputerUseResolution(int screenW, int screenH)
    {
        if (screenW <= 0 || screenH <= 0) return ((int)CuMaxWidth, (int)CuMaxHeight);

        // Uniform scale to fit inside the CU box; never upscale beyond native.
        double scale = Math.Min(Math.Min(CuMaxWidth / screenW, CuMaxHeight / screenH), 1.0);
        int w = Math.Max(1, (int)Math.Round(screenW * scale));
        int h = Math.Max(1, (int)Math.Round(screenH * scale));
        return (w, h);
    }
}
