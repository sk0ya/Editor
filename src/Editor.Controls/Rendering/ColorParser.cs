using System.Windows.Media;

namespace Editor.Controls.Rendering;

/// <summary>
/// Parses CSS/HTML color literals from text.
/// Supported formats: #RGB, #RRGGBB, #RRGGBBAA, rgb(R,G,B), rgba(R,G,B,A)
/// </summary>
internal static class ColorParser
{
    /// <summary>
    /// Try to parse a color starting at <paramref name="startIndex"/> in <paramref name="text"/>.
    /// Returns true and sets <paramref name="color"/> and <paramref name="length"/> on success.
    /// </summary>
    public static bool TryParseColor(string text, int startIndex, out Color color, out int length)
    {
        color = default;
        length = 0;

        if (startIndex >= text.Length) return false;

        char c = text[startIndex];

        if (c == '#')
            return IsHexColorStart(text, startIndex) && TryParseHex(text, startIndex, out color, out length);

        // rgb( は語の途中（torgb( など）では取らない
        if (startIndex > 0 && IsWordChar(text[startIndex - 1])) return false;

        if (startIndex + 3 < text.Length &&
            char.ToLowerInvariant(c) == 'r' &&
            char.ToLowerInvariant(text[startIndex + 1]) == 'g' &&
            char.ToLowerInvariant(text[startIndex + 2]) == 'b')
            return TryParseRgb(text, startIndex, out color, out length);

        return false;
    }

    private static bool TryParseHex(string text, int start, out Color color, out int length)
    {
        color = default;
        length = 0;

        // Must start with '#'
        if (start >= text.Length || text[start] != '#') return false;

        int maxLen = Math.Min(9, text.Length - start - 1); // up to 8 hex digits
        int hexLen = 0;
        for (int i = 0; i < maxLen; i++)
        {
            char h = text[start + 1 + i];
            if (!char.IsAsciiHexDigit(h)) break;
            hexLen++;
        }

        // 直後が語の続きなら色ではない。'-' も続きとして扱う：見出しのアンカー（#add-feature）の頭
        // （#add）が色として拾われていた。
        int afterEnd = start + 1 + hexLen;
        if (afterEnd < text.Length && (IsWordChar(text[afterEnd]) || text[afterEnd] == '-'))
            return false;

        if (hexLen == 3)
        {
            // #RGB -> #RRGGBB
            byte r = HexToByte(text[start + 1], text[start + 1]);
            byte g = HexToByte(text[start + 2], text[start + 2]);
            byte b = HexToByte(text[start + 3], text[start + 3]);
            color = Color.FromRgb(r, g, b);
            length = 4;
            return true;
        }

        if (hexLen == 6)
        {
            byte r = HexToByte(text[start + 1], text[start + 2]);
            byte g = HexToByte(text[start + 3], text[start + 4]);
            byte b = HexToByte(text[start + 5], text[start + 6]);
            color = Color.FromRgb(r, g, b);
            length = 7;
            return true;
        }

        if (hexLen == 8)
        {
            byte r = HexToByte(text[start + 1], text[start + 2]);
            byte g = HexToByte(text[start + 3], text[start + 4]);
            byte b = HexToByte(text[start + 5], text[start + 6]);
            byte a = HexToByte(text[start + 7], text[start + 8]);
            color = Color.FromArgb(a, r, g, b);
            length = 9;
            return true;
        }

        return false;
    }

