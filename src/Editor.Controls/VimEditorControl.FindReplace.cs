using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Editor.Core.Models;

namespace Editor.Controls;

/// <summary>VSCode-style Find/Replace bar: Ctrl+F / Ctrl+H (see the interception in
/// OnPreviewKeyDown), independent of Vim mode/VimEnabled. Reuses the same search-highlight
/// rendering path as Vim's own hlsearch (EditorCanvas.SetSearchMatches).</summary>
public partial class VimEditorControl
{
    private bool _findBarVisible;
    private bool _findMatchCase;
    private List<CursorPosition> _findMatches = [];
    private int _findActiveMatchIndex = -1;

    private void ShowFindReplace(bool withReplace)
    {
        if (!_findBarVisible && FindSearchBox.Text.Length == 0)
        {
            var word = GetWordAtCursor();
            if (word.Length > 0) FindSearchBox.Text = word;
        }

        _findBarVisible = true;
        FindReplaceBar.Visibility = Visibility.Visible;
        ReplaceRow.Visibility = withReplace ? Visibility.Visible : Visibility.Collapsed;
        FindReplaceToggleBtn.Content = withReplace ? "▾" : "▸";
        ApplyFindReplaceBarTheme(); // in case the theme changed while the bar was hidden

        RecomputeFindMatches(jumpToNearest: true);

        FindSearchBox.Focus();
        FindSearchBox.SelectAll();
    }

    /// <summary>Paints the Find/Replace bar from the active EditorTheme. Called on open and
    /// whenever the theme changes (see ApplyTheme in VimEditorControl.xaml.cs).</summary>
    private void ApplyFindReplaceBarTheme()
    {
        var shellBg = _theme.LineNumberBg;
        var fieldBg = _theme.Background;
        var border = _theme.IndentGuideBrush;
        var fg = _theme.Foreground;
        var accent = _theme.LinkColor;

        FindReplaceBar.Background = shellBg;
        FindReplaceBar.BorderBrush = border;

        foreach (var box in new[] { FindSearchBoxBorder, FindReplaceBoxBorder })
        {
            box.Background = fieldBg;
            box.BorderBrush = border;
        }
        foreach (var tb in new[] { FindSearchBox, FindReplaceBox })
        {
            tb.Foreground = fg;
            tb.CaretBrush = fg;
            tb.SelectionBrush = Translucent(accent, 0x55);
        }

        foreach (var btn in new ButtonBase[] { FindReplaceToggleBtn, FindPrevBtn, FindNextBtn, FindCloseBtn })
            btn.Foreground = fg;

        FindCaseBtn.Background = _findMatchCase ? accent : Brushes.Transparent;
        FindCaseBtn.Foreground = _findMatchCase ? fieldBg : fg;

        foreach (var btn in new[] { ReplaceOneBtn, ReplaceAllBtn })
        {
            btn.Background = _theme.CurrentLineBg;
            btn.Foreground = fg;
        }

        UpdateFindMatchCountText(); // re-color the match-count label against the (possibly new) theme
    }

