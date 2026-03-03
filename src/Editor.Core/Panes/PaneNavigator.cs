using Editor.Core.Models;

namespace Editor.Core.Panes;

/// <summary>
/// Axis-aligned bounding rectangle for a pane (pixel coordinates).
/// </summary>
public readonly record struct PaneRect(double Left, double Top, double Right, double Bottom)
{
    public double Width  => Right  - Left;
    public double Height => Bottom - Top;
    public double MidX   => (Left + Right)  / 2.0;
    public double MidY   => (Top  + Bottom) / 2.0;
}

/// <summary>
/// Stateless, WPF-free spatial navigator for split panes.
/// All navigation is based on the pixel rectangles of the panes, which
/// makes it correct regardless of how deeply nested the split tree is.
/// </summary>
public static class PaneNavigator
{
    // Panes share exact edges in a grid layout, but floating-point layout
    // arithmetic can introduce tiny sub-pixel errors, so allow this slack.
    private const double EdgeEpsilon = 2.0;

    /// <summary>
    /// Returns the index (into <paramref name="all"/>) of the pane that is
    /// spatially adjacent to <paramref name="current"/> in direction
    /// <paramref name="dir"/>, or <c>null</c> if no candidate exists.
    ///
    /// For Next / Prev the caller must use its own tree-order cycle.
    /// </summary>
    public static int? FindNext(
        PaneRect current,
        IReadOnlyList<PaneRect> all,
        WindowNavDir dir)
    {
        if (dir == WindowNavDir.Next || dir == WindowNavDir.Prev)
            return null; // Caller handles cyclic order

        int?   best      = null;
        double bestPrim  = double.MaxValue;
        double bestSec   = double.MaxValue;

        for (int i = 0; i < all.Count; i++)
        {
            var c = all[i];
            if (!IsCandidate(current, c, dir)) continue;

            (double prim, double sec) = Score(current, c, dir);

            // Primary: edge distance in the travel direction (prefer nearest)
            // Secondary: midpoint distance in the perpendicular axis (prefer centred)
            if (prim < bestPrim - EdgeEpsilon ||
                (Math.Abs(prim - bestPrim) <= EdgeEpsilon && sec < bestSec))
            {
                bestPrim = prim;
                bestSec  = sec;
                best     = i;
            }
        }

        return best;
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static bool IsCandidate(PaneRect from, PaneRect to, WindowNavDir dir)
    {
        // 1. The candidate must be on the correct side.
        // 2. The two panes must share at least 1 px of overlap on the
        //    perpendicular axis (their strips cross each other).
        return dir switch
        {
            WindowNavDir.Left  => to.Right  <= from.Left   + EdgeEpsilon
                                   && VertOverlap(from, to),
            WindowNavDir.Right => to.Left   >= from.Right  - EdgeEpsilon
                                   && VertOverlap(from, to),
            WindowNavDir.Up    => to.Bottom <= from.Top    + EdgeEpsilon
                                   && HorzOverlap(from, to),
            WindowNavDir.Down  => to.Top    >= from.Bottom - EdgeEpsilon
                                   && HorzOverlap(from, to),
            _ => false
        };
    }

    private static (double primary, double secondary) Score(
        PaneRect from, PaneRect to, WindowNavDir dir)
    {
        return dir switch
        {
            WindowNavDir.Left  => (from.Left   - to.Right,  Math.Abs(from.MidY - to.MidY)),
            WindowNavDir.Right => (to.Left     - from.Right, Math.Abs(from.MidY - to.MidY)),
            WindowNavDir.Up    => (from.Top    - to.Bottom, Math.Abs(from.MidX - to.MidX)),
            WindowNavDir.Down  => (to.Top      - from.Bottom, Math.Abs(from.MidX - to.MidX)),
            _ => (double.MaxValue, double.MaxValue)
        };
    }

    // Vertical strip overlap: top < other.Bottom && other.Top < bottom
    private static bool VertOverlap(PaneRect a, PaneRect b)
        => a.Top  < b.Bottom - EdgeEpsilon && b.Top  < a.Bottom - EdgeEpsilon;

    // Horizontal strip overlap: left < other.Right && other.Left < right
    private static bool HorzOverlap(PaneRect a, PaneRect b)
        => a.Left < b.Right  - EdgeEpsilon && b.Left < a.Right  - EdgeEpsilon;
}
