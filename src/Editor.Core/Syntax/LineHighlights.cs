namespace Editor.Core.Syntax;

/// <summary>行の中で背景を塗る範囲（0 始まりの列と長さ）。</summary>
public readonly record struct LineHighlightSpan(int Start, int Length);

/// <summary>
/// 見えている行（<paramref name="firstLine"/>〜<paramref name="lastLine"/>）のうち、ホストが強調したい範囲を
/// 行番号ごとに返す。表示範囲や本文が変わるたびに UI スレッドで呼ばれるので、重い処理をしないこと。
/// <paramref name="tokens"/> は字句分けの結果で、構文表示が無効なときや巨大なファイルでは空になる。
/// </summary>
public delegate IReadOnlyDictionary<int, IReadOnlyList<LineHighlightSpan>> LineHighlightProvider(
    string? filePath, string[] lines, LineTokens[] tokens, int firstLine, int lastLine);
