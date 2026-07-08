using Editor.Core.Buffer;
using Editor.Core.Models;

namespace Editor.Core.Engine;

/// <summary>
/// Builds the Ctrl+G / g&lt;C-g&gt; file-info status message (name, modified flag,
/// line/col, percent through file, word count, byte offset). Stateless.
/// </summary>
public static class BufferInfoReporter
{
    public static string BuildFileInfo(VimBuffer vbuf, CursorPosition cursor, bool brief)
    {
        var buf = vbuf.Text;
        var totalLines = buf.LineCount;
        var currentLine = cursor.Line + 1;  // 1-based
        var currentCol = cursor.Column + 1; // 1-based

        // File name
        string name = vbuf.FilePath != null
            ? System.IO.Path.GetFileName(vbuf.FilePath)
            : "[No Name]";

        // Modified flag
        string modified = buf.IsModified ? " [Modified]" : "";

        // Percent through file
        int pct = totalLines <= 1 ? 100 : (int)Math.Round(cursor.Line * 100.0 / (totalLines - 1));
        pct = Math.Clamp(pct, 0, 100);

        if (brief)
            return $"\"{name}\"{modified} line {currentLine} of {totalLines} --{pct}%-- col {currentCol}";

        int wordCount = CountWords(buf);
        long byteOffset = CountBytesToCursor(buf, cursor);
        return $"Col {currentCol}, Line {currentLine} of {totalLines}{modified}, Word {wordCount}, Byte {byteOffset}";
    }

    private static int CountWords(TextBuffer buf)
    {
        int words = 0;
        for (int i = 0; i < buf.LineCount; i++)
        {
            var line = buf.GetLine(i);
            bool inWord = false;
            foreach (char c in line)
            {
                bool isWordChar = char.IsLetterOrDigit(c) || c == '_';
                if (isWordChar && !inWord) { words++; inWord = true; }
                else if (!isWordChar) inWord = false;
            }
        }
        return words;
    }

    private static long CountBytesToCursor(TextBuffer buf, CursorPosition cursor)
    {
        long bytes = 0;
        for (int i = 0; i < cursor.Line; i++)
            bytes += System.Text.Encoding.UTF8.GetByteCount(buf.GetLine(i)) + 1; // +1 for newline
        if (cursor.Line < buf.LineCount)
        {
            var line = buf.GetLine(cursor.Line);
            int col = Math.Min(cursor.Column, line.Length);
            bytes += System.Text.Encoding.UTF8.GetByteCount(line[..col]);
        }
        return bytes + 1; // 1-based byte offset
    }
}
