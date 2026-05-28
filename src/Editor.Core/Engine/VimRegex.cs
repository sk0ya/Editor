using System.Text;

namespace Editor.Core.Engine;

internal static class VimRegex
{
    public static string ToDotNetPattern(string pattern)
    {
        if (string.IsNullOrEmpty(pattern))
            return pattern;

        var mode = VimRegexMode.Default;
        var sb = new StringBuilder(pattern.Length);

        for (int i = 0; i < pattern.Length; i++)
        {
            if (IsModeSwitch(pattern, i, out var nextMode))
            {
                mode = nextMode;
                i++;
                continue;
            }

            if (mode == VimRegexMode.VeryNomagic)
                AppendVeryNomagic(pattern, ref i, sb);
            else
                sb.Append(pattern[i]);
        }

        return sb.ToString();
    }

    private static bool IsModeSwitch(string pattern, int index, out VimRegexMode mode)
    {
        mode = VimRegexMode.Default;
        if (index + 1 >= pattern.Length || pattern[index] != '\\')
            return false;

        mode = pattern[index + 1] switch
        {
            'v' => VimRegexMode.VeryMagic,
            'V' => VimRegexMode.VeryNomagic,
            _ => VimRegexMode.Default,
        };

        return mode != VimRegexMode.Default;
    }

    private static void AppendVeryNomagic(string pattern, ref int index, StringBuilder sb)
    {
        var ch = pattern[index];
        if (ch != '\\')
        {
            AppendLiteralRegexChar(sb, ch);
            return;
        }

        if (index + 1 >= pattern.Length)
        {
            sb.Append(@"\\");
            return;
        }

        var next = pattern[index + 1];
        if (next is 'v' or 'V')
            return;

        if (next == '\\')
            sb.Append(@"\\");
        else if (IsDotNetRegexMeta(next))
            sb.Append(next);
        else
            sb.Append('\\').Append(next);

        index++;
    }

    private static void AppendLiteralRegexChar(StringBuilder sb, char ch)
    {
        if (IsDotNetRegexMeta(ch))
            sb.Append('\\');
        sb.Append(ch);
    }

    private static bool IsDotNetRegexMeta(char ch) =>
        ch is '\\' or '.' or '^' or '$' or '|' or '?' or '*' or '+' or '(' or ')' or '[' or ']' or '{' or '}';

    private enum VimRegexMode
    {
        Default,
        VeryMagic,
        VeryNomagic,
    }
}
