using Editor.Core.Models;

namespace Editor.Core.Editing;

/// <summary>
/// Editing aid for C-style-comment languages. On Enter it continues a <c>//</c> line
/// comment (clearing the marker when the line is empty, exiting the comment) or a
/// <c>/* */</c> block comment (opener aligns the continuation with <c> * </c>, and an
/// existing <c>*</c>-prefixed body line is continued with <c>* </c>).
/// </summary>
public sealed class CStyleCommentEditAssist : EditAssistBase
{
    private static readonly HashSet<string> LineAndBlockExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".js", ".jsx", ".ts", ".tsx", ".rs", ".go", ".c", ".cpp", ".cc", ".cxx", ".h", ".hpp", ".hh",
    };

    private static readonly HashSet<string> BlockOnlyExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".css", ".scss", ".less",
    };

    public override bool AppliesTo(string? filePath)
    {
        if (filePath == null) return false;
        var ext = System.IO.Path.GetExtension(filePath);
        return LineAndBlockExtensions.Contains(ext) || BlockOnlyExtensions.Contains(ext);
    }

    public override EditResult OnEnter(EditContext ctx)
    {
        var buf = ctx.Buffer;
        var line = buf.GetLine(ctx.Cursor.Line);

        if (SupportsLineComment(ctx.FilePath) && TryLineCommentContinuation(line, out var linePrefix, out var lineContentStart, out var isEmpty))
        {
            if (ctx.Cursor.Column < lineContentStart) return EditResult.NotHandled;

            if (isEmpty)
            {
                buf.ReplaceLine(ctx.Cursor.Line, "");
                return EditResult.Done(ctx.Cursor with { Column = 0 });
            }

            buf.BreakLine(ctx.Cursor.Line, ctx.Cursor.Column);
            int newLine = ctx.Cursor.Line + 1;
            buf.InsertText(newLine, 0, linePrefix);
            return EditResult.Done(new CursorPosition(newLine, linePrefix.Length));
        }

        if (SupportsBlockComment(ctx.FilePath) && TryBlockCommentContinuation(line, out var blockPrefix, out var blockContentStart))
        {
            if (ctx.Cursor.Column < blockContentStart) return EditResult.NotHandled;

            buf.BreakLine(ctx.Cursor.Line, ctx.Cursor.Column);
            int newLine = ctx.Cursor.Line + 1;
            buf.InsertText(newLine, 0, blockPrefix);
            return EditResult.Done(new CursorPosition(newLine, blockPrefix.Length));
        }

        return EditResult.NotHandled;
    }

    public override string? OpenLinePrefix(EditContext ctx, bool above)
    {
        var line = ctx.Buffer.GetLine(ctx.Cursor.Line);

        if (SupportsLineComment(ctx.FilePath) && TryLineCommentContinuation(line, out var linePrefix, out _, out _))
            return linePrefix;

        if (SupportsBlockComment(ctx.FilePath) && TryBlockCommentContinuation(line, out var blockPrefix, out _))
            return blockPrefix;

        return null;
    }

    // ── Extension classification ──

    private static bool SupportsLineComment(string? filePath)
    {
        if (filePath == null) return false;
        var ext = System.IO.Path.GetExtension(filePath);
        return LineAndBlockExtensions.Contains(ext);
    }

    private static bool SupportsBlockComment(string? filePath)
    {
        if (filePath == null) return false;
        var ext = System.IO.Path.GetExtension(filePath);
        return LineAndBlockExtensions.Contains(ext) || BlockOnlyExtensions.Contains(ext);
    }

    // ── Comment continuation parsing ──

    /// <summary>Parses a <c>//</c> line-comment line: indent + marker, content start column, and whether it's empty.</summary>
    private static bool TryLineCommentContinuation(string line, out string prefix, out int contentStart, out bool isEmpty)
    {
        prefix = "";
        contentStart = 0;
        isEmpty = false;

        string trimmed = line.TrimStart();
        string indent = line[..(line.Length - trimmed.Length)];
        if (!trimmed.StartsWith("//")) return false;

        contentStart = indent.Length + 2;
        prefix = indent + "// ";

        string rest = contentStart <= line.Length ? line[contentStart..] : "";
        isEmpty = string.IsNullOrWhiteSpace(rest);
        return true;
    }

    /// <summary>Parses a <c>/* */</c> block-comment opener or continuation body line.</summary>
    private static bool TryBlockCommentContinuation(string line, out string prefix, out int contentStart)
    {
        prefix = "";
        contentStart = 0;

        string trimmed = line.TrimStart();
        string indent = line[..(line.Length - trimmed.Length)];

        if (trimmed.StartsWith("/*") && !trimmed.Contains("*/"))
        {
            contentStart = indent.Length + 2;
            prefix = indent + " * ";
            return true;
        }

        // A bare "*"-prefixed line is only treated as a block-comment body when the star is
        // followed by a space (or nothing) — "*ptr = 5;" (C/C++ pointer dereference) must not
        // be mistaken for a comment continuation.
        if (trimmed.StartsWith("*") && !trimmed.StartsWith("*/") && (trimmed.Length == 1 || trimmed[1] == ' '))
        {
            contentStart = indent.Length + 1;
            prefix = indent + "* ";
            return true;
        }

        return false;
    }
}
