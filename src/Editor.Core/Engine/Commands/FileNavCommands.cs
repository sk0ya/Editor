using Editor.Core.Buffer;
using Editor.Core.Models;

namespace Editor.Core.Engine.Commands;

/// <summary>
/// Handles the file/URL navigation Normal mode commands (gf/gx): extracts a
/// path-like token under the cursor and emits an OpenFileRequested/OpenUrlRequested
/// event for the host to act on.
/// </summary>
public class FileNavCommands(BufferManager bufferManager)
{
    public bool TryHandle(string cmd, CursorPosition cursor, List<VimEvent> events)
    {
        switch (cmd)
        {
            case "gf":
            {
                var line = bufferManager.Current.Text.GetLine(cursor.Line);
                var path = ExtractFilePathUnderCursor(line, cursor.Column);
                if (!string.IsNullOrEmpty(path))
                {
                    path = ResolveRelativeToCurrentFile(path);
                    events.Add(VimEvent.OpenFileRequested(path));
                }
                return true;
            }
            case "gx":
            {
                var line = bufferManager.Current.Text.GetLine(cursor.Line);
                var token = ExtractFilePathUnderCursor(line, cursor.Column);
                if (!string.IsNullOrEmpty(token))
                {
                    if (token.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                        token.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                        token.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase))
                    {
                        events.Add(VimEvent.OpenUrlRequested(token));
                    }
                    else
                    {
                        token = ResolveRelativeToCurrentFile(token);
                        events.Add(VimEvent.OpenFileRequested(token));
                    }
                }
                return true;
            }
            default:
                return false;
        }
    }

    private string ResolveRelativeToCurrentFile(string path)
    {
        if (System.IO.Path.IsPathRooted(path)) return path;
        var dir = bufferManager.Current.FilePath is { } fp
            ? System.IO.Path.GetDirectoryName(fp)
            : null;
        return dir != null ? System.IO.Path.GetFullPath(System.IO.Path.Combine(dir, path)) : path;
    }

    private static string? ExtractFilePathUnderCursor(string line, int col)
    {
        if (string.IsNullOrEmpty(line) || col < 0 || col >= line.Length)
            return null;

        // Path chars: anything except whitespace, quotes, and common delimiters
        static bool IsPathChar(char c) =>
            !char.IsWhiteSpace(c) && c != '"' && c != '\'' && c != '<' && c != '>' &&
            c != '(' && c != ')' && c != '[' && c != ']' && c != '{' && c != '}' &&
            c != ',' && c != ';';

        if (!IsPathChar(line[col])) return null;

        int start = col;
        while (start > 0 && IsPathChar(line[start - 1])) start--;

        int end = col;
        while (end < line.Length - 1 && IsPathChar(line[end + 1])) end++;

        return line[start..(end + 1)];
    }
}
