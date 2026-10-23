namespace Editor.Core.Syntax;

/// <summary>
/// 1行を「その行の文字列」と「前の行から持ち越した状態」（ブロックコメントの中か、複数行文字列の中か…）
/// だけから字句分けできる言語。<see cref="SyntaxEngine"/> はこれを使って、編集のたびに
/// <b>変わった行から先だけ</b>を分け直し、持ち越す状態が編集前と一致した所で打ち切る。
/// 実装しない言語（ホストが足した独自の言語など）は従来どおり毎回ファイル全体を分ける。
/// </summary>
public interface ILineStateSyntaxLanguage : ISyntaxLanguage
{
    /// <summary>1行目の前の状態。比較は値の等価性（<see cref="object.Equals(object?)"/>）なので record で作る。</summary>
    object InitialState { get; }

    /// <summary><paramref name="state"/> を行頭の状態として1行を分け、行末の状態へ書き換える。
    /// 状態が変わらなければ同じインスタンスのまま返してよい。</summary>
    SyntaxToken[] TokenizeLine(string line, ref object state);
}

public static class LineStateTokenizer
{
    /// <summary>ファイル全体を先頭から分ける。<see cref="ISyntaxLanguage.Tokenize"/> の実装に使う
    /// ——差分の分け直しと同じ1行ずつの経路を通すので、両者の結果は構造上一致する。</summary>
    public static LineTokens[] TokenizeAll(ILineStateSyntaxLanguage language, string[] lines)
    {
        var result = new LineTokens[lines.Length];
        var state = language.InitialState;
        for (int i = 0; i < lines.Length; i++)
            result[i] = new LineTokens(i, language.TokenizeLine(lines[i], ref state));
        return result;
    }

    /// <summary>行末の状態を書き戻す。値が同じなら元のインスタンスを残す（行ごとの箱詰めを避ける）。</summary>
    public static void Advance<TState>(ref object state, TState next) where TState : struct, IEquatable<TState>
    {
        if (state is TState current && current.Equals(next)) return;
        state = next;
    }
}
