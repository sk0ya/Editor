using Editor.Core.Buffer;

namespace Editor.Core.Engine;

/// <summary>
/// Collects Ctrl+N/P (keyword), Ctrl+X Ctrl+F (file path), and Ctrl+X Ctrl+L
/// (whole line) completion candidates. Stateless — takes the collaborators it
/// needs per call, mirroring <see cref="MotionEngine"/>.
/// </summary>
public static class CompletionCollector
{
    public static string[] CollectBufferKeywords(BufferManager bufferManager, string prefix)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var vbuf in bufferManager.Buffers)
        {
            for (int l = 0; l < vbuf.Text.LineCount; l++)
            {
                var line = vbuf.Text.GetLine(l);
                int i = 0;
                while (i < line.Length)
                {
                    if (MotionEngine.IsWordChar(line[i]))
                    {
                        int start = i;
                        while (i < line.Length && MotionEngine.IsWordChar(line[i]))
                            i++;
                        var word = line[start..i];
                        if (word.Length > prefix.Length &&
                            word.StartsWith(prefix, StringComparison.Ordinal) &&
                            seen.Add(word))
                            result.Add(word);
                    }
                    else i++;
                }
            }
        }
        return [.. result];
    }

    public static string[] CollectFilePathCompletions(BufferManager bufferManager, string prefix)
    {
        // Determine base directory for relative paths
        string? currentFile = bufferManager.Current.FilePath;
        string baseDir = currentFile != null
            ? Path.GetDirectoryName(currentFile) ?? Directory.GetCurrentDirectory()
            : Directory.GetCurrentDirectory();

        // Split prefix into directory part and file prefix
        string dirPart = Path.GetDirectoryName(prefix) ?? "";
        string filePart = Path.GetFileName(prefix);
        string searchDir = dirPart.Length > 0
            ? (Path.IsPathRooted(dirPart) ? dirPart : Path.Combine(baseDir, dirPart))
            : baseDir;

        if (!Directory.Exists(searchDir))
            return [];

        var results = new List<string>();
        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(searchDir, filePart + "*")
                                           .OrderBy(e => e))
            {
                string name = Path.GetFileName(entry);
                bool isDir = Directory.Exists(entry);
                string completion = dirPart.Length > 0
                    ? Path.Combine(dirPart, name).Replace('\\', '/')
                    : name;
                if (isDir) completion += "/";
                results.Add(completion);
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
        return [.. results];
    }

    public static string[] CollectLineCompletions(BufferManager bufferManager, string prefix, int currentLine)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var vbuf in bufferManager.Buffers)
        {
            for (int l = 0; l < vbuf.Text.LineCount; l++)
            {
                if (vbuf == bufferManager.Current && l == currentLine) continue;
                var line = vbuf.Text.GetLine(l);
                var trimmed = line.TrimStart();
                if (trimmed.Length > prefix.Length &&
                    trimmed.StartsWith(prefix, StringComparison.Ordinal) &&
                    seen.Add(line))
                    result.Add(line);
            }
        }
        return [.. result];
    }
}
