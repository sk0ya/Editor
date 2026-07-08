using Editor.Core.Buffer;
using Editor.Core.Models;

namespace Editor.Core.Engine.Commands;

/// <summary>
/// Handles the fold-management Normal mode commands (za/zo/zc/zM/zR/zf/zj/zk/
/// [z/]z/zd/zD/zE/zn/zN). Computes buffer mutations and emits FoldsChanged, but
/// leaves cursor application to the caller since VimEngine applies fold-clamped
/// cursors and fold-jump cursors through two different existing code paths
/// (direct clamp-to-visible vs. the normal MoveCursor bounds-clamp).
/// </summary>
public class FoldCommands(BufferManager bufferManager)
{
    public readonly record struct Result(CursorPosition? DirectCursor, CursorPosition? MoveCursor, bool? FoldDisabled);

    public bool TryHandle(string cmd, CursorPosition cursor, int count, List<VimEvent> events, out Result result)
    {
        var buffer = bufferManager.Current;
        var folds = buffer.Folds;

        switch (cmd)
        {
            case "za":
                folds.ToggleFold(cursor.Line);
                events.Add(VimEvent.FoldsChanged());
                result = new Result(ClampCursorToVisible(buffer, cursor), null, null);
                return true;
            case "zo":
                folds.OpenFold(cursor.Line);
                events.Add(VimEvent.FoldsChanged());
                result = default;
                return true;
            case "zc":
                folds.CloseFold(cursor.Line);
                events.Add(VimEvent.FoldsChanged());
                result = new Result(ClampCursorToVisible(buffer, cursor), null, null);
                return true;
            case "zM":
                folds.CloseAll();
                events.Add(VimEvent.FoldsChanged());
                result = new Result(ClampCursorToVisible(buffer, cursor), null, null);
                return true;
            case "zR":
                folds.OpenAll();
                events.Add(VimEvent.FoldsChanged());
                result = default;
                return true;
            case "zf":
            {
                int foldEnd = Math.Min(buffer.Text.LineCount - 1, cursor.Line + count - 1);
                if (foldEnd > cursor.Line)
                {
                    folds.CreateFold(cursor.Line, foldEnd);
                    events.Add(VimEvent.FoldsChanged());
                }
                result = default;
                return true;
            }
            case "zj":
            {
                int next = folds.NextFoldStart(cursor.Line);
                result = new Result(null, next >= 0 ? new CursorPosition(next, 0) : null, null);
                return true;
            }
            case "zk":
            {
                int prev = folds.PrevFoldStart(cursor.Line);
                result = new Result(null, prev >= 0 ? new CursorPosition(prev, 0) : null, null);
                return true;
            }
            case "[z":
            {
                int start = folds.CurrentFoldStart(cursor.Line);
                result = new Result(null, start >= 0 ? new CursorPosition(start, 0) : null, null);
                return true;
            }
            case "]z":
            {
                int end = folds.CurrentFoldEnd(cursor.Line);
                result = new Result(null, end >= 0 ? new CursorPosition(end, 0) : null, null);
                return true;
            }
            case "zd":
                folds.DeleteFold(cursor.Line);
                events.Add(VimEvent.FoldsChanged());
                result = default;
                return true;
            case "zD":
                folds.DeleteFoldsAt(cursor.Line);
                events.Add(VimEvent.FoldsChanged());
                result = default;
                return true;
            case "zE":
                folds.Clear();
                events.Add(VimEvent.FoldsChanged());
                result = default;
                return true;
            case "zn":
                events.Add(VimEvent.FoldsChanged());
                result = new Result(null, null, true);
                return true;
            case "zN":
                events.Add(VimEvent.FoldsChanged());
                result = new Result(null, null, false);
                return true;
            default:
                result = default;
                return false;
        }
    }

    private static CursorPosition ClampCursorToVisible(VimBuffer buffer, CursorPosition cursor)
    {
        var hiding = buffer.Folds.GetHidingFold(cursor.Line);
        if (hiding.HasValue)
        {
            int foldStart = hiding.Value.StartLine;
            int maxCol = Math.Max(0, buffer.Text.GetLineLength(foldStart) - 1);
            return new CursorPosition(foldStart, Math.Min(cursor.Column, maxCol));
        }
        return cursor;
    }
}
