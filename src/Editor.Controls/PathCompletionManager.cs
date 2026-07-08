using System.IO;
using System.Linq;
using Editor.Controls.Lsp;
using Editor.Controls.Rendering;
using Editor.Core.Engine;
using Editor.Core.Lsp;

namespace Editor.Controls;

/// <summary>
/// Owns filesystem path completion (Insert-mode, LSP-independent) for a single
/// <see cref="VimEditorControl"/>: recognizing a path-like token before the cursor, listing
/// matching directory entries, and driving the shared completion popup on
/// <see cref="EditorCanvas"/>. Composed the same way as <c>MultiCursorManager</c>/
/// <c>SnippetTabStopManager</c> — the control owns an instance and passes the WPF-side
/// callback (key replay) it needs to insert an accepted item via the existing Insert-mode path.
/// </summary>
public sealed class PathCompletionManager(
    VimEngine engine,
    EditorCanvas canvas,
    IEditorLspManager lspManager,
    Action<string, bool, bool, bool> processKey)
{
    private List<LspCompletionItem> _items = [];
    private int _selection = -1;
    private int _scroll;
    private int _replaceStart;   // column where the editable filename segment begins
    private bool _visible;
    private bool _suppress;      // guard while programmatically inserting an accepted item

    public bool Visible => _visible;
    /// <summary>True while an accepted item is being inserted programmatically — callers should drive nothing else.</summary>
    public bool Suppressed => _suppress;

    public static bool IsPathDelimiter(char c) =>
        c is ' ' or '\t' or '"' or '\'' or '`' or '(' or ')'
          or '<' or '>' or '=' or ',' or ';' or '|' or '*' or '?';

    /// <summary>
    /// Recompute path completion from the token before the cursor. On auto-trigger
    /// the popup is shown only once the token contains a path separator (so plain
    /// identifiers don't trigger it); a <paramref name="forced"/> invocation
    /// (Ctrl+Space) treats the bare token as a path relative to the base directory.
    /// Returns true when the path popup is active afterwards.
    /// </summary>
    public bool Update(bool forced = false)
    {
        if (engine.Mode != VimMode.Insert || !engine.VimEnabled)
        {
            Hide();
            return false;
        }

        var cursor = engine.Cursor;
        var line = engine.CurrentBuffer.Text.GetLine(cursor.Line);
        int col = Math.Min(cursor.Column, line.Length);

        int start = col;
        while (start > 0 && !IsPathDelimiter(line[start - 1]))
            start--;
        string token = line[start..col];   // text before the cursor — used as the prefix

        // Whether this is a path context is decided by the WHOLE token surrounding
        // the cursor (scan forward over the rest of it too), not just the part before
        // the cursor. Otherwise placing the cursor just before the '/' — e.g. after
        // Esc moves it left onto the separator and `i` re-enters there — would drop
        // the separator from the prefix and wrongly look like a plain identifier.
        int end = col;
        while (end < line.Length && !IsPathDelimiter(line[end]))
            end++;
        string fullToken = line[start..end];

        // Auto-trigger only when the surrounding token contains a separator; a forced
        // (Ctrl+Space) invocation lists the base directory even for a bare token.
        if (!forced && (fullToken.Length == 0 || (fullToken.IndexOf('/') < 0 && fullToken.IndexOf('\\') < 0)))
        {
            Hide();
            return false;
        }

        var items = BuildCompletions(token, out int filePartLen);
        if (items.Count == 0)
        {
            Hide();
            return false;
        }

        _items = items;
        _replaceStart = col - filePartLen;
        _selection = 0;
        _scroll = 0;
        _visible = true;
        canvas.SetCompletionItems(_items, _selection, _scroll);
        return true;
    }

    /// <summary>Build directory/file completions for the given path token.</summary>
    private List<LspCompletionItem> BuildCompletions(string token, out int filePartLen)
    {
        var items = new List<LspCompletionItem>();

        int sep = token.LastIndexOfAny(['/', '\\']);
        string dirPart = sep >= 0 ? token[..sep] : "";
        string filePart = sep >= 0 ? token[(sep + 1)..] : token;
        filePartLen = filePart.Length;

        // Match the separator style the user is typing: append '\' to directories
        // when the token already uses backslashes, otherwise '/'.
        char sepChar = sep >= 0 ? token[sep] : (token.IndexOf('\\') >= 0 ? '\\' : '/');

        // Base directory: the folder of the current file, else the working directory.
        string? currentFile = engine.CurrentBuffer.FilePath;
        string baseDir = currentFile != null
            ? (Path.GetDirectoryName(currentFile) ?? Directory.GetCurrentDirectory())
            : Directory.GetCurrentDirectory();

        string searchDir;
        if (dirPart == "~" || dirPart.StartsWith("~/", StringComparison.Ordinal) || dirPart.StartsWith("~\\", StringComparison.Ordinal))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var rest = dirPart.Length > 2 ? dirPart[2..] : "";
            searchDir = rest.Length > 0 ? Path.Combine(home, rest) : home;
        }
        else if (dirPart.Length == 2 && char.IsLetter(dirPart[0]) && dirPart[1] == ':')
            // Drive spec without a trailing separator ("C:") means the drive ROOT
            // here, not the drive's current directory — normalize to "C:\".
            searchDir = dirPart + Path.DirectorySeparatorChar;
        else if (Path.IsPathRooted(dirPart))
            searchDir = dirPart;
        else
            searchDir = Path.Combine(baseDir, dirPart);

        const int maxItems = 200;
        try
        {
            if (!Directory.Exists(searchDir)) return items;

            foreach (var dir in Directory.EnumerateDirectories(searchDir)
                         .Where(d => Path.GetFileName(d).StartsWith(filePart, StringComparison.OrdinalIgnoreCase))
                         .OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase))
            {
                items.Add(new LspCompletionItem(Path.GetFileName(dir) + sepChar, CompletionItemKind.Module));
                if (items.Count >= maxItems) return items;
            }
            foreach (var file in Directory.EnumerateFiles(searchDir)
                         .Where(f => Path.GetFileName(f).StartsWith(filePart, StringComparison.OrdinalIgnoreCase))
                         .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase))
            {
                items.Add(new LspCompletionItem(Path.GetFileName(file), CompletionItemKind.File));
                if (items.Count >= maxItems) return items;
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
        catch (ArgumentException) { }

        return items;
    }

    public void MoveSelection(int delta)
    {
        if (!_visible || _items.Count == 0) return;
        int n = _items.Count;
        _selection = (_selection + delta + n) % n;

        const int maxVisible = 10; // matches DrawCompletionPopup
        if (_selection < _scroll)
            _scroll = _selection;
        else if (_selection >= _scroll + maxVisible)
            _scroll = _selection - maxVisible + 1;

        canvas.SetCompletionItems(_items, _selection, _scroll);
    }

    public void Insert()
    {
        if (!_visible || _selection < 0 || _selection >= _items.Count) return;

        var label = _items[_selection].Label;
        int col = engine.Cursor.Column;
        int deleteCount = Math.Max(0, col - _replaceStart);

        Hide();

        _suppress = true;
        try
        {
            for (int i = 0; i < deleteCount; i++)
                processKey("Back", false, false, false);
            foreach (var ch in label)
                processKey(ch.ToString(), false, false, false);
        }
        finally
        {
            _suppress = false;
        }

        // Completing a directory descends into it; offer its contents immediately.
        if (label.EndsWith('/') || label.EndsWith('\\'))
            Update();
    }

    public void Hide()
    {
        if (!_visible) return;
        _visible = false;
        _items = [];
        _selection = -1;
        _scroll = 0;
        // Hand the popup back to the LSP layer (usually nothing to show).
        canvas.SetCompletionItems(lspManager.CompletionItems, lspManager.CompletionSelection, lspManager.CompletionScrollOffset);
    }
}
