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
}
