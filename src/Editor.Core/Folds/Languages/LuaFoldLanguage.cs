namespace Editor.Core.Folds.Languages;

/// <summary>
/// Lua のフォールド検出。function/if-then/for-do/while-do/do ブロックは end、
/// repeat ブロックは until で閉じる（閉じキーワードをスタックで追跡）。
/// </summary>
public class LuaFoldLanguage : IFoldLanguage
{
    public string[] Extensions => [".lua"];

    public IReadOnlyList<(int StartLine, int EndLine)> Detect(string[] lines)
    {
        var result = new List<(int, int)>();
        var stack = new Stack<(int Line, string Closer)>();

        for (int i = 0; i < lines.Length; i++)
        {
            var leading = StripComment(lines[i]).TrimEnd().TrimStart();
            if (leading.Length == 0) continue;

            if (leading == "repeat")
            {
                stack.Push((i, "until"));
                continue;
            }

            if (IsBlockOpener(leading))
            {
                stack.Push((i, "end"));
                continue;
            }

            if ((leading == "until" || leading.StartsWith("until ", StringComparison.Ordinal))
                && stack.Count > 0 && stack.Peek().Closer == "until")
            {
                var (start, _) = stack.Pop();
                if (i > start) result.Add((start, i));
                continue;
            }

            if (IsEnd(leading) && stack.Count > 0 && stack.Peek().Closer == "end")
            {
                var (start, _) = stack.Pop();
                if (i > start) result.Add((start, i));
            }
        }

        return result;
    }

    private static bool IsBlockOpener(string leading)
    {
        if (leading == "do") return true;
        if (leading.StartsWith("elseif ", StringComparison.Ordinal)) return false;
        if (leading.StartsWith("if ", StringComparison.Ordinal) && leading.EndsWith("then", StringComparison.Ordinal)) return true;
        if ((leading.StartsWith("for ", StringComparison.Ordinal) || leading.StartsWith("while ", StringComparison.Ordinal))
            && leading.EndsWith("do", StringComparison.Ordinal)) return true;
        if (ContainsWord(leading, "function") && !leading.EndsWith("end", StringComparison.Ordinal)) return true;
        return false;
    }

    private static bool IsEnd(string leading) => leading == "end" || (leading.StartsWith("end", StringComparison.Ordinal) && !IsIdentChar(leading[3]));

    private static string StripComment(string line)
    {
        int idx = line.IndexOf("--", StringComparison.Ordinal);
        return idx >= 0 ? line[..idx] : line;
    }

    private static bool ContainsWord(string s, string word)
    {
        int idx = 0;
        while ((idx = s.IndexOf(word, idx, StringComparison.Ordinal)) >= 0)
        {
            bool leftOk = idx == 0 || !IsIdentChar(s[idx - 1]);
            int rightIdx = idx + word.Length;
            bool rightOk = rightIdx >= s.Length || !IsIdentChar(s[rightIdx]);
            if (leftOk && rightOk) return true;
            idx += word.Length;
        }
        return false;
    }

    private static bool IsIdentChar(char c) => char.IsLetterOrDigit(c) || c == '_';
}
