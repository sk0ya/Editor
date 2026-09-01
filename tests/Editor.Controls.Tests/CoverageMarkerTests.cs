using System.Reflection;
using Editor.Controls.Rendering;
using Xunit;

namespace Editor.Controls.Tests;

public sealed class CoverageMarkerTests
{
    [Fact]
    public void Coverage_markers_share_the_test_column_and_ignore_negative_lines()
    {
        RunSta(() =>
        {
            var canvas = new EditorCanvas();
            canvas.SetCoverageMarkers([
                new EditorCoverageMarker(-1, CoverageMarkerKind.Uncovered),
                new EditorCoverageMarker(2, CoverageMarkerKind.Covered, "実行済み"),
                new EditorCoverageMarker(2, CoverageMarkerKind.Uncovered, "未実行"),
            ]);
            canvas.SetCoverageMarkersEnabled(true);

            var markers = (System.Collections.IDictionary)typeof(EditorCanvas)
                .GetField("_coverageMarkers", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(canvas)!;
            Assert.Single(markers);
            Assert.True((bool)typeof(EditorCanvas)
                .GetField("_coverageMarkersEnabled", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(canvas)!);
            Assert.Equal(CoverageMarkerKind.Uncovered,
                ((EditorCoverageMarker)markers[2]!).Kind);
        });
    }

    [Fact]
    public void Disabling_coverage_markers_keeps_the_data_for_a_fast_reenable()
    {
        RunSta(() =>
        {
            var canvas = new EditorCanvas();
            canvas.SetCoverageMarkers([new EditorCoverageMarker(0, CoverageMarkerKind.Partial)]);
            canvas.SetCoverageMarkersEnabled(true);
            canvas.SetCoverageMarkersEnabled(false);

            var markers = (System.Collections.IDictionary)typeof(EditorCanvas)
                .GetField("_coverageMarkers", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(canvas)!;
            Assert.Single(markers);
        });
    }

    private static void RunSta(Action action)
        => WpfTestHost.Run(action);
}
