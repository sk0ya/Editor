namespace Editor.Core.Lsp;

/// <summary>本文の一部にかける装飾の種類。</summary>
public enum DiagnosticDecorationKind
{
    /// <summary>薄字（<see cref="DiagnosticTag.Unnecessary"/>）。消しても動きが変わらない範囲。</summary>
    Faded,
    /// <summary>取り消し線（<see cref="DiagnosticTag.Deprecated"/>）。</summary>
    StruckThrough,
}

/// <summary>1行の中の <c>[StartColumn, StartColumn+Length)</c> にかける装飾。</summary>
public readonly record struct DiagnosticDecoration(int StartColumn, int Length, DiagnosticDecorationKind Kind);

/// <summary>
/// 診断のうち<b>波線で出すもの</b>と<b>本文の見た目を変えるもの</b>を仕分ける。
///
/// <para>LSP の <c>tags</c> は重大度と直交する「種類」で、<see cref="DiagnosticTag.Unnecessary"/> は
/// 「間違い」ではなく「消しても何も変わらない」という印（未使用の using、到達しないコード）。
/// これを他の診断と同じ波線で出すと、赤い波線と並んだ瞬間に<b>エラーに見える</b>——実際
/// 「using が不要」の表示をエラーと読み違える事故が起きた。VS Code と同じく、薄字で示して
/// 波線は引かない。<see cref="DiagnosticTag.Deprecated"/> は取り消し線。</para>
///
/// <para>Roslyn は連続する不要 using を<b>1件にまとめて</b>返す（0行目〜13行目のような範囲）ので、
/// 装飾は行ごとに切り出す必要がある。ここは純粋な計算だけを持ち、描画は
/// <c>EditorCanvas</c> が行う。</para>
/// </summary>
public static class DiagnosticDecorations
{
    /// <summary>波線を引く診断か。タグ付き（薄字・取り消し線で示すもの）は引かない。</summary>
    public static bool DrawsSquiggle(LspDiagnostic diagnostic)
        => !diagnostic.IsUnnecessary && !diagnostic.IsDeprecated;

    /// <summary><paramref name="line"/> 行目にかかる装飾を、その行の列に切り出して返す。
    /// <paramref name="lineLength"/> は行の文字数（範囲が行末を越えるときの打ち止め）。</summary>
    public static IReadOnlyList<DiagnosticDecoration> ForLine(
        IReadOnlyList<LspDiagnostic> diagnostics, int line, int lineLength)
    {
        if (diagnostics.Count == 0 || lineLength <= 0) return [];

        List<DiagnosticDecoration>? decorations = null;
        foreach (var diagnostic in diagnostics)
        {
            var kind = diagnostic switch
            {
                { IsUnnecessary: true } => DiagnosticDecorationKind.Faded,
                { IsDeprecated: true } => DiagnosticDecorationKind.StruckThrough,
                _ => (DiagnosticDecorationKind?)null,
            };
            if (kind is null) continue;
            if (diagnostic.Range.Start.Line > line || diagnostic.Range.End.Line < line) continue;

            int start = diagnostic.Range.Start.Line == line
                ? Math.Clamp(diagnostic.Range.Start.Character, 0, lineLength)
                : 0;
            int end = diagnostic.Range.End.Line == line
                ? Math.Clamp(diagnostic.Range.End.Character, 0, lineLength)
                : lineLength;
            if (end <= start) continue;

            decorations ??= [];
            decorations.Add(new DiagnosticDecoration(start, end - start, kind.Value));
        }
        return decorations is null ? [] : decorations;
    }
}
