namespace Editor.Core.Folds.Languages;

/// <summary>
/// Python のフォールド検出。
/// def / class / if / for / while / with / try などのブロックをインデントで検出する。
/// </summary>
public class PythonFoldLanguage : IFoldLanguage
{
    public string[] Extensions => [".py", ".pyw"];

    private static readonly string[] _blockKeywords =
        ["def ", "class ", "if ", "elif ", "else:", "for ", "while ", "with ", "try:", "except", "finally:"];

    public IReadOnlyList<(int StartLine, int EndLine)> Detect(string[] lines)
    {
        var result = new List<(int, int)>();

        for (int i = 0; i < lines.Length - 1; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();

            // コメント行・空行はスキップ
            if (trimmed.Length == 0 || trimmed[0] == '#') continue;

            // ブロック開始キーワードで `:` で終わっているか確認
            bool isBlock = _blockKeywords.Any(kw => trimmed.StartsWith(kw, StringComparison.Ordinal));
            if (!isBlock || !EndsWithColon(trimmed)) continue;

            int baseIndent = line.Length - trimmed.Length;

            // 次の非空行のインデントが大きければブロック本体
            int bodyStart = i + 1;
            while (bodyStart < lines.Length && string.IsNullOrWhiteSpace(lines[bodyStart])) bodyStart++;
            if (bodyStart >= lines.Length) continue;

            int bodyIndent = lines[bodyStart].Length - lines[bodyStart].TrimStart().Length;
            if (bodyIndent <= baseIndent) continue;

            // ブロックの終端: インデントが baseIndent 以下に戻る行の直前
            int end = bodyStart;
            while (end + 1 < lines.Length)
            {
                var nextTrimmed = lines[end + 1].TrimStart();
                if (nextTrimmed.Length == 0) { end++; continue; }
                int nextIndent = lines[end + 1].Length - nextTrimmed.Length;
                if (nextIndent <= baseIndent) break;
                end++;
            }

            // 末尾空行を詰める
            while (end > i && string.IsNullOrWhiteSpace(lines[end])) end--;

            if (end > i) result.Add((i, end));
        }

        return result;
    }

    private static bool EndsWithColon(string trimmed)
    {
        // インラインコメントを除いた実コード部分が : で終わるか
        int commentIdx = trimmed.IndexOf('#');
        var code = commentIdx >= 0 ? trimmed[..commentIdx].TrimEnd() : trimmed.TrimEnd();
        return code.EndsWith(':');
    }
}
