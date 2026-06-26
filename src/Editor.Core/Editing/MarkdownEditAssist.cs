using Editor.Core.Models;

namespace Editor.Core.Editing;

/// <summary>
/// Editing aid for Markdown files. On Enter it continues the current list item with a
/// fresh marker (<c>-</c>/<c>*</c>/<c>+</c> or the next ordinal for <c>1.</c>/<c>1)</c>),
/// or clears the marker when the item is empty (exiting the list). On Tab/Shift+Tab it
/// indents/outdents the current list item — even when it has no text yet.
/// </summary>
public sealed class MarkdownEditAssist : EditAssistBase
{
    public override bool AppliesTo(string? filePath)
    {
        if (filePath == null) return false;
        var ext = System.IO.Path.GetExtension(filePath).ToLowerInvariant();
        return ext == ".md" || ext == ".markdown";
    }

    public override EditResult OnEnter(EditContext ctx)
    {
        var buf = ctx.Buffer;
        var line = buf.GetLine(ctx.Cursor.Line);
        if (!TryParseListItem(line, out var item)) return EditResult.NotHandled;

        // Pressing Enter inside the leading indent/marker behaves like a normal newline.
        if (ctx.Cursor.Column < item.ContentStart) return EditResult.NotHandled;

        // Empty item (marker but no text) → exit the list by clearing the line.
        if (!item.HasContent)
        {
            buf.ReplaceLine(ctx.Cursor.Line, "");
            return EditResult.Done(ctx.Cursor with { Column = 0 });
        }

        // Continue the list on a fresh line with the next marker.
        string marker = item.Ordered
            ? $"{item.Number + 1}{item.Delimiter} "
            : $"{item.Bullet} ";
        string prefix = item.Indent + marker;

        buf.BreakLine(ctx.Cursor.Line, ctx.Cursor.Column);
        int newLine = ctx.Cursor.Line + 1;
        buf.InsertText(newLine, 0, prefix);
        return EditResult.Done(new CursorPosition(newLine, prefix.Length));
    }

    public override EditResult OnTab(EditContext ctx, bool shift)
    {
        var buf = ctx.Buffer;
        var line = buf.GetLine(ctx.Cursor.Line);
        if (!TryParseListItem(line, out _)) return EditResult.NotHandled;

        int sw = System.Math.Max(1, ctx.ShiftWidth);
        if (!shift)
        {
            string indentStr = ctx.ExpandTab ? new string(' ', sw) : "\t";
            buf.InsertText(ctx.Cursor.Line, 0, indentStr);
            return EditResult.Done(ctx.Cursor with { Column = ctx.Cursor.Column + indentStr.Length });
        }

        // Outdent: remove up to one level of leading whitespace (a tab counts as a full level).
        int toRemove = 0;
        for (int i = 0; i < sw && i < line.Length && (line[i] == ' ' || line[i] == '\t'); i++)
        {
            toRemove++;
            if (line[i] == '\t') break;
        }
        if (toRemove == 0) return EditResult.Done(ctx.Cursor); // handled, nothing to outdent
        buf.DeleteRange(ctx.Cursor.Line, 0, toRemove);
        return EditResult.Done(ctx.Cursor with { Column = System.Math.Max(0, ctx.Cursor.Column - toRemove) });
    }

    public override string? OpenLinePrefix(EditContext ctx, bool above)
    {
        var line = ctx.Buffer.GetLine(ctx.Cursor.Line);
        if (!TryParseListItem(line, out var item)) return null;

        if (!item.Ordered) return item.Indent + $"{item.Bullet} ";

        // o (below) advances the ordinal; O (above) keeps the current one.
        int number = above ? item.Number : item.Number + 1;
        return item.Indent + $"{number}{item.Delimiter} ";
    }

    // ── List-item parsing ──

    private readonly record struct ListItem(
        string Indent, bool Ordered, char Bullet, int Number, char Delimiter, int ContentStart, bool HasContent);

    /// <summary>
    /// Parses a Markdown list line into its indent, marker, and whether it has any text
    /// content after the marker. Returns false when the line is not a list item.
    /// </summary>
    private static bool TryParseListItem(string line, out ListItem item)
    {
        item = default;
        int i = 0;
        while (i < line.Length && (line[i] == ' ' || line[i] == '\t')) i++;
        if (i >= line.Length) return false;
        string indent = line[..i];
        char c = line[i];

        int contentStart;
        bool ordered = false;
        char bullet = '\0';
        int number = 0;
        char delimiter = '.';

        if (c is '-' or '*' or '+')
        {
            // Unordered marker must be followed by a space.
            if (i + 1 >= line.Length || line[i + 1] != ' ') return false;
            bullet = c;
            contentStart = i + 2;
        }
        else if (char.IsDigit(c))
        {
            int j = i;
            while (j < line.Length && char.IsDigit(line[j])) j++;
            if (j >= line.Length || (line[j] != '.' && line[j] != ')')) return false;
            if (j + 1 >= line.Length || line[j + 1] != ' ') return false;
            if (!int.TryParse(line[i..j], out number)) return false;
            ordered = true;
            delimiter = line[j];
            contentStart = j + 2;
        }
        else
        {
            return false;
        }

        bool hasContent = false;
        for (int k = contentStart; k < line.Length; k++)
        {
            if (line[k] != ' ' && line[k] != '\t') { hasContent = true; break; }
        }

        item = new ListItem(indent, ordered, bullet, number, delimiter, contentStart, hasContent);
        return true;
    }
}