    private static Brush Translucent(Brush brush, byte alpha) =>
        brush is SolidColorBrush { Color: var c } ? new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B)) : brush;

    private void HideFindReplace()
    {
        _findBarVisible = false;
        FindReplaceBar.Visibility = Visibility.Collapsed;
        _findMatches = [];
        _findActiveMatchIndex = -1;
        UpdateSearchHighlights(_engine.SearchPattern); // restore Vim's own hlsearch highlight, if any
        Canvas.Focus();
    }

    private void FindReplaceToggleBtn_Click(object sender, RoutedEventArgs e)
    {
        bool show = ReplaceRow.Visibility != Visibility.Visible;
        ReplaceRow.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        FindReplaceToggleBtn.Content = show ? "▾" : "▸";
        FindSearchBox.Focus();
    }

    private void FindCloseBtn_Click(object sender, RoutedEventArgs e) => HideFindReplace();

    private void FindCaseBtn_Click(object sender, RoutedEventArgs e)
    {
        _findMatchCase = FindCaseBtn.IsChecked == true;
        FindCaseBtn.Background = _findMatchCase ? _theme.LinkColor : Brushes.Transparent;
        FindCaseBtn.Foreground = _findMatchCase ? _theme.Background : _theme.Foreground;
        RecomputeFindMatches(jumpToNearest: false);
        FindSearchBox.Focus();
    }

    private void FindSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        => RecomputeFindMatches(jumpToNearest: true);

    private void FindSearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                HideFindReplace();
                e.Handled = true;
                break;
            case Key.Enter:
                FindStep((Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? -1 : 1);
                e.Handled = true;
                break;
        }
    }

    private void FindReplaceBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                HideFindReplace();
                e.Handled = true;
                break;
            case Key.Enter:
                ReplaceOneBtn_Click(sender, e);
                e.Handled = true;
                break;
        }
    }

    private void FindNextBtn_Click(object sender, RoutedEventArgs e) => FindStep(1);
    private void FindPrevBtn_Click(object sender, RoutedEventArgs e) => FindStep(-1);

    private void FindStep(int direction)
    {
        if (_findMatches.Count == 0)
        {
            RecomputeFindMatches(jumpToNearest: true);
            FindSearchBox.Focus();
            return;
        }
        _findActiveMatchIndex = ((_findActiveMatchIndex + direction) % _findMatches.Count + _findMatches.Count) % _findMatches.Count;
        JumpToActiveMatch();
        FindSearchBox.Focus();
    }

    private void RecomputeFindMatches(bool jumpToNearest)
    {
        var pattern = FindSearchBox.Text;
        var buf = _engine.CurrentBuffer.Text;

        _findMatches = pattern.Length == 0 ? [] : buf.FindAll(pattern, ignoreCase: !_findMatchCase);
        Canvas.SetSearchMatches(_findMatches, pattern);

        if (_findMatches.Count == 0)
        {
            _findActiveMatchIndex = -1;
        }
        else if (jumpToNearest)
        {
            var cursor = _engine.Cursor;
            _findActiveMatchIndex = _findMatches.FindIndex(m =>
                m.Line > cursor.Line || (m.Line == cursor.Line && m.Column >= cursor.Column));
            if (_findActiveMatchIndex < 0) _findActiveMatchIndex = 0;
            JumpToActiveMatch();
        }
        else
        {
            _findActiveMatchIndex = Math.Clamp(_findActiveMatchIndex, 0, _findMatches.Count - 1);
        }

        UpdateFindMatchCountText();
    }

    private void UpdateFindMatchCountText()
    {
        bool hasResults = _findMatches.Count > 0;
        FindMatchCountText.Text = hasResults ? $"{_findActiveMatchIndex + 1} of {_findMatches.Count}" : "No results";
        FindMatchCountText.Foreground = hasResults ? _theme.LinkColor : _theme.LineNumberFg;
    }

    private void JumpToActiveMatch()
    {
        if (_findActiveMatchIndex < 0 || _findActiveMatchIndex >= _findMatches.Count) return;
        ProcessVimEvents(_engine.SetCursorPosition(_findMatches[_findActiveMatchIndex]));
        UpdateFindMatchCountText();
    }

    private void ReplaceOneBtn_Click(object sender, RoutedEventArgs e)
    {
        var pattern = FindSearchBox.Text;
        if (pattern.Length == 0 || _findActiveMatchIndex < 0 || _findActiveMatchIndex >= _findMatches.Count)
            return;

        var replacement = FindReplaceBox.Text;
        var match = _findMatches[_findActiveMatchIndex];
        var buf = _engine.CurrentBuffer.Text;
        var preLines = buf.Snapshot();
        var preCursor = _engine.Cursor;

        var line = buf.GetLine(match.Line);
        buf.ReplaceLine(match.Line, line[..match.Column] + replacement + line[(match.Column + pattern.Length)..]);

        _engine.CurrentBuffer.Undo.Snapshot(preLines, preCursor);
        UpdateAll();
        _lspManager.OnTextChanged(buf.GetText());
        BufferChanged?.Invoke(this, EventArgs.Empty);

        RecomputeFindMatches(jumpToNearest: true);
        FindSearchBox.Focus();
    }

    private void ReplaceAllBtn_Click(object sender, RoutedEventArgs e)
    {
        var pattern = FindSearchBox.Text;
        if (pattern.Length == 0) return;

        var replacement = FindReplaceBox.Text;
        var buf = _engine.CurrentBuffer.Text;
        var comparison = _findMatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var preLines = buf.Snapshot();
        var preCursor = _engine.Cursor;
        int count = 0;

        for (int l = 0; l < buf.LineCount; l++)
        {
            var line = buf.GetLine(l);
            if (line.IndexOf(pattern, comparison) < 0) continue;

            var sb = new StringBuilder();
            int from = 0, idx;
            while ((idx = line.IndexOf(pattern, from, comparison)) >= 0)
            {
                sb.Append(line, from, idx - from).Append(replacement);
                from = idx + pattern.Length;
                count++;
            }
            sb.Append(line, from, line.Length - from);
            buf.ReplaceLine(l, sb.ToString());
        }

        if (count == 0) return;

        _engine.CurrentBuffer.Undo.Snapshot(preLines, preCursor);
        UpdateAll();
        _lspManager.OnTextChanged(buf.GetText());
        BufferChanged?.Invoke(this, EventArgs.Empty);
        ActiveStatusBar.UpdateStatus($"Replaced {count} occurrence(s)");

        RecomputeFindMatches(jumpToNearest: false);
        FindSearchBox.Focus();
    }
}
