namespace ClickyWindows.Models;

public enum AnnotationKind
{
    Box,    // A=(x, y) top-left, B=(width, height) — highlight region
    Arrow,  // A=(x1, y1) tail, B=(x2, y2) tip
    Line,   // A=(x1, y1), B=(x2, y2)
    Note,   // A=(x, y) anchor; B unused
}

/// <summary>
/// One temporary drawing on the user's screen, in PHYSICAL pixel coordinates
/// (already scaled from the screenshot's image space by CompanionManager).
/// </summary>
public record ScreenAnnotation(
    AnnotationKind Kind,
    double Ax, double Ay,
    double Bx, double By,
    string Label);
