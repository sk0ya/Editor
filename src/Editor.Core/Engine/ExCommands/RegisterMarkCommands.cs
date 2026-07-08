using Editor.Core.Buffer;
using Editor.Core.Marks;
using Editor.Core.Models;
using Editor.Core.Registers;

namespace Editor.Core.Engine.ExCommands;

/// <summary>Handles :yank, :put, :registers, :marks, and :delmarks.</summary>
public class RegisterMarkCommands(
    BufferManager bufferManager,
    MarkManager markManager,
    RegisterManager? registerManager,
    RangeResolver rangeResolver)
{
    public bool TryHandle(string cmd, string range, CursorPosition cursor, out ExResult result)
    {
        // :[range]yank [reg] — yank lines to register
        if (cmd is "y" or "yank" || cmd.StartsWith("y ") || cmd.StartsWith("yank "))
        {
            result = ExecuteYank(cmd, range, cursor);
            return true;
        }

        // :[line]put [reg] — paste register after line
        if (cmd is "pu" or "put" || cmd.StartsWith("pu ") || cmd.StartsWith("put "))
        {
            result = ExecutePut(cmd, range, cursor);
            return true;
        }

        // :registers [names]  :reg [names] — display register contents
        if (cmd == "registers" || cmd == "reg" ||
            cmd.StartsWith("registers ") || cmd.StartsWith("reg "))
        {
            result = ExecuteRegisters(cmd);
            return true;
        }

        // :marks [names] — display marks
        if (cmd == "marks" || cmd.StartsWith("marks "))
        {
            result = ExecuteMarks(cmd);
            return true;
        }

        // :delmarks {marks} / :delmarks! — delete marks
        if (cmd is "delmarks" or "delm" or "delmarks!" or "delm!" ||
            cmd.StartsWith("delmarks ") || cmd.StartsWith("delm "))
        {
            result = ExecuteDelmarks(cmd);
            return true;
        }

        result = default!;
        return false;
    }

    private ExResult ExecuteYank(string cmd, string range, CursorPosition cursor)
    {
        char regName = '"';
        var parts = cmd.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 1 && parts[1].Length == 1 && char.IsLetter(parts[1][0]))
            regName = char.ToLower(parts[1][0]);

        var buf = bufferManager.Current.Text;
        int startLine = cursor.Line, endLine = cursor.Line;
        if (!string.IsNullOrEmpty(range))
            rangeResolver.ResolveRange(range, cursor, buf.LineCount, ref startLine, ref endLine);

        if (registerManager == null)
            return new ExResult(false, "No register manager available");

        var text = string.Join("\n", buf.GetLines(startLine, endLine));
        registerManager.SetYank(regName, new Register(text, RegisterType.Line));
        return new ExResult(true, $"{endLine - startLine + 1} line(s) yanked");
    }

    private ExResult ExecutePut(string cmd, string range, CursorPosition cursor)
    {
        char regName = '"';
        var parts = cmd.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 1 && parts[1].Length == 1 && char.IsLetter(parts[1][0]))
            regName = char.ToLower(parts[1][0]);

        if (registerManager == null)
            return new ExResult(false, "No register manager available");

        var reg = registerManager.Get(regName);
        if (string.IsNullOrEmpty(reg.Text))
            return new ExResult(false, $"Nothing in register {regName}");

        var buf = bufferManager.Current.Text;
        int insertAfter = cursor.Line;
        if (!string.IsNullOrEmpty(range))
        {
            int rStart = cursor.Line, rEnd = cursor.Line;
            rangeResolver.ResolveRange(range, cursor, buf.LineCount, ref rStart, ref rEnd);
            insertAfter = rEnd;
        }

        var pasteLines = reg.Text.Split('\n');
        buf.InsertLines(insertAfter, pasteLines);

        return new ExResult(true, null, null, TextModified: true);
    }

    private ExResult ExecuteRegisters(string cmd)
    {
        if (registerManager == null)
            return new ExResult(false, "No register manager available");

        var filter = RangeResolver.GetCommandArg(cmd);
        var allRegs = registerManager.GetAll();
        if (allRegs.Count == 0)
            return new ExResult(true, "--- Registers ---\n(empty)");

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("--- Registers ---");
        bool any = false;

        foreach (var (name, reg) in allRegs)
        {
            if (filter.Length > 0 && !filter.Contains(name))
                continue;

            // Represent newlines as ^J (Vim-compatible)
            var display = reg.Text.Replace("\n", "^J");
            if (display.Length > 200) display = display[..200] + "…";
            var typeLabel = reg.Type switch { RegisterType.Line => "l", RegisterType.Block => "b", _ => "c" };
            sb.AppendLine($"\"{name}   {typeLabel}   {display}");
            any = true;
        }

        if (!any) sb.AppendLine("(no matches)");
        return new ExResult(true, sb.ToString().TrimEnd());
    }

    private ExResult ExecuteMarks(string cmd)
    {
        var filter = RangeResolver.GetCommandArg(cmd);
        var allMarks = markManager.GetAllMarks();
        if (allMarks.Count == 0)
            return new ExResult(true, "mark  line  col  text\n(no marks set)");

        var buf = bufferManager.Current.Text;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("mark  line  col  text");
        bool any = false;

        foreach (var (name, pos) in allMarks)
        {
            if (filter.Length > 0 && !filter.Contains(name))
                continue;

            var lineText = pos.Line >= 0 && pos.Line < buf.LineCount
                ? buf.GetLine(pos.Line).TrimStart()
                : "";
            if (lineText.Length > 50) lineText = lineText[..50] + "…";
            sb.AppendLine($" {name}  {pos.Line + 1,5}  {pos.Column,3}  {lineText}");
            any = true;
        }

        if (!any) sb.AppendLine("(no matches)");
        return new ExResult(true, sb.ToString().TrimEnd());
    }

    private ExResult ExecuteDelmarks(string cmd)
    {
        if (cmd is "delmarks!" or "delm!")
        {
            markManager.ClearMarks();
            return new ExResult(true, "All marks deleted");
        }

        var arg = RangeResolver.GetCommandArg(cmd);
        if (string.IsNullOrWhiteSpace(arg))
            return new ExResult(false, "E471: Argument required");

        if (!TryParseMarkList(arg, out var marks, out var error))
            return new ExResult(false, error);

        int deleted = 0;
        foreach (var mark in marks)
        {
            if (markManager.DeleteMark(mark))
                deleted++;
        }

        return new ExResult(true, $"{deleted} mark(s) deleted");
    }

    private static bool TryParseMarkList(string arg, out IReadOnlyList<char> marks, out string? error)
    {
        var result = new List<char>();
        error = null;

        for (int i = 0; i < arg.Length; i++)
        {
            var ch = arg[i];
            if (char.IsWhiteSpace(ch))
                continue;

            if (!IsValidMarkName(ch))
            {
                marks = [];
                error = $"E475: Invalid argument: {arg}";
                return false;
            }

            if (i + 2 < arg.Length && arg[i + 1] == '-' && IsValidMarkName(arg[i + 2]))
            {
                var end = arg[i + 2];
                if (ch > end)
                {
                    marks = [];
                    error = $"E475: Invalid argument: {arg}";
                    return false;
                }

                for (char mark = ch; mark <= end; mark++)
                {
                    if (IsValidMarkName(mark))
                        result.Add(mark);
                }
                i += 2;
                continue;
            }

            result.Add(ch);
        }

        marks = result.Distinct().ToList();
        return true;
    }

    private static bool IsValidMarkName(char mark)
    {
        return char.IsLetter(mark) || mark is '<' or '>' or '.' or '\'';
    }
}
