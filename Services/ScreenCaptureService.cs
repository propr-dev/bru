using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;
using ClickyWindows.Helpers;

namespace ClickyWindows.Services;

public record ScreenshotResult(
    byte[] JpegBytes,      // raw JPEG data
    string Base64,         // base64-encoded JPEG for Claude API
    string Label,          // human-readable label, includes the image's pixel dimensions
    Rectangle Bounds,      // physical pixel bounds of the captured screen region
    int ImageWidth,        // actual pixel width of the JPEG sent to Claude
    int ImageHeight        // actual pixel height of the JPEG sent to Claude
);

/// <summary>
/// Captures JPEG screenshots of all connected monitors using GDI (System.Drawing).
/// Reliable, no extra packages required, produces JPEG at quality 80 (matching macOS Clicky).
/// The monitor where the cursor is located is returned first.
/// </summary>
public class ScreenCaptureService
{
    // 72 keeps UI text crisp while trimming upload size ~25% vs 80 — faster requests.
    private const long JpegQuality = 72L;

    public List<ScreenshotResult> CaptureAll()
    {
        Win32.GetCursorPos(out var cursorPos);

        var results = new List<ScreenshotResult>();
        var screens = Screen.AllScreens;

        // Sort: screen containing cursor comes first
        var sorted = screens
            .OrderByDescending(s => s.Bounds.Contains(cursorPos.X, cursorPos.Y))
            .ToList();

        foreach (var screen in sorted)
        {
            var result = CaptureScreen(screen, cursorPos);
            if (result != null)
                results.Add(result);
        }

        return results;
    }

    // Longest edge of the screenshot sent to Claude. The API downsamples anything
    // larger anyway, so sending native 2K/4K frames wastes tokens AND breaks the
    // coordinate contract (the model sees a smaller image than the label claims).
    // 1568 is the API's own long-edge cap — what we send is exactly what it sees.
    private const int MaxImageLongEdge = 1568;

    private static ScreenshotResult? CaptureScreen(Screen screen, Win32.POINT cursorPos)
    {
        try
        {
            var bounds = screen.Bounds;
            using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size);
            }

            // Uniformly downscale to the API's long-edge cap (never upscale).
            double scale = Math.Min(1.0, (double)MaxImageLongEdge / Math.Max(bounds.Width, bounds.Height));
            int imgW = Math.Max(1, (int)Math.Round(bounds.Width * scale));
            int imgH = Math.Max(1, (int)Math.Round(bounds.Height * scale));

            byte[] jpeg;
            if (scale < 1.0)
            {
                using var resized = new Bitmap(imgW, imgH, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(resized))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.DrawImage(bitmap, 0, 0, imgW, imgH);
                }
                jpeg = EncodeJpeg(resized);
            }
            else
            {
                jpeg = EncodeJpeg(bitmap);
            }

            bool hasCursor = bounds.Contains(cursorPos.X, cursorPos.Y);
            // The pixel dimensions in the label anchor Claude's [POINT:x,y] coordinate
            // space to the exact image it sees (the trick the original macOS Clicky uses).
            string label = (hasCursor
                ? "Primary focus: the display the user's cursor is on"
                : "Secondary display (no cursor)")
                + $" (image dimensions: {imgW}x{imgH} pixels)";

            return new ScreenshotResult(jpeg, Convert.ToBase64String(jpeg), label, bounds, imgW, imgH);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Captures a specific screen region and resizes it to the given dimensions.
    /// Used to produce a Computer Use-compatible screenshot at the expected CU resolution.
    /// </summary>
    public ScreenshotResult? CaptureResized(System.Drawing.Rectangle bounds, int targetWidth, int targetHeight)
    {
        try
        {
            using var fullBitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(fullBitmap))
                g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size);

            using var resized = new Bitmap(targetWidth, targetHeight, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(resized))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(fullBitmap, 0, 0, targetWidth, targetHeight);
            }

            byte[] jpeg = EncodeJpeg(resized);
            string base64 = Convert.ToBase64String(jpeg);
            return new ScreenshotResult(jpeg, base64, $"Resized {targetWidth}x{targetHeight}", bounds,
                targetWidth, targetHeight);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Captures a region of the screen at NATIVE resolution (no scaling). Used for the
    /// second "zoom" pointing pass — a full-detail crop around the coarse estimate.
    /// Coordinates are absolute physical pixels (virtual-screen space).
    /// </summary>
    public ScreenshotResult? CaptureRegion(Rectangle region)
    {
        try
        {
            using var bitmap = new Bitmap(region.Width, region.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bitmap))
                g.CopyFromScreen(region.Left, region.Top, 0, 0, region.Size);

            byte[] jpeg = EncodeJpeg(bitmap);
            return new ScreenshotResult(jpeg, Convert.ToBase64String(jpeg),
                $"Region {region.Width}x{region.Height} at ({region.X},{region.Y})", region,
                region.Width, region.Height);
        }
        catch
        {
            return null;
        }
    }

    private static byte[] EncodeJpeg(Bitmap bitmap)
    {
        var jpegEncoder = GetEncoder(ImageFormat.Jpeg)!;
        var encoderParams = new EncoderParameters(1);
        encoderParams.Param[0] = new EncoderParameter(
            System.Drawing.Imaging.Encoder.Quality, JpegQuality);

        using var ms = new MemoryStream();
        bitmap.Save(ms, jpegEncoder, encoderParams);
        return ms.ToArray();
    }

    private static ImageCodecInfo? GetEncoder(ImageFormat format)
    {
        return ImageCodecInfo.GetImageEncoders()
            .FirstOrDefault(c => c.FormatID == format.Guid);
    }
}
