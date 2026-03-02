namespace Editor.Core.Folds.Languages;

/// <summary>
/// { } ブレース対応言語のフォールド検出。
/// C#、JS/TS、Rust、Go、Java、C/C++ など共通ロジック。
/// C# のみ #region / #endregion も検出する。
/// </summary>
public class BraceFoldLanguage : IFoldLanguage
{
    public string[] Extensions =>
    [
        ".cs", ".js", ".ts", ".jsx", ".tsx",
        ".rs", ".go", ".java",
        ".c", ".cpp", ".h", ".hpp",
        ".css", ".scss", ".less",
        ".swift", ".kt", ".kts",
    ];

    public IReadOnlyList<(int StartLine, int EndLine)> Detect(string[] lines)
    {
        var result = new List<(int, int)>();
        AddBraceFolds(lines, result);
        AddRegionFolds(lines, result);   // #region は C# のみ有効だが他言語で誤検出しても無害
        return result;
    }

    internal static void AddBraceFolds(string[] lines, List<(int, int)> result)
    {
        var stack = new Stack<int>();
        bool inBlockComment = false;

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            bool inLineComment = false;
            bool inString = false;
            char stringChar = '"';

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
                    if (c == stringChar) inString = false;
                    continue;
                }

                if (c == '/' && next == '/') { inLineComment = true; continue; }
                if (c == '/' && next == '*') { inBlockComment = true; j++; continue; }
                if (c == '#' && IsLineComment(line, j)) { inLineComment = true; continue; } // Python/Ruby-style
                if (c == '"' || c == '\'') { inString = true; stringChar = c; continue; }

                if (c == '{') stack.Push(i);
                else if (c == '}' && stack.Count > 0)
                {
                    int start = stack.Pop();
                    if (i > start) result.Add((start, i));
                }
            }
        }
    }

    // # がコメント開始かどうか（CSS の色コードや C# の #region を除外）
    private static bool IsLineComment(string line, int pos)
    {
        // C# 等では # は前置詞として使わない（行頭の #region は別途処理）
        // ここでは無効にして AddBraceFolds では Python-style は対象外
        return false;
    }

    internal static void AddRegionFolds(string[] lines, List<(int, int)> result)
    {
        var stack = new Stack<int>();
        for (int i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith("#region", StringComparison.OrdinalIgnoreCase))
                stack.Push(i);
            else if (trimmed.StartsWith("#endregion", StringComparison.OrdinalIgnoreCase) && stack.Count > 0)
            {
                int start = stack.Pop();
                if (i > start) result.Add((start, i));
            }
        }
    }
}
