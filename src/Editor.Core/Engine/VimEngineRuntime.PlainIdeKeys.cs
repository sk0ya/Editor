using Editor.Core.Buffer;
using Editor.Core.Models;

namespace Editor.Core.Engine;

/// <summary>
/// Vim 無効時（plain モード）の IDE 標準キー。Vim を切った人が Rider／Visual Studio と同じ手癖で
/// 書けるようにする（Loomo 設計書 §36）。Vim 有効時の入力経路からは一切呼ばれない。
///
/// <para>本文を変える操作はどれも「その操作だけで 1 回の Undo」にする——連続した文字入力の塊
/// （<see cref="BeginPlainEdit"/> の run）へ混ぜると、Ctrl+D の複製を戻したつもりが直前の入力まで消える。</para>
/// </summary>
internal sealed partial class VimEngineRuntime
{
    /// <summary>IDE 標準キーなら処理して true。該当しなければ何もせず false（従来の plain 処理へ）。</summary>
    private bool TryProcessPlainIdeKey(
        string key, bool ctrl, bool shift, bool alt, bool hasSelection, List<VimEvent> events)
    {
        var buf = _bufferManager.Current.Text;

        if (alt && !ctrl)
        {
            // Alt+Shift+↑↓：行（選択があれば選択の行）を上下へ移動する。
            if (shift && key is "Up" or "Down")
            {
                MovePlainLines(key == "Up" ? -1 : 1, events);
                return true;
            }
            return false;
        }

        if (ctrl && alt)
        {
            // Ctrl+Alt+Enter：上に新しい行を作る（Rider の Start New Line Before Current）。
            if (key == "Return")
            {
                StartPlainLine(above: true, events);
                return true;
            }
            return false;
        }

        if (ctrl)
        {
            switch (key.ToLowerInvariant())
            {
                case "a":
                    _plainEditRunActive = false;
                    _plainSelAnchor = new CursorPosition(0, 0);
                    _plainSelActive = true;
                    var last = buf.LineCount - 1;
                    UpdatePlainSelection(new CursorPosition(last, buf.GetLineLength(last)), events);
                    SetPreferredColumn(_cursor.Column);
                    return true;
                case "left":
                    PlainMoveCaret(PlainWordNavigation.Previous(buf, _cursor), shift, events);
                    return true;
                case "right":
                    PlainMoveCaret(PlainWordNavigation.Next(buf, _cursor), shift, events);
                    return true;
                case "home":
                    PlainMoveCaret(new CursorPosition(0, 0), shift, events);
                    return true;
                case "end":
                    var lastLine = buf.LineCount - 1;
                    PlainMoveCaret(new CursorPosition(lastLine, buf.GetLineLength(lastLine)), shift, events);
                    return true;
                case "back":
                    DeletePlainWord(forward: false, hasSelection, events);
                    return true;
                case "delete":
                    DeletePlainWord(forward: true, hasSelection, events);
                    return true;
                case "d":
                    DuplicatePlain(events);
                    return true;
                case "/":
                    TogglePlainComment(events);
                    return true;
            }
            return false;
        }

        switch (key)
        {
            case "Return" when shift:
                // Shift+Enter：行のどこにいても、下に新しい行を作る（Rider の Start New Line）。
                StartPlainLine(above: false, events);
                return true;
            case "PageUp":
            case "PageDown":
                var page = Math.Max(1, _viewportVisibleLines - 1);
                PlainMoveVertical(key == "PageUp" ? -page : page, shift, events);
                return true;
        }
        return false;
    }

    /// <summary>Home の行き先。行頭の空白の直後（最初の非空白）と行頭 0 桁を往復する（VS／Rider と同じ）。</summary>
    internal static int SmartHomeColumn(string line, int column)
    {
        var firstNonBlank = 0;
        while (firstNonBlank < line.Length && line[firstNonBlank] is ' ' or '\t')
            firstNonBlank++;
        if (firstNonBlank == line.Length)
            return 0;
        return column == firstNonBlank ? 0 : firstNonBlank;
    }

    /// <summary>この操作だけで 1 回の Undo になるよう、前の入力の塊を閉じてから撮る。</summary>
    private void BeginPlainCommand()
    {
        _plainEditRunActive = false;
        BeginPlainEdit();
        _plainEditRunActive = false;
    }

