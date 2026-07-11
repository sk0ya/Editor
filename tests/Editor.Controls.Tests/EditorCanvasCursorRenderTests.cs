using System.Reflection;
using System.Windows.Media.Imaging;
using Editor.Controls.Rendering;
using Editor.Core.Engine;
using Editor.Core.Models;

namespace Editor.Controls.Tests;

public class EditorCanvasCursorRenderTests
{
    [Fact]
    public void Render_CursorOnBlankLineAfterEmojiLine_DoesNotThrow()
    {
        WpfTestHost.Run(() =>
        {
            string pic = char.ConvertFromUtf32(0x1F5BC) + "️"; // 🖼️, 3 UTF-16 units
            var canvas = new EditorCanvas();
            canvas.UpdateFont("Consolas", 14);
            canvas.IsActive = true;
            canvas.SetMode(VimMode.Normal);
            canvas.SetLines(["abc" + pic, "", "xyz"]);

            // Simulate `$` on line 0 (end of line, on/after the emoji cluster), then `j` onto the blank line.
            canvas.SetCursor(new CursorPosition(0, ("abc" + pic).Length - 1));
            var window = WpfTestHost.Load(canvas);
            try
            {
                var bmp = new RenderTargetBitmap(200, 200, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                bmp.Render(canvas); // force OnRender while cursor is on the emoji line

                canvas.SetCursor(new CursorPosition(1, 0));
                canvas.UpdateLayout();
                bmp.Render(canvas); // force OnRender while cursor is on the blank line below
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// Dumps the real (font-shaped) cursor width for every column of a line containing
    /// "a" + 🖼️ + " " + "b" so a zero/negative width glitch around the emoji or the space
    /// right after it would show up directly, using the actual installed font stack.
    /// </summary>
    [Fact]
    public void CursorGlyphWidth_AroundEmojiAndFollowingSpace_IsNeverZeroOrNegative()
    {
        WpfTestHost.Run(() =>
        {
            string pic = char.ConvertFromUtf32(0x1F5BC) + "️";
            string line = "a" + pic + " b";
            var canvas = new EditorCanvas();
            canvas.UpdateFont("Consolas", 14);
            canvas.IsActive = true;
            canvas.SetMode(VimMode.Normal);
            canvas.SetLines([line, "", "xyz"]);
            var window = WpfTestHost.Load(canvas);
            try
            {
                var setActiveLine = typeof(EditorCanvas).GetMethod("SetActiveLine", BindingFlags.NonPublic | BindingFlags.Instance)!;
                var cursorGlyphWidth = typeof(EditorCanvas).GetMethod("CursorGlyphWidth", BindingFlags.NonPublic | BindingFlags.Instance)!;
                var getVisualX = typeof(EditorCanvas).GetMethod("GetVisualX", BindingFlags.NonPublic | BindingFlags.Instance)!;

                setActiveLine.Invoke(canvas, [0]);

                // Only check columns a real cursor can land on (grapheme-cluster boundaries),
                // matching what the fixed h/l/$/etc. motions now produce.
                var widths = new List<(int Col, double Width, double X)>();
                int col2 = 0;
                while (col2 < line.Length)
                {
                    double w = (double)cursorGlyphWidth.Invoke(canvas, [line, col2])!;
                    double x = (double)getVisualX.Invoke(canvas, [line, col2])!;
                    widths.Add((col2, w, x));
                    col2 = Editor.Core.Text.GraphemeCluster.NextBoundary(line, col2, 1);
                }

                foreach (var (col, w, x) in widths)
                    Assert.True(w > 0.01, $"col {col} ('{(col < line.Length ? line[col] : ' ')}') has non-positive cursor width {w} at x={x}. All: {string.Join(", ", widths)}");
            }
            finally
            {
                window.Close();
            }
        });
    }
}
