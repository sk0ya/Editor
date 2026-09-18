using Editor.Core.Lsp;

namespace Editor.Core.Text;

/// <summary>
/// ホバーの説明ポップアップを<b>文字として</b>持ち出すための組み立て。
///
/// <para>ポップアップの中身は色の付いた <c>TextBlock</c> の積み重ねで、WPF のそれは選択できない
/// ——画面に出ている説明は、読めても<b>取り出せない</b>。シグネチャを手で打ち直すか諦めるかの
/// 二択だった。ここは「見えているものと同じ並び」を素のテキストへ直す係で、描画
/// （<c>HoverContentBuilder</c>）と同じ順序——診断 → シグネチャ → 説明——を守る。</para>
///
/// <para>コードフェンスに<b>フェンス記号は付けない</b>。ポップアップにも出ていないし、この文字列の
/// 主な行き先はシグネチャを貼り付けるエディタだから。区切り線（<c>---</c>）も落とす——あれは絵で、
/// 文字にすると本文に混ざるだけ。</para>
///
/// <para>1 行しか出せない場所（ステータスバー）向けの
/// <see cref="HoverMarkdown.PlainText"/> とは別物：あちらは Markdown から直接で診断を知らない。
/// こちらは<b>いま出ているポップアップ</b>——診断を含む——の写しを作る。</para>
/// </summary>
public static class HoverCopyText
{
    /// <summary>いま出ているポップアップの中身を素のテキストに。空なら空文字。</summary>
    public static string Build(
        IReadOnlyList<LspDiagnostic> diagnostics, IReadOnlyList<HoverBlock> blocks)
    {
        var parts = new List<string>();

        foreach (var diagnostic in diagnostics)
        {
            var origin = Origin(diagnostic);
            parts.Add(origin.Length == 0 ? diagnostic.Message : $"{diagnostic.Message}  {origin}");
        }

        foreach (var block in blocks)
        {
            switch (block.Kind)
            {
                case HoverBlockKind.Code:
                    if (block.Code.Length > 0) parts.Add(block.Code);
                    break;
                case HoverBlockKind.Rule:
                    break;
                default:
                    var text = new System.Text.StringBuilder();
                    foreach (var span in block.Spans)
                        text.Append(span.Style == HoverSpanStyle.LineBreak ? "\n" : span.Text);
                    if (text.Length > 0) parts.Add(text.ToString());
                    break;
            }
        }

        return string.Join("\n\n", parts);
    }

    /// <summary>診断の出どころ（<c>Roslyn(CS0219)</c> のような並び）。表示と写しで綴りがずれないよう、
    /// ここが唯一の実装——描画側（<c>HoverContentBuilder</c>）もこれを呼ぶ。</summary>
    public static string Origin(LspDiagnostic diagnostic) => (diagnostic.Source, diagnostic.Code) switch
    {
        (null or "", null or "") => "",
        (null or "", var code) => code!,
        (var source, null or "") => source!,
        var (source, code) => $"{source}({code})",
    };
}
