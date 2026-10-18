using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Editor.Controls.Git;

namespace Editor.Controls;

/// <summary>
/// カーソル行の blame を行末に薄く出す（VS Code の Git blame decoration・Loomo §24.42）。<c>:Gblame</c> の左の欄と違い、
/// いまいる 1 行の「誰が・いつ・どのコミットで」だけを本文の邪魔にならない位置に置く。
///
/// <para>blame は保存してある版の行番号なので、<b>未保存の編集がある間は出さない</b>（行がずれていれば別の行の来歴を
/// 見せてしまう）。コミットしていない行にも出さない。読み込みは開いたとき・保存したとき（<see cref="RefreshGitDiff"/> の
/// 流れ）に背景で 1 回だけ行い、カーソル移動では git を起こさない。</para>
/// </summary>
public partial class VimEditorControl
{
    private bool _showCaretLineBlame;
    private Dictionary<int, EditorBlameLine>? _caretBlame;
    private string? _caretBlamePath;

    /// <summary>カーソル行の blame を行末に出すか（既定は出さない）。</summary>
    public bool ShowCaretLineBlame
    {
        get => _showCaretLineBlame;
        set
        {
            if (_showCaretLineBlame == value) return;
            _showCaretLineBlame = value;
            if (value)
                _ = LoadCaretBlameAsync();
            else
            {
                _caretBlame = null;
                _caretBlamePath = null;
                Canvas.SetLineAnnotation(-1, null);
            }
        }
    }

    /// <summary>行末に出す文面（純粋・テスト対象）。<c>作者, 日付 · 件名</c>。件名が長ければ切る。</summary>
    internal static string FormatCaretBlame(EditorBlameLine blame)
    {
        const int maxSummary = 60;
        var summary = blame.Summary.Length > maxSummary ? blame.Summary[..(maxSummary - 1)] + "…" : blame.Summary;
        return summary.Length > 0 ? $"{blame.Author}, {blame.Date} · {summary}" : $"{blame.Author}, {blame.Date}";
    }

    private async Task LoadCaretBlameAsync()
    {
        if (!_showCaretLineBlame) return;
        var filePath = _engine.CurrentBuffer.FilePath;
        if (string.IsNullOrEmpty(filePath) || _engine.CurrentBuffer.IsVirtual)
        {
            _caretBlame = null;
            _caretBlamePath = null;
            UpdateCaretBlameAnnotation();
            return;
        }
        Dictionary<int, EditorBlameLine> blame;
        try
        {
            blame = await Task.Run(() => _gitProvider.GetBlameLines(filePath));
        }
        catch (Exception)
        {
            blame = [];
        }
        // 待っている間に別のファイルへ移っていたら捨てる。
        if (!_showCaretLineBlame || !string.Equals(filePath, _engine.CurrentBuffer.FilePath, StringComparison.OrdinalIgnoreCase))
            return;
        _caretBlame = blame;
        _caretBlamePath = filePath;
        UpdateCaretBlameAnnotation();
    }

    /// <summary>カーソルが動いた・編集した・読み直したときに注記を合わせ直す（git は起こさない）。</summary>
    private void UpdateCaretBlameAnnotation()
    {
        if (!_showCaretLineBlame || _caretBlame is null
            || !string.Equals(_caretBlamePath, _engine.CurrentBuffer.FilePath, StringComparison.OrdinalIgnoreCase)
            || _engine.CurrentBuffer.HasUnsavedChanges)
        {
            Canvas.SetLineAnnotation(-1, null);
            return;
        }
        var line = _engine.Cursor.Line;
        Canvas.SetLineAnnotation(line, _caretBlame.TryGetValue(line, out var blame) ? FormatCaretBlame(blame) : null);
    }
}
