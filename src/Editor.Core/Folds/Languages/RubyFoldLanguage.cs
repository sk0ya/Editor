namespace Editor.Core.Folds.Languages;

/// <summary>
/// Ruby のフォールド検出。def/class/module/if/unless/while/until/for/case/begin/do
/// ブロックを end で閉じる（キーワードのネストをスタックで追跡）。
/// </summary>
public class RubyFoldLanguage : IFoldLanguage
{
    public string[] Extensions => [".rb"];

    private static readonly string[] _openerPrefixes =
        ["def ", "class ", "module ", "if ", "unless ", "while ", "until ", "for ", "case "];

    public IReadOnlyList<(int StartLine, int EndLine)> Detect(string[] lines)
    {
        var result = new List<(int, int)>();
        var stack = new Stack<int>();

        for (int i = 0; i < lines.Length; i++)
        {
            var leading = StripComment(lines[i]).TrimEnd().TrimStart();
            if (leading.Length == 0) continue;

            if (IsOpener(leading) || EndsWithDoBlock(leading))
                stack.Push(i);
            else if (IsEnd(leading) && stack.Count > 0)
            {
                int start = stack.Pop();
                if (i > start) result.Add((start, i));
            }
        }

        return result;
    }

    private static string StripComment(string line)
    {
        int idx = line.IndexOf('#');
        return idx >= 0 ? line[..idx] : line;
    }

    private static bool IsOpener(string leading)
    {
        if (leading == "begin") return true;
        foreach (var kw in _openerPrefixes)
        {
            if (leading.StartsWith(kw, StringComparison.Ordinal) && !EndsWithEnd(leading))
                return true;
        }
        return false;
    }

    // "arr.each do |x|" / "loop do" — a trailing "do" (optionally followed by |params|) opens a block.
    // "while "/"until "/"for " already push via IsOpener, so this only fires for bare method-call blocks.
    private static bool EndsWithDoBlock(string leading)
    {
        var s = leading;
        if (s.EndsWith('|'))
        {
            int lastPipe = s.LastIndexOf('|', s.Length - 2);
            if (lastPipe >= 0) s = s[..lastPipe].TrimEnd();
        }
        return s == "do" || s.EndsWith(" do", StringComparison.Ordinal);
    }

    private static bool IsEnd(string leading) =>
        leading == "end" || (leading.StartsWith("end", StringComparison.Ordinal) && !char.IsLetterOrDigit(leading[3]) && leading[3] != '_');

    private static bool EndsWithEnd(string leading) => leading == "end" || leading.EndsWith(" end", StringComparison.Ordinal);
}
