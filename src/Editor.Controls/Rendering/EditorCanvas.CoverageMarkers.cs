using System.Collections.Generic;
using System.Windows.Media;

using Editor.Controls.Themes;

namespace Editor.Controls.Rendering;

/// <summary>カバレッジをテスト実行列と同じガター列へ表示するためのEditor汎用モデル。</summary>
public enum CoverageMarkerKind
{
    Covered,
    Partial,
    Uncovered,
}

/// <summary>1行分のcoverage marker。Line0はバッファ行の0始まり。</summary>
public readonly record struct EditorCoverageMarker(int Line0, CoverageMarkerKind Kind, string? Tooltip = null);

public partial class EditorCanvas
{
    private bool _coverageMarkersEnabled;
    private readonly Dictionary<int, EditorCoverageMarker> _coverageMarkers = new();

    public void SetCoverageMarkersEnabled(bool enabled)
    {
        if (_coverageMarkersEnabled == enabled) return;
        _coverageMarkersEnabled = enabled;
        SetHoveredTestGlyphLine(_hoveredTestGlyphLine);
        InvalidateArrange();
        InvalidateVisual();
    }

    public void SetCoverageMarkers(IReadOnlyList<EditorCoverageMarker> markers)
    {
        _coverageMarkers.Clear();
        foreach (var marker in markers)
            if (marker.Line0 >= 0) _coverageMarkers[marker.Line0] = marker;
        SetHoveredTestGlyphLine(_hoveredTestGlyphLine);
        InvalidateVisual();
    }

    private bool HasCoverageMarker(int line) => _coverageMarkers.ContainsKey(line);

    private void DrawCoverageMarker(DrawingContext dc, int line, double y, double x, int colWidth)
    {
        if (!_coverageMarkers.TryGetValue(line, out var marker)) return;
        var brush = marker.Kind switch
        {
            CoverageMarkerKind.Covered => Theme.GitAdded,
            CoverageMarkerKind.Partial => Theme.DiagnosticWarning,
            CoverageMarkerKind.Uncovered => Theme.DiagnosticError,
            _ => Theme.LineNumberFg,
        };
        if (brush is null) return;
        // 3pxの縦バー。テストグリフが無い行ではcoverage専用の軽い表示になる。
        dc.DrawRectangle(brush, null, new System.Windows.Rect(x + Math.Max(0, (colWidth - 3) / 2.0), y, 3, _lineHeight));
    }
}