    /// <summary>選択が触れている行の範囲（選択が次の行の 0 桁で終わるなら、その行は含めない）。
    /// 選択が無ければキャレット行だけ。</summary>
    private (int Start, int End) PlainSelectedLines()
    {
        if (!_plainSelActive || _plainSelAnchor == _cursor)
            return (_cursor.Line, _cursor.Line);
        var (low, high) = ComparePos(_plainSelAnchor, _cursor) <= 0
            ? (_plainSelAnchor, _cursor)
            : (_cursor, _plainSelAnchor);
        var end = high.Column == 0 && high.Line > low.Line ? high.Line - 1 : high.Line;
        return (low.Line, end);
    }

    private void CutCurrentPlainLine(List<VimEvent> events)
    {
        var buf = _bufferManager.Current.Text;
        CopyCurrentLineToClipboard();
        BeginPlainCommand();
        if (buf.LineCount == 1)
        {
            buf.DeleteRange(0, 0, buf.GetLineLength(0));
            _cursor = new CursorPosition(0, 0);
        }
        else
        {
            var line = _cursor.Line;
            buf.DeleteLines(line, line);
            var target = Math.Min(line, buf.LineCount - 1);
            _cursor = new CursorPosition(target, Math.Min(_cursor.Column, buf.GetLineLength(target)));
        }
        ClearPlainSelection(events);
        EmitText(events);
    }

    private void DeletePlainWord(bool forward, bool hasSelection, List<VimEvent> events)
    {
        var buf = _bufferManager.Current.Text;
        BeginPlainCommand();
        if (hasSelection)
        {
            DeletePlainSelection(events);
            EmitText(events);
            return;
        }

        var target = forward ? PlainWordNavigation.Next(buf, _cursor) : PlainWordNavigation.Previous(buf, _cursor);
        if (target == _cursor)
            return;
        var (origin, to) = forward ? (_cursor, target) : (target, _cursor);
        if (origin.Line == to.Line)
        {
            buf.DeleteRange(origin.Line, origin.Column, to.Column);
        }
        else
        {
            // 行をまたぐのは「行末で Ctrl+Delete」「行頭で Ctrl+Backspace」だけ＝改行 1 つを消す。
            buf.JoinLines(origin.Line);
        }
        _cursor = origin;
        EmitText(events);
    }

    private void DuplicatePlain(List<VimEvent> events)
    {
        var buf = _bufferManager.Current.Text;
        BeginPlainCommand();
        if (PlainSelectionRange() is not null)
        {
            // 選択の直後へ選択と同じ文字列を足し、足した側を選択し直す（続けて押すと増え続ける）。
            var (low, high) = ComparePos(_plainSelAnchor, _cursor) <= 0
                ? (_plainSelAnchor, _cursor)
                : (_cursor, _plainSelAnchor);
            var text = PlainText(buf, low, high);
            InsertPlainText(buf, high, text);
            var end = AdvancePosition(high, text);
            _plainSelAnchor = high;
            _plainSelActive = true;
            EmitText(events);
            UpdatePlainSelection(end, events);
            return;
        }

        var line = _cursor.Line;
        buf.InsertLines(line, [buf.GetLine(line)]);
        _cursor = new CursorPosition(line + 1, _cursor.Column);
        EmitText(events);
    }

    private void MovePlainLines(int delta, List<VimEvent> events)
    {
        var buf = _bufferManager.Current.Text;
        var (start, end) = PlainSelectedLines();
        if (delta < 0 && start == 0 || delta > 0 && end >= buf.LineCount - 1)
            return;

        BeginPlainCommand();
        if (delta < 0)
        {
            var above = buf.GetLine(start - 1);
            buf.DeleteLines(start - 1, start - 1);
            buf.InsertLines(end - 1, [above]);
        }
        else
        {
            var below = buf.GetLine(end + 1);
            buf.DeleteLines(end + 1, end + 1);
            buf.InsertLineAbove(start, below);
        }

        var hadSelection = _plainSelActive && _plainSelAnchor != _cursor;
        _cursor = _cursor with { Line = _cursor.Line + delta };
        EmitText(events);
        if (hadSelection)
        {
            _plainSelAnchor = _plainSelAnchor with { Line = _plainSelAnchor.Line + delta };
            UpdatePlainSelection(_cursor, events);
        }
    }

    private void StartPlainLine(bool above, List<VimEvent> events)
    {
        var buf = _bufferManager.Current.Text;
        BeginPlainCommand();
        ClearPlainSelection(events);
        if (above)
        {
            var current = buf.GetLine(_cursor.Line);
            var indent = current[..(current.Length - current.TrimStart(' ', '\t').Length)];
            buf.InsertLineAbove(_cursor.Line, indent);
            _cursor = new CursorPosition(_cursor.Line, indent.Length);
            EmitText(events);
            return;
        }

        _cursor = _cursor with { Column = buf.GetLineLength(_cursor.Line) };
        InsertNewline(events);
    }

