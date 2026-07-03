using System.Text.RegularExpressions;

namespace Editor.Core.Editing;

/// <summary>
/// Editing aid for HTML/XML-like markup. Typing <c>&gt;</c> that closes an opening tag
/// (e.g. <c>&lt;div</c>) auto-inserts the matching <c>&lt;/tagname&gt;</c> immediately after
/// the cursor, leaving the caret between the two tags. Self-closing tags (<c>/&gt;</c>),
/// closing tags, comments and void elements (<c>br</c>, <c>img</c>, …) are left alone.
/// </summary>
public sealed class HtmlTagEditAssist : EditAssistBase
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".html", ".htm", ".xml", ".xaml", ".jsx", ".tsx", ".vue", ".svg",
    };

    private static readonly HashSet<string> VoidElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "br", "img", "input", "hr", "meta", "link", "area", "base", "col", "embed", "source", "track", "wbr",
    };

    private static readonly Regex OpenTagRegex = new(@"^<([A-Za-z][A-Za-z0-9:_-]*)(?:\s[^<>]*)?$", RegexOptions.Compiled);

    public override bool AppliesTo(string? filePath)
    {
        if (filePath == null) return false;
        return Extensions.Contains(System.IO.Path.GetExtension(filePath));
    }

    public override EditResult OnChar(EditContext ctx, char typed)
    {
        if (typed != '>') return EditResult.NotHandled;
        if (!AppliesTo(ctx.FilePath)) return EditResult.NotHandled;

        var buf = ctx.Buffer;
        int col = ctx.Cursor.Column;
        var line = buf.GetLine(ctx.Cursor.Line);
        if (col > line.Length) return EditResult.NotHandled;

        string before = line[..col];
        int lt = before.LastIndexOf('<');
        if (lt < 0) return EditResult.NotHandled;

        string tagText = before[lt..];
        if (tagText.StartsWith("</", StringComparison.Ordinal)) return EditResult.NotHandled;
        if (tagText.StartsWith("<!--", StringComparison.Ordinal)) return EditResult.NotHandled;
        if (tagText.TrimEnd().EndsWith("/", StringComparison.Ordinal)) return EditResult.NotHandled;

        var match = OpenTagRegex.Match(tagText);
        if (!match.Success) return EditResult.NotHandled;

        string tagName = match.Groups[1].Value;
        if (VoidElements.Contains(tagName)) return EditResult.NotHandled;

        buf.InsertChar(ctx.Cursor.Line, col, '>');
        buf.InsertText(ctx.Cursor.Line, col + 1, $"</{tagName}>");
        return EditResult.Done(ctx.Cursor with { Column = col + 1 });
    }
}
