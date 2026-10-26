using Editor.Core.Buffer;

namespace Editor.Controls;

/// <summary>
/// VimEditorControl の取り消し履歴の持ち越し（Vim の 'undofile' に当たるもの・Loomo の §24.96）の公開窓口。
/// どこに書くか・いつ書くか・どの文書の履歴かはホストの責務で、エディタは今のバッファの履歴を出し入れするだけ。
/// 履歴は文書の鍵（<c>documentId</c>・ホストが決める。ファイルのパスなど）と、出したときの本文に結び付く——
/// 入れるときにどちらかが違えば何も変えずに断る（別の文書や、外で書き換わった本文に古い履歴を付けない）。
/// </summary>
public partial class VimEditorControl
{
    /// <summary>
    /// 今のバッファの取り消し履歴を書き出す（新しいものから <paramref name="options"/> の範囲で）。取り消すものも
    /// やり直すものも無ければ null。<see cref="LoadFile(string)"/> で別のファイルを載せると履歴は捨てられるので、その前に呼ぶ。
    /// </summary>
    public string? ExportUndoHistory(string documentId, UndoExportOptions? options = null)
    {
        var buffer = _engine.CurrentBuffer;
        return buffer.Undo.ExportHistory(buffer.Text, documentId, options ?? new UndoExportOptions());
    }

    /// <summary>
    /// 前に書き出した取り消し履歴を今のバッファへ戻す。ファイルを載せたあと（<see cref="LoadFile(string)"/>）に呼ぶ。
    /// 文書の鍵か本文が書き出したときと違えば、何も変えずに失敗を返す。
    /// </summary>
    public UndoImportResult ImportUndoHistory(string json, string documentId, UndoPersistenceLimits? limits = null)
    {
        var buffer = _engine.CurrentBuffer;
        return buffer.Undo.ImportHistory(json, buffer.Text, documentId, limits);
    }
}