    private void TogglePlainComment(List<VimEvent> events)
    {
        var buf = _bufferManager.Current.Text;
        var (start, end) = PlainSelectedLines();
        var before = buf.GetLineLength(_cursor.Line);
        var anchorBefore = _plainSelActive ? buf.GetLineLength(_plainSelAnchor.Line) : 0;

        // ToggleCommentLines は自分で撮る。入力の塊を閉じておけば、それが単独の Undo になる。
        _plainEditRunActive = false;
        _textTransform.ToggleCommentLines(start, end, events);

        var delta = buf.GetLineLength(_cursor.Line) - before;
        _cursor = buf.ClampCursor(_cursor with { Column = Math.Max(0, _cursor.Column + delta) }, insertMode: true);
        if (_plainSelActive && _plainSelAnchor != _cursor)
        {
            var anchorDelta = buf.GetLineLength(_plainSelAnchor.Line) - anchorBefore;
            if (_plainSelAnchor.Column > 0)
                _plainSelAnchor = buf.ClampCursor(
                    _plainSelAnchor with { Column = Math.Max(0, _plainSelAnchor.Column + anchorDelta) }, insertMode: true);
            UpdatePlainSelection(_cursor, events);
        }
        else
        {
            EmitCursor(events);
        }
    }

    private static string PlainText(TextBuffer buf, CursorPosition origin, CursorPosition to)
    {
        if (origin.Line == to.Line)
            return buf.GetLine(origin.Line)[origin.Column..to.Column];
        var parts = new List<string> { buf.GetLine(origin.Line)[origin.Column..] };
        for (var line = origin.Line + 1; line < to.Line; line++)
            parts.Add(buf.GetLine(line));
        parts.Add(buf.GetLine(to.Line)[..to.Column]);
        return string.Join('\n', parts);
    }

    private static void InsertPlainText(TextBuffer buf, CursorPosition at, string text)
        => buf.InsertText(at.Line, at.Column, text);

    private static CursorPosition AdvancePosition(CursorPosition origin, string text)
    {
        var newline = text.LastIndexOf('\n');
        if (newline < 0)
            return origin with { Column = origin.Column + text.Length };
        var lines = text.Count(ch => ch == '\n');
        return new CursorPosition(origin.Line + lines, text.Length - newline - 1);
    }
}

/// <summary>plain モードの単語単位の移動先（Ctrl+←→・Ctrl+Backspace/Delete）。
/// 語（英数字と _、CJK を含む文字）・記号の並び・空白を別の塊として扱う——VS／Rider と同じく、
/// 右へは「今の塊の終わり→続く空白の終わり」、左へは「手前の空白→その前の塊の始まり」。</summary>
internal static class PlainWordNavigation
{
    private enum CharClass { Space, Word, Symbol }

    private static CharClass Classify(char ch)
        => char.IsWhiteSpace(ch) ? CharClass.Space
            : char.IsLetterOrDigit(ch) || ch == '_' ? CharClass.Word
            : CharClass.Symbol;

    public static CursorPosition Next(TextBuffer buf, CursorPosition origin)
    {
        var line = buf.GetLine(origin.Line);
        if (origin.Column >= line.Length)
            return origin.Line < buf.LineCount - 1 ? new CursorPosition(origin.Line + 1, 0) : origin;

        var col = origin.Column;
        var cls = Classify(line[col]);
        if (cls != CharClass.Space)
            while (col < line.Length && Classify(line[col]) == cls)
                col++;
        while (col < line.Length && Classify(line[col]) == CharClass.Space)
            col++;
        return origin with { Column = col };
    }

    public static CursorPosition Previous(TextBuffer buf, CursorPosition origin)
    {
        if (origin.Column == 0)
            return origin.Line > 0 ? new CursorPosition(origin.Line - 1, buf.GetLineLength(origin.Line - 1)) : origin;

        var line = buf.GetLine(origin.Line);
        var col = Math.Min(origin.Column, line.Length);
        while (col > 0 && Classify(line[col - 1]) == CharClass.Space)
            col--;
        if (col == 0)
            return origin with { Column = 0 };
        var cls = Classify(line[col - 1]);
        while (col > 0 && Classify(line[col - 1]) == cls)
            col--;
        return origin with { Column = col };
    }
}
