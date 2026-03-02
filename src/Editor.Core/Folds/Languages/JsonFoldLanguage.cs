namespace Editor.Core.Folds.Languages;

/// <summary>
/// JSON / JSONC のフォールド検出。
/// { } オブジェクトと [ ] 配列の両方を対応させる。
/// JSONC の // および /* */ コメントもスキップする。
/// </summary>
public class JsonFoldLanguage : IFoldLanguage
{
    public string[] Extensions => [".json", ".jsonc"];

    public IReadOnlyList<(int StartLine, int EndLine)> Detect(string[] lines)
    {
        var result = new List<(int, int)>();
        // { と [ を同じスタックで扱う (close と対応させるため open 文字も記録)
        var stack = new Stack<(char Open, int Line)>();
        bool inBlockComment = false;

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            bool inLineComment = false;
            bool inString = false;

            for (int j = 0; j < line.Length; j++)
            {
                char c = line[j];
                char next = j + 1 < line.Length ? line[j + 1] : '\0';

                if (inBlockComment)
                {
                    if (c == '*' && next == '/') { inBlockComment = false; j++; }
                    continue;
                }
                if (inLineComment) break;
                if (inString)
                {
                    if (c == '\\') { j++; continue; }
                    if (c == '"') inString = false;
                    continue;
                }

                // JSONC コメント
                if (c == '/' && next == '/') { inLineComment = true; continue; }
                if (c == '/' && next == '*') { inBlockComment = true; j++; continue; }
                if (c == '"') { inString = true; continue; }

                if (c == '{' || c == '[')
                    stack.Push((c, i));
                else if ((c == '}' || c == ']') && stack.Count > 0)
                {
                    char expected = c == '}' ? '{' : '[';
                    if (stack.Peek().Open == expected)
                    {
                        var (_, start) = stack.Pop();
                        if (i > start) result.Add((start, i));
                    }
                }
            }
        }

        return result;
    }
}
