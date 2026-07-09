using Editor.Core.Buffer;
using Editor.Core.Macros;
using Editor.Core.Models;

namespace Editor.Core.Engine;

/// <summary>
/// Implements <c>:[range]normal[!] {commands}</c> — replaying a normal-mode key
/// sequence on every line in the range as a single undo step. The engine re-entry
/// points it drives (stroke dispatch, mode/cursor/parser access, emit) are taken as
/// callbacks so it holds no engine state of its own.
/// </summary>
public sealed class NormalCommandExecutor(
    BufferManager bufferManager,
    ExCommandProcessor exProcessor,
    CommandParser commandParser,
    Func<CursorPosition> getCursor,
    Action<CursorPosition> setCursor,
    Func<VimMode> getMode,
    Action<VimMode> setMode,
    Action<bool> setSuppressSnapshot,
    Action<VimKeyStroke, List<VimEvent>, bool> processStroke,
    Action<string, bool, bool, bool, List<VimEvent>> processKeyInternal,
    Action<List<VimEvent>> emitText,
    Action<List<VimEvent>> emitCursor)
{
    private VimBuffer CurrentBuffer => bufferManager.Current;

    /// <summary>
    /// Handles :[range]normal[!] {commands}. Returns true if the command line was a
    /// :normal command (consumed), false otherwise.
    /// </summary>
    public bool TryExecute(string cmdLine, List<VimEvent> events)
    {
        var trimmed = cmdLine.Trim();

        // Parse optional range prefix (%, ., number, number,number)
        string range = "";
        int pos = 0;
        if (trimmed.StartsWith('%')) { range = "%"; pos = 1; }
        else if (trimmed.StartsWith('.')) { range = "."; pos = 1; }
        else if (pos < trimmed.Length && char.IsDigit(trimmed[pos]))
        {
            while (pos < trimmed.Length && (char.IsDigit(trimmed[pos]) || trimmed[pos] == ','))
                pos++;
            range = trimmed[..pos];
        }

        var rest = trimmed[pos..].TrimStart();

        // Detect "normal" or "norm" keyword
        int keywordLen;
        if (rest.StartsWith("normal", StringComparison.OrdinalIgnoreCase) &&
            (rest.Length == 6 || rest[6] is ' ' or '!'))
            keywordLen = 6;
        else if (rest.StartsWith("norm", StringComparison.OrdinalIgnoreCase) &&
                 (rest.Length == 4 || rest[4] is ' ' or '!'))
            keywordLen = 4;
        else
            return false;

        rest = rest[keywordLen..];
        bool ignoreMapping = rest.StartsWith('!');
        if (ignoreMapping) rest = rest[1..];

        // Require a space followed by at least one command character
        if (rest.Length == 0 || rest[0] != ' ') return false;
        var commands = rest[1..];
        if (string.IsNullOrEmpty(commands)) return false;

        var strokes = ParseNormalKeySequence(commands);

        // Resolve line range
        var buf = CurrentBuffer.Text;
        var cursor = getCursor();
        int startLine = cursor.Line, endLine = cursor.Line;
        exProcessor.ResolveRange(range, cursor, buf.LineCount, ref startLine, ref endLine);
        startLine = Math.Clamp(startLine, 0, buf.LineCount - 1);
        endLine = Math.Clamp(endLine, 0, buf.LineCount - 1);

        // Snapshot before mutations for undo; suppress inner Snapshot() calls so the
        // entire :normal range executes as a single undo record.
        var preLines = buf.Snapshot();
        var preCursor = cursor;

        setMode(VimMode.Normal);
        commandParser.Reset();
        setSuppressSnapshot(true);
        var innerEvents = new List<VimEvent>();
        bool anyChange = false;

        try
        {
            for (int l = startLine; l <= endLine; l++)
            {
                if (l >= CurrentBuffer.Text.LineCount) break;
                setCursor(new CursorPosition(l, 0));

                foreach (var stroke in strokes)
                {
                    innerEvents.Clear();
                    processStroke(stroke, innerEvents, !ignoreMapping);
                    if (!anyChange)
                        foreach (var ev in innerEvents)
                            if (ev.Type == VimEventType.TextChanged) { anyChange = true; break; }
                }

                // Implicitly exit Insert/Visual mode after command sequence (like Vim does)
                if (getMode() != VimMode.Normal)
                {
                    innerEvents.Clear();
                    processKeyInternal("Escape", false, false, false, innerEvents);
                }
            }
        }
        finally
        {
            setSuppressSnapshot(false);
        }

        if (anyChange)
        {
            CurrentBuffer.Undo.Snapshot(preLines, preCursor);
            emitText(events);
        }
        else
        {
            emitCursor(events);
        }

        return true;
    }

    private static List<VimKeyStroke> ParseNormalKeySequence(string seq)
    {
        var strokes = new List<VimKeyStroke>();
        int i = 0;
        while (i < seq.Length)
        {
            if (seq[i] == '<')
            {
                var close = seq.IndexOf('>', i + 1);
                if (close > i)
                {
                    var keyName = seq[(i + 1)..close];
                    strokes.Add(new VimKeyStroke(MapNormalSpecialKey(keyName), false, false, false));
                    i = close + 1;
                    continue;
                }
            }
            strokes.Add(new VimKeyStroke(seq[i].ToString(), false, false, false));
            i++;
        }
        return strokes;
    }

    private static string MapNormalSpecialKey(string name) => name.ToUpperInvariant() switch
    {
        "ESC" or "ESCAPE" => "Escape",
        "CR" or "ENTER" or "RETURN" => "Return",
        "BS" or "BACKSPACE" => "Back",
        "TAB" => "Tab",
        "SPACE" => " ",
        "DEL" or "DELETE" => "Delete",
        _ => name
    };
}
