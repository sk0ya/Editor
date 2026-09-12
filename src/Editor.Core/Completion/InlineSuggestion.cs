namespace Editor.Core.Completion;

/// <summary>提案の出どころ。表示の強さ（出典バッジ）と、競合したときの優先順位に使う。</summary>
public enum InlineSuggestionSource
{
    /// <summary>同じバッファの既出行から。外部依存なしで即答できる。</summary>
    BufferLine,

    /// <summary>言語サーバーの補完候補の先頭。識別子 1 つぶんの短い提案。</summary>
    Lsp,

    /// <summary>ホストが差した供給元（ローカル LLM など）。</summary>
    External,
}

/// <summary>
/// キャレットの先に薄く表示する提案 1 件。<see cref="Text"/> はキャレット位置に
/// <b>そのまま挿入できる</b>文字列（本文の置き換えではない）。
/// </summary>
public readonly record struct InlineSuggestion(string Text, InlineSuggestionSource Source)
{
    /// <summary>最初の改行までの 1 行分。複数行の提案を「1 行だけ受け入れる」ときに使う。</summary>
    public string FirstLine
    {
        get
        {
            int newline = Text.IndexOf('\n');
            return newline < 0 ? Text : Text[..newline].TrimEnd('\r');
        }
    }

    /// <summary>
    /// 単語 1 つぶんだけ受け入れるときの長さ。先頭の区切り文字を 1 つ飲み込んでから
    /// 語（英数字と <c>_</c>）の終わりまで進む——「<c>.Name</c>」を <c>.</c> と <c>Name</c> の
    /// 2 回に割ると、Ctrl+→ を 2 回押さないと 1 語も進まない。
    /// </summary>
    public int NextWordLength()
    {
        var text = FirstLine;
        if (text.Length == 0) return 0;

        int i = 0;
        while (i < text.Length && !IsWordChar(text[i])) i++;
        if (i == 0)
        {
            while (i < text.Length && IsWordChar(text[i])) i++;
        }
        else if (i < text.Length && IsWordChar(text[i]))
        {
            while (i < text.Length && IsWordChar(text[i])) i++;
        }
        return i;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';
}

/// <summary>入力の先読みを求めるときの状況。バッファの中身は呼び出し時点のスナップショット。</summary>
/// <param name="Lines">バッファ全行（呼び出し後に書き換わらない）。</param>
/// <param name="Line">キャレットの行（0 始まり）。</param>
/// <param name="Column">キャレットの桁（0 始まり）。</param>
/// <param name="FilePath">対象ファイル。無題バッファでは null。</param>
public readonly record struct InlineSuggestionContext(
    IReadOnlyList<string> Lines, int Line, int Column, string? FilePath);
