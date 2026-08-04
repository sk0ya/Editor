using System.Text;

namespace Editor.Core.Text;

/// <summary>One inline link or image. All positions are indexes into the parsed string.</summary>
/// <param name="Start">Index of the opening token (<c>!</c> for an image, <c>[</c> for a link).</param>
/// <param name="Length">Total length through the closing <c>)</c>.</param>
/// <param name="Text">Raw link text (the contents of <c>[]</c>, escapes not resolved).</param>
/// <param name="DestinationStart">Index of the destination (the char after <c>&lt;</c> for the angled form).</param>
/// <param name="DestinationLength">Length of the destination as written (excluding <c>&lt;&gt;</c>).</param>
/// <param name="Destination">The destination with <c>&lt;&gt;</c> removed and backslash escapes resolved.</param>
/// <param name="IsImage">True for an image (<c>![...]</c>).</param>
public readonly record struct MarkdownInlineLink(
    int Start,
    int Length,
    string Text,
    int DestinationStart,
    int DestinationLength,
    string Destination,
    bool IsImage);

/// <summary>
/// Parses Markdown inline links <c>[text](dest "title")</c> and images <c>![alt](dest)</c>.
///
/// <para><b>A destination does not end at the first <c>)</c>.</b> CommonMark allows <b>balanced</b>
/// parentheses inside a destination that is not wrapped in <c>&lt;&gt;</c>, so the destination of
/// <c>[aa](aa(bb)_cc.md)</c> is <c>aa(bb)_cc.md</c>. Cutting at the first <c>)</c> — with a
/// <c>[^\)]+</c> regex or <c>IndexOf(')')</c> — truncates the path and leaks the remainder into the
/// surrounding text. Link text may likewise contain balanced <c>[]</c>.</para>
///
/// <para>Handled: the <c>&lt;dest&gt;</c> form (may contain spaces and unbalanced parens), backslash
/// escapes (<c>\(</c> <c>\)</c> <c>\&lt;</c> <c>\&gt;</c>), and an optional title
/// (<c>"..."</c> / <c>'...'</c> / <c>(...)</c>).</para>
/// </summary>
public static class MarkdownLinkParser
{
    /// <summary>Every top-level inline link and image, in source order. Reference and autolinks are out of scope.</summary>
    public static IReadOnlyList<MarkdownInlineLink> FindAll(string? text)
    {
        if (string.IsNullOrEmpty(text)) return [];

        var results = new List<MarkdownInlineLink>();
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\\') { i++; continue; }     // an escaped char never opens a link
            if (text[i] != '[') continue;
            if (!TryParseAt(text, i, out var link)) continue;

            results.Add(link);
            i = link.Start + link.Length - 1;
        }
        return results;
    }

    /// <summary>
    /// Every image <c>![alt](src)</c>, <b>including ones nested inside link text</b> — the badge form
    /// <c>[![alt](badge.svg)](url)</c>. Use this where images are resolved before links.
    /// </summary>
    public static IReadOnlyList<MarkdownInlineLink> FindImages(string? text)
    {
        if (string.IsNullOrEmpty(text)) return [];

        var results = new List<MarkdownInlineLink>();
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\\') { i++; continue; }
            if (text[i] != '!' || i + 1 >= text.Length || text[i + 1] != '[') continue;
            if (!TryParseAt(text, i + 1, out var link) || !link.IsImage) continue;

            results.Add(link);
            i = link.Start + link.Length - 1;
        }
        return results;
    }

    /// <summary>
    /// Parses the inline link or image starting at <paramref name="bracket"/> (the index of <c>[</c>).
    /// A preceding <c>!</c> makes it an image, and <see cref="MarkdownInlineLink.Start"/> points at that <c>!</c>.
    /// </summary>
    public static bool TryParseAt(string text, int bracket, out MarkdownInlineLink link)
    {
        link = default;
        if (string.IsNullOrEmpty(text) || bracket < 0 || bracket >= text.Length || text[bracket] != '[')
            return false;

        if (!TryScanBalanced(text, bracket, '[', ']', out int textEnd))
            return false;

        int paren = textEnd + 1;                        // just past ']'
        if (paren >= text.Length || text[paren] != '(')
            return false;

        if (!TryParseDestination(text, paren, out int destStart, out int destLength, out bool angled, out int close))
            return false;

        bool isImage = bracket > 0 && text[bracket - 1] == '!'
            && !(bracket > 1 && text[bracket - 2] == '\\');
        int start = isImage ? bracket - 1 : bracket;

        link = new MarkdownInlineLink(
            start,
            close + 1 - start,
            text[(bracket + 1)..textEnd],
            destStart,
            destLength,
            Unescape(text.Substring(destStart, destLength), angled),
            isImage);
        return true;
    }

    /// <summary>
    /// Reads the destination and optional title starting at <paramref name="open"/> (the index of <c>(</c>)
    /// and reports the matching <c>)</c>. Balanced parentheses belong to the destination.
    /// </summary>
    /// <param name="angled">True when the destination used the <c>&lt;...&gt;</c> form
    /// (<paramref name="destStart"/> then points at the contents).</param>
    public static bool TryParseDestination(
        string text, int open, out int destStart, out int destLength, out bool angled, out int close)
    {
        destStart = 0;
        destLength = 0;
        angled = false;
        close = -1;
        if (open < 0 || open >= text.Length || text[open] != '(') return false;

        int i = SkipWhitespace(text, open + 1);
        if (i >= text.Length) return false;

        if (text[i] == '<')
        {
            // <dest>: spaces and unbalanced parens are allowed; a newline or a bare '<' is not.
            destStart = i + 1;
            int j = destStart;
            while (j < text.Length && text[j] != '>' && text[j] != '\n' && text[j] != '<')
            {
                if (text[j] == '\\' && j + 1 < text.Length) j++;
                j++;
            }
            if (j >= text.Length || text[j] != '>') return false;
            destLength = j - destStart;
            angled = true;
            i = j + 1;
        }
        else
        {
            destStart = i;
            int depth = 0;
            int j = i;
            while (j < text.Length)
            {
                char c = text[j];
                if (c == '\\' && j + 1 < text.Length) { j += 2; continue; }
                if (char.IsWhiteSpace(c)) break;        // a title starts here
                if (c == '(') depth++;
                else if (c == ')')
                {
                    if (depth == 0) break;              // the ')' that closes this link
                    depth--;
                }
                j++;
            }
            if (depth != 0) return false;               // unbalanced parens are not a destination
            destLength = j - destStart;
            i = j;
        }

        i = SkipWhitespace(text, i);
        if (i < text.Length && text[i] is '"' or '\'' or '(')
        {
            char openQuote = text[i];
            char closeQuote = openQuote == '(' ? ')' : openQuote;
            int j = i + 1;
            while (j < text.Length && text[j] != closeQuote)
            {
                if (text[j] == '\\' && j + 1 < text.Length) j++;
                j++;
            }
            if (j >= text.Length) return false;
            i = SkipWhitespace(text, j + 1);
        }

        if (i >= text.Length || text[i] != ')') return false;
        close = i;
        return true;
    }

    /// <summary>Resolves backslash escapes in a destination (<c>&lt;</c>/<c>&gt;</c> too for the angled form).</summary>
    public static string Unescape(string destination, bool angled = false)
    {
        if (destination.IndexOf('\\') < 0) return destination;

        var sb = new StringBuilder(destination.Length);
        for (int i = 0; i < destination.Length; i++)
        {
            if (destination[i] == '\\' && i + 1 < destination.Length && IsEscapable(destination[i + 1], angled))
            {
                sb.Append(destination[i + 1]);
                i++;
                continue;
            }
            sb.Append(destination[i]);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Renders a destination so it reads back unchanged: wraps it in <c>&lt;...&gt;</c> when it contains
    /// whitespace, <c>&lt;</c>/<c>&gt;</c>, or unbalanced parens, escaping only what must be escaped.
    /// Anything that <b>writes</b> a Markdown link (image paste, HTML→Markdown) should go through this.
    /// </summary>
    public static string EncodeDestination(string? destination)
    {
        var dest = destination ?? "";
        if (dest.Length == 0) return dest;

        bool needsAngle = false;
        int depth = 0;
        foreach (var c in dest)
        {
            if (char.IsWhiteSpace(c) || c is '<' or '>') { needsAngle = true; continue; }
            if (c == '(') depth++;
            else if (c == ')') { if (depth == 0) needsAngle = true; else depth--; }
        }
        if (depth != 0) needsAngle = true;

        if (!needsAngle) return dest.Replace("\\", "\\\\");

        var sb = new StringBuilder(dest.Length + 2).Append('<');
        foreach (var c in dest)
        {
            if (c is '\\' or '<' or '>') sb.Append('\\');
            sb.Append(c == '\n' ? ' ' : c);
        }
        return sb.Append('>').ToString();
    }

    /// <summary>Finds the balanced closing delimiter for the one at <paramref name="open"/>, honouring escapes.</summary>
    private static bool TryScanBalanced(string text, int open, char openChar, char closeChar, out int close)
    {
        close = -1;
        int depth = 0;
        for (int i = open; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\\') { i++; continue; }
            if (c == '\n' && i + 1 < text.Length && text[i + 1] == '\n') return false; // inlines never span a blank line
            if (c == openChar) depth++;
            else if (c == closeChar)
            {
                depth--;
                if (depth == 0) { close = i; return true; }
            }
        }
        return false;
    }

    private static int SkipWhitespace(string text, int i)
    {
        while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
        return i;
    }

    private static bool IsEscapable(char c, bool angled) =>
        c is '(' or ')' or '\\' || (angled && c is '<' or '>');
}