    private static bool TryParseRgb(string text, int start, out Color color, out int length)
    {
        color = default;
        length = 0;

        // Caller has already verified text[start..start+2] == "rgb" (case-insensitive).
        // Determine if this is "rgba(" or "rgb(".
        bool hasAlpha = start + 4 < text.Length &&
                        (text[start + 3] == 'a' || text[start + 3] == 'A');
        int prefixLen = hasAlpha ? 5 : 4; // "rgba(" or "rgb("

        if (start + prefixLen > text.Length) return false;

        // Verify the opening parenthesis (last char of prefix)
        if (text[start + prefixLen - 1] != '(') return false;

        // Find closing ')'
        int parenStart = start + prefixLen;
        int closeIdx = text.IndexOf(')', parenStart);
        if (closeIdx < 0) return false;

        string inner = text.Substring(parenStart, closeIdx - parenStart);
        var parts = inner.Split(',');

        if (hasAlpha && parts.Length == 4)
        {
            if (!TryParseByte(parts[0], out byte r)) return false;
            if (!TryParseByte(parts[1], out byte g)) return false;
            if (!TryParseByte(parts[2], out byte b)) return false;
            if (!TryParseAlpha(parts[3], out byte a)) return false;
            color = Color.FromArgb(a, r, g, b);
            length = closeIdx - start + 1;
            return true;
        }

        if (!hasAlpha && parts.Length == 3)
        {
            if (!TryParseByte(parts[0], out byte r)) return false;
            if (!TryParseByte(parts[1], out byte g)) return false;
            if (!TryParseByte(parts[2], out byte b)) return false;
            color = Color.FromRgb(r, g, b);
            length = closeIdx - start + 1;
            return true;
        }

        return false;
    }

    /// <summary>
    /// '#' が色の書き出しとして置かれうる位置か。以前は '#' の前を見ていなかったので、Markdown のリンク
    /// （<c>[見出し](#add)</c>、<c>doc.md#bed</c>）、HTML の <c>href="#fed"</c>、<c>issue#123</c> まで
    /// 色として拾っていた。色を書く場所（行頭・空白・<c>:</c> <c>=</c> <c>,</c> <c>(</c> の後・引用符の中・
    /// XML の値）の直後だけを認め、リンク先になる文脈は除く。
    /// </summary>
    private static bool IsHexColorStart(string text, int hashIndex)
    {
        if (hashIndex == 0) return true;
        char prev = text[hashIndex - 1];
        switch (prev)
        {
            case ' ' or '\t' or ':' or '=' or ',' or '{' or '>':
                return true;
            case '(':
                // Markdown のリンク先 [..](#anchor)
                return !(hashIndex >= 2 && text[hashIndex - 2] == ']');
            case '"' or '\'' or '`':
                // HTML のリンク先 href="#anchor"
                return !EndsWithLinkAttribute(text, hashIndex - 1);
            default:
                return false;
        }
    }

    private static bool EndsWithLinkAttribute(string text, int quoteIndex)
    {
        int end = quoteIndex; // text[..end] が引用符の手前まで
        while (end > 0 && text[end - 1] == ' ') end--;
        if (end == 0 || text[end - 1] != '=') return false;
        end--;
        while (end > 0 && text[end - 1] == ' ') end--;
        int start = end;
        while (start > 0 && (char.IsAsciiLetter(text[start - 1]) || text[start - 1] == ':')) start--;
        var name = text.AsSpan(start, end - start);
        return name.Equals("href", StringComparison.OrdinalIgnoreCase)
            || name.Equals("xlink:href", StringComparison.OrdinalIgnoreCase)
            || name.Equals("src", StringComparison.OrdinalIgnoreCase)
            || name.Equals("action", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static bool TryParseByte(string s, out byte value)
    {
        value = 0;
        s = s.Trim();
        if (!int.TryParse(s, out int n) || n < 0 || n > 255) return false;
        value = (byte)n;
        return true;
    }

    private static bool TryParseAlpha(string s, out byte value)
    {
        value = 255;
        s = s.Trim();
        // Accept float (0.0–1.0) or integer (0–255)
        if (double.TryParse(s, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double d))
        {
            if (d < 0 || d > 255) return false;
            value = d <= 1.0 ? (byte)Math.Round(d * 255) : (byte)d;
            return true;
        }
        return false;
    }

    private static byte HexToByte(char hi, char lo)
        => (byte)((HexVal(hi) << 4) | HexVal(lo));

    private static int HexVal(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => 0
    };
}
