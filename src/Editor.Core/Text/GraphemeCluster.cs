using System.Globalization;

namespace Editor.Core.Text;

/// <summary>
/// Extended-grapheme-cluster boundaries for a buffer line, so cursor motion and
/// character-wise edits step over one visible character even when it spans multiple
/// UTF-16 units (surrogate-pair emoji, variation selectors like U+FE0F, ZWJ sequences,
/// regional-indicator flag pairs, combining marks). Backed by <see cref="StringInfo"/>,
/// which implements full Unicode grapheme-cluster segmentation.
/// </summary>
public static class GraphemeCluster
{
    /// <summary>UTF-16 unit length of the cluster starting at <paramref name="index"/>.</summary>
    public static int LengthAt(string text, int index)
    {
        if (index < 0 || index >= text.Length) return 1;
        return StringInfo.GetNextTextElementLength(text.AsSpan(index));
    }

    /// <summary>Steps forward <paramref name="count"/> cluster boundaries from <paramref name="index"/>, clamped to <c>text.Length</c>.</summary>
    public static int NextBoundary(string text, int index, int count = 1)
    {
        int col = Math.Clamp(index, 0, text.Length);
        for (int i = 0; i < count && col < text.Length; i++)
            col += LengthAt(text, col);
        return col;
    }

    /// <summary>Steps backward <paramref name="count"/> cluster boundaries from <paramref name="index"/>, clamped to 0.</summary>
    public static int PrevBoundary(string text, int index, int count = 1)
    {
        int col = Math.Clamp(index, 0, text.Length);
        for (int i = 0; i < count && col > 0; i++)
        {
            int start = 0, probe = 0;
            while (probe < col)
            {
                start = probe;
                probe += LengthAt(text, probe);
            }
            col = start;
        }
        return col;
    }

    /// <summary>The start of the grapheme cluster containing <paramref name="index"/> (itself if already a boundary).</summary>
    public static int ClusterStart(string text, int index) => PrevBoundary(text, Math.Min(index + 1, text.Length), 1);
}
