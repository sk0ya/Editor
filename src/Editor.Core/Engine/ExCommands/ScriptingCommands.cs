using Editor.Core.Buffer;
using Editor.Core.Config;
using Editor.Core.Models;

namespace Editor.Core.Engine.ExCommands;

/// <summary>
/// Handles :let, :echo/:echomsg, :execute, :call, and the (interactive-error-only)
/// :function/:if/:for family. Function bodies (multi-line if/for/call blocks) are
/// evaluated here; :execute and per-line function-body execution recurse back into
/// the owning ExCommandProcessor via the <paramref name="execute"/>/<paramref name="executeNoHistory"/> delegates.
/// </summary>
public class ScriptingCommands(
    BufferManager bufferManager,
    Dictionary<string, string> variables,
    Dictionary<string, VimFunctionDefinition> functions,
    Func<string, CursorPosition, ExResult> execute,
    Func<string, CursorPosition, ExResult> executeNoHistory)
{
    private const int MaxFunctionCallDepth = 20;
    private const int MaxForListItems = 1000;
    private const int MaxForIterations = 10000;

    private int _executeDepth;
    private int _functionCallDepth;

    public bool TryHandle(string cmd, CursorPosition cursor, out ExResult result)
    {
        // :call Name(...) — execute a function defined while sourcing Vimscript.
        if (cmd == "call" || cmd.StartsWith("call "))
        {
            result = ExecuteFunctionCall(cmd, cursor);
            return true;
        }

        // Multi-line Vimscript functions are evaluated by VimConfig when
        // sourcing vimrc files. Interactive definition is intentionally minimal.
        if (cmd == "function" || cmd.StartsWith("function "))
        {
            result = new ExResult(false, "E126: Missing :endfunction");
            return true;
        }
        if (cmd == "endfunction")
        {
            result = new ExResult(false, "E193: :endfunction not inside a function");
            return true;
        }

        // Multi-line Vimscript conditionals are evaluated by VimConfig when
        // sourcing vimrc files. Interactive one-line input is not stateful.
        if (cmd == "if" || cmd.StartsWith("if "))
        {
            result = new ExResult(false, "E580: :endif missing");
            return true;
        }
        if (cmd == "else")
        {
            result = new ExResult(false, "E581: :else without :if");
            return true;
        }
        if (cmd == "endif")
        {
            result = new ExResult(false, "E580: :endif without :if");
            return true;
        }

        // Multi-line Vimscript loops are evaluated by VimConfig when sourcing
        // vimrc files. Interactive one-line input is not stateful.
        if (cmd == "for" || cmd.StartsWith("for "))
        {
            result = new ExResult(false, "E170: Missing :endfor");
            return true;
        }
        if (cmd == "endfor")
        {
            result = new ExResult(false, "E588: :endfor without :for");
            return true;
        }

        // :let {var} = {expr} — assign a simple Vimscript variable.
        // :let with no argument lists currently stored variables.
        if (cmd == "let" || cmd.StartsWith("let "))
        {
            result = ExecuteLet(cmd);
            return true;
        }

        // :echo {expr} / :echomsg {expr} — print message
        if (cmd.StartsWith("echo ") || cmd == "echo" ||
            cmd.StartsWith("echomsg ") || cmd == "echomsg")
        {
            string expr;
            if (cmd.StartsWith("echomsg "))
                expr = cmd[8..].Trim();
            else if (cmd == "echomsg")
                expr = "";
            else
                expr = cmd.Length > 5 ? cmd[5..].Trim() : "";
            result = new ExResult(true, EvalExpr(expr));
            return true;
        }

        // :execute {expr} — evaluate string and run as Ex command
        if (cmd.StartsWith("execute ") || cmd == "execute")
        {
            if (_executeDepth >= 20)
            {
                result = new ExResult(false, "E169: Command too recursive");
                return true;
            }
            var expr = cmd.Length > 8 ? cmd[8..].Trim() : "";
            var resolved = EvalExpr(expr);
            if (string.IsNullOrEmpty(resolved))
            {
                result = new ExResult(false, "E471: Argument required");
                return true;
            }
            _executeDepth++;
            try { result = execute(resolved, cursor); }
            finally { _executeDepth--; }
            return true;
        }

        result = default!;
        return false;
    }

    private ExResult ExecuteFunctionCall(string cmd, CursorPosition cursor)
    {
        if (!TryParseCallCommand(cmd, out var functionName, out var argumentExpressions))
            return new ExResult(false, "E476: Invalid command");

        if (!functions.TryGetValue(functionName, out var function))
            return new ExResult(false, "E117: Unknown function: " + functionName);

        if (_functionCallDepth >= MaxFunctionCallDepth)
            return new ExResult(false, "E132: Function call depth is higher than 'maxfuncdepth'");

        if (argumentExpressions.Count < function.Parameters.Count)
            return new ExResult(false, "E119: Not enough arguments for function: " + functionName);
        if (argumentExpressions.Count > function.Parameters.Count)
            return new ExResult(false, "E118: Too many arguments for function: " + functionName);

        var boundVariables = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["a:0"] = variables.TryGetValue("a:0", out var previousCount) ? previousCount : null
        };

        var evaluatedArgs = argumentExpressions.Select(EvalExpr).ToArray();
        for (var i = 0; i < function.Parameters.Count; i++)
        {
            var scopedName = "a:" + function.Parameters[i];
            boundVariables[scopedName] = variables.TryGetValue(scopedName, out var previous) ? previous : null;
            variables[scopedName] = evaluatedArgs[i];
        }
        variables["a:0"] = evaluatedArgs.Length.ToString();

        _functionCallDepth++;
        try
        {
            var iterationBudget = MaxForIterations;
            return ExecuteFunctionBlock(function.Body, 0, function.Body.Count, cursor, ref iterationBudget).Result;
        }
        finally
        {
            _functionCallDepth--;
            foreach (var (name, previousValue) in boundVariables)
            {
                if (previousValue == null)
                    variables.Remove(name);
                else
                    variables[name] = previousValue;
            }
        }
    }

    private (int NextIndex, ExResult Result) ExecuteFunctionBlock(
        IReadOnlyList<string> lines,
        int start,
        int end,
        CursorPosition cursor,
        ref int iterationBudget)
    {
        var index = start;
        ExResult lastResult = new(true);

        while (index < end)
        {
            var line = lines[index];
            if (line.Length == 0)
            {
                index++;
                continue;
            }

            if (IsFunctionBlockTerminator(line))
                return (index, lastResult);

            if (TryParseIfCommand(line, out var ifExpression))
            {
                var branch = ExecuteFunctionIfBlock(lines, index, end, ifExpression, cursor, ref iterationBudget);
                if (!branch.Result.Success)
                    return branch;
                lastResult = branch.Result;
                index = branch.NextIndex;
                continue;
            }

            if (TryParseForCommand(line, out var loopVariable, out var listExpression))
            {
                var loop = ExecuteFunctionForBlock(lines, index, end, loopVariable, listExpression, cursor, ref iterationBudget);
                if (!loop.Result.Success)
                    return loop;
                lastResult = loop.Result;
                index = loop.NextIndex;
                continue;
            }

            lastResult = executeNoHistory(line, cursor);
            if (!lastResult.Success)
                return (index + 1, lastResult);

            index++;
        }

        return (index, lastResult);
    }

    private (int NextIndex, ExResult Result) ExecuteFunctionIfBlock(
        IReadOnlyList<string> lines,
        int ifIndex,
        int end,
        string expression,
        CursorPosition cursor,
        ref int iterationBudget)
    {
        var (elseIndex, endifIndex) = FindIfBlock(lines, ifIndex, end);
        var blockEnd = endifIndex >= 0 ? endifIndex : end;
        var result = new ExResult(true);

        if (EvaluateCondition(expression))
        {
            var trueEnd = elseIndex >= 0 ? elseIndex : blockEnd;
            result = ExecuteFunctionBlock(lines, ifIndex + 1, trueEnd, cursor, ref iterationBudget).Result;
        }
        else if (elseIndex >= 0)
        {
            result = ExecuteFunctionBlock(lines, elseIndex + 1, blockEnd, cursor, ref iterationBudget).Result;
        }

        return (endifIndex >= 0 ? endifIndex + 1 : end, result);
    }

    private (int NextIndex, ExResult Result) ExecuteFunctionForBlock(
        IReadOnlyList<string> lines,
        int forIndex,
        int end,
        string variableName,
        string listExpression,
        CursorPosition cursor,
        ref int iterationBudget)
    {
        var endforIndex = FindForBlockEnd(lines, forIndex, end);
        if (endforIndex < 0)
            return (end, new ExResult(false, "E170: Missing :endfor"));

        if (!IsValidVariableName(variableName) ||
            !TryParseListLiteral(listExpression, out var items))
            return (endforIndex + 1, new ExResult(true));

        ExResult lastResult = new(true);
        foreach (var item in items.Take(MaxForListItems))
        {
            if (iterationBudget <= 0)
                break;

            iterationBudget--;
            variables[variableName] = item;
            lastResult = ExecuteFunctionBlock(lines, forIndex + 1, endforIndex, cursor, ref iterationBudget).Result;
            if (!lastResult.Success)
                return (endforIndex + 1, lastResult);
        }

        return (endforIndex + 1, lastResult);
    }

    private ExResult ExecuteLet(string cmd)
    {
        var rest = cmd.Length > 3 ? cmd[3..].Trim() : "";
        if (rest.Length == 0)
        {
            if (variables.Count == 0)
                return new ExResult(true, "(no variables)");

            var lines = variables
                .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kv => $"{kv.Key} = {FormatVariableValue(kv.Value)}");
            return new ExResult(true, string.Join('\n', lines));
        }

        var eqIdx = rest.IndexOf('=');
        if (eqIdx < 0)
        {
            var name = rest.Trim();
            return variables.TryGetValue(name, out var value)
                ? new ExResult(true, $"{name} = {FormatVariableValue(value)}")
                : new ExResult(false, $"E121: Undefined variable: {name}");
        }

        var varName = rest[..eqIdx].Trim();
        var expr = rest[(eqIdx + 1)..].Trim();
        if (!IsValidVariableName(varName))
            return new ExResult(false, $"E461: Illegal variable name: {varName}");

        variables[varName] = EvalExpr(expr);
        return new ExResult(true);
    }

    private static bool IsValidVariableName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var bare = name.Length > 2 && name[1] == ':' ? name[2..] : name;
        if (bare.Length == 0 || !(char.IsLetter(bare[0]) || bare[0] == '_')) return false;
        return bare.All(ch => char.IsLetterOrDigit(ch) || ch == '_');
    }

    private static string FormatVariableValue(string value) =>
        int.TryParse(value, out _) ? value : $"\"{value}\"";

    private static bool TryParseCallCommand(string cmd, out string name, out IReadOnlyList<string> arguments)
    {
        name = "";
        arguments = [];

        if (!cmd.StartsWith("call", StringComparison.OrdinalIgnoreCase) ||
            (cmd.Length > 4 && !char.IsWhiteSpace(cmd[4])))
            return false;

        var rest = cmd.Length > 4 ? cmd[4..].Trim() : "";
        var openParen = rest.IndexOf('(');
        if (openParen <= 0 || !rest.EndsWith(')'))
            return false;

        name = rest[..openParen].Trim();
        var rawArguments = rest[(openParen + 1)..^1].Trim();
        if (rawArguments.Length == 0)
            return true;

        if (!TrySplitCommaSeparated(rawArguments, out var parts))
            return false;

        arguments = parts.Select(p => p.Trim()).ToArray();
        return arguments.All(arg => arg.Length > 0);
    }

    private static bool TryParseIfCommand(string line, out string expression)
    {
        expression = "";
        if (line.Length < 2 ||
            !line[..2].Equals("if", StringComparison.OrdinalIgnoreCase) ||
            (line.Length > 2 && !char.IsWhiteSpace(line[2])))
            return false;

        expression = line.Length > 2 ? line[2..].Trim() : "";
        return true;
    }

    private static bool TryParseForCommand(string line, out string variableName, out string listExpression)
    {
        variableName = "";
        listExpression = "";

        if (line.Length < 3 ||
            !line[..3].Equals("for", StringComparison.OrdinalIgnoreCase) ||
            (line.Length > 3 && !char.IsWhiteSpace(line[3])))
            return false;

        var rest = line.Length > 3 ? line[3..].TrimStart() : "";
        var variableEnd = 0;
        while (variableEnd < rest.Length && !char.IsWhiteSpace(rest[variableEnd]))
            variableEnd++;

        if (variableEnd == 0)
            return false;

        var inStart = variableEnd;
        while (inStart < rest.Length && char.IsWhiteSpace(rest[inStart]))
            inStart++;

        if (inStart + 2 > rest.Length ||
            !rest.AsSpan(inStart, 2).Equals("in", StringComparison.OrdinalIgnoreCase))
            return false;

        var expressionStart = inStart + 2;
        if (expressionStart < rest.Length && !char.IsWhiteSpace(rest[expressionStart]))
            return false;

        while (expressionStart < rest.Length && char.IsWhiteSpace(rest[expressionStart]))
            expressionStart++;

        if (expressionStart >= rest.Length)
            return false;

        variableName = rest[..variableEnd].Trim();
        listExpression = rest[expressionStart..].Trim();
        return true;
    }

    private static (int ElseIndex, int EndifIndex) FindIfBlock(IReadOnlyList<string> lines, int ifIndex, int end)
    {
        var depth = 0;
        var elseIndex = -1;

        for (var index = ifIndex + 1; index < end; index++)
        {
            var line = lines[index];
            if (line.Length == 0)
                continue;

            if (TryParseIfCommand(line, out _))
            {
                depth++;
                continue;
            }

            if (line.Equals("endif", StringComparison.OrdinalIgnoreCase))
            {
                if (depth == 0)
                    return (elseIndex, index);

                depth--;
                continue;
            }

            if (depth == 0 &&
                elseIndex < 0 &&
                line.Equals("else", StringComparison.OrdinalIgnoreCase))
            {
                elseIndex = index;
            }
        }

        return (elseIndex, -1);
    }

    private static int FindForBlockEnd(IReadOnlyList<string> lines, int forIndex, int end)
    {
        var depth = 0;

        for (var index = forIndex + 1; index < end; index++)
        {
            var line = lines[index];
            if (line.Length == 0)
                continue;

            if (TryParseForCommand(line, out _, out _))
            {
                depth++;
                continue;
            }

            if (line.Equals("endfor", StringComparison.OrdinalIgnoreCase))
            {
                if (depth == 0)
                    return index;

                depth--;
            }
        }

        return -1;
    }

    private bool EvaluateCondition(string expr)
    {
        expr = expr.Trim();
        if (expr.Length == 0)
            return false;

        if (expr[0] == '!')
            return !EvaluateCondition(expr[1..]);

        if (expr.Length >= 2 &&
            ((expr[0] == '"' && expr[^1] == '"') ||
             (expr[0] == '\'' && expr[^1] == '\'')))
            return IsTruthy(StripQuotes(expr));

        if (TryGetVariable(expr, out var variableValue))
            return IsTruthy(variableValue);

        if (ExpressionEvaluator.Evaluate(expr) is { } arithmeticValue)
            return IsTruthy(arithmeticValue);

        if (int.TryParse(expr, out var n))
            return n != 0;

        return false;
    }

    private static bool TryParseListLiteral(string expression, out List<string> items)
    {
        items = [];
        expression = expression.Trim();
        if (expression.Length < 2 || expression[0] != '[' || expression[^1] != ']')
            return false;

        var content = expression[1..^1].Trim();
        if (content.Length == 0)
            return true;

        if (!TrySplitCommaSeparated(content, out var rawItems, rejectNestedListBrackets: true))
            return false;

        foreach (var rawItem in rawItems)
        {
            var item = rawItem.Trim();
            if (item.Length == 0)
                return false;

            if (item.Length >= 2 &&
                ((item[0] == '"' && item[^1] == '"') ||
                 (item[0] == '\'' && item[^1] == '\'')))
            {
                if (items.Count < MaxForListItems)
                    items.Add(StripQuotes(item));
                continue;
            }

            var evaluated = ExpressionEvaluator.Evaluate(item);
            if (evaluated == null)
                return false;

            if (items.Count < MaxForListItems)
                items.Add(evaluated);
        }

        return true;
    }

    private static bool TrySplitCommaSeparated(
        string content,
        out List<string> items,
        bool rejectNestedListBrackets = false)
    {
        items = [];
        var start = 0;
        char quote = '\0';
        var escaped = false;

        for (var index = 0; index < content.Length; index++)
        {
            var ch = content[index];
            if (quote != '\0')
            {
                if (quote == '"' && escaped)
                {
                    escaped = false;
                    continue;
                }

                if (quote == '"' && ch == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (quote == '\'' && ch == '\'' && index + 1 < content.Length && content[index + 1] == '\'')
                {
                    index++;
                    continue;
                }

                if (ch == quote)
                    quote = '\0';
                continue;
            }

            if (ch is '\'' or '"')
            {
                quote = ch;
                continue;
            }

            if (rejectNestedListBrackets && ch is '[' or ']')
                return false;

            if (ch == ',')
            {
                items.Add(content[start..index]);
                start = index + 1;
            }
        }

        if (quote != '\0' || escaped)
            return false;

        items.Add(content[start..]);
        return true;
    }

    private static bool IsFunctionBlockTerminator(string line) =>
        line.Equals("else", StringComparison.OrdinalIgnoreCase) ||
        line.Equals("endif", StringComparison.OrdinalIgnoreCase) ||
        line.Equals("endfor", StringComparison.OrdinalIgnoreCase);

    private static bool IsTruthy(string value)
    {
        value = value.Trim();
        if (value.Length == 0)
            return false;

        if (double.TryParse(value, out var number))
            return number != 0;

        if (bool.TryParse(value, out var boolean))
            return boolean;

        return false;
    }

    private string EvalExpr(string expr)
    {
        expr = expr.Trim();

        // String literal: "..." or '...'
        if (expr.Length >= 2 &&
            ((expr[0] == '"' && expr[^1] == '"') ||
             (expr[0] == '\'' && expr[^1] == '\'')))
            return StripQuotes(expr);

        // expand('%'), expand('%:t'), expand('%:h'), expand('%:r'), expand('%:e')
        if (expr.StartsWith("expand(", StringComparison.Ordinal))
            return EvalExpand(expr);

        // strftime('fmt') / strftime("fmt")
        if (expr.StartsWith("strftime(", StringComparison.Ordinal))
            return EvalStrftime(expr);

        if (TryGetVariable(expr, out var variableValue))
            return variableValue;

        if (ExpressionEvaluator.Evaluate(expr) is { } arithmeticValue)
            return arithmeticValue;

        // Numeric literal
        if (int.TryParse(expr, out var n)) return n.ToString();

        // Bare word / unrecognised — return as-is
        return expr;
    }

    private bool TryGetVariable(string expr, out string value)
    {
        if (variables.TryGetValue(expr, out value!))
            return true;

        if (!expr.Contains(':') && variables.TryGetValue("g:" + expr, out value!))
            return true;

        value = "";
        return false;
    }

    private string EvalExpand(string expr)
    {
        // expr like: expand('%') or expand('%:t') or expand('%:h') etc.
        var inner = ExtractFunctionArg(expr, "expand");
        if (inner == null) return expr;

        inner = StripQuotes(inner);

        var filePath = bufferManager.Current.FilePath ?? "";

        return inner switch
        {
            "%"    => filePath,
            "%:t"  => Path.GetFileName(filePath),
            "%:h"  => Path.GetDirectoryName(filePath) ?? "",
            "%:r"  => Path.Combine(
                          Path.GetDirectoryName(filePath) ?? "",
                          Path.GetFileNameWithoutExtension(filePath)),
            "%:e"  => Path.GetExtension(filePath).TrimStart('.'),
            "%:p"  => string.IsNullOrEmpty(filePath) ? filePath : Path.GetFullPath(filePath),
            _      => filePath
        };
    }

    private static string EvalStrftime(string expr)
    {
        // expr like: strftime('%Y-%m-%d') or strftime("%H:%M")
        var fmt = ExtractFunctionArg(expr, "strftime");
        if (fmt == null) return expr;

        fmt = StripQuotes(fmt);

        // Convert strftime format specifiers to .NET equivalents
        fmt = fmt
            .Replace("%Y", "yyyy")
            .Replace("%y", "yy")
            .Replace("%m", "MM")
            .Replace("%d", "dd")
            .Replace("%H", "HH")
            .Replace("%M", "mm")
            .Replace("%S", "ss")
            .Replace("%A", "dddd")
            .Replace("%a", "ddd")
            .Replace("%B", "MMMM")
            .Replace("%b", "MMM")
            .Replace("%I", "hh")
            .Replace("%p", "tt");

        try { return DateTime.Now.ToString(fmt); }
        catch { return fmt; }
    }

    private static string? ExtractFunctionArg(string expr, string funcName)
    {
        // funcName + '(' ... ')'
        var prefix = funcName + "(";
        if (!expr.StartsWith(prefix, StringComparison.Ordinal)) return null;
        if (!expr.EndsWith(')')) return null;
        return expr[prefix.Length..^1].Trim();
    }

    private static string StripQuotes(string s) =>
        s.Length >= 2 && ((s[0] == '"' && s[^1] == '"') || (s[0] == '\'' && s[^1] == '\''))
            ? s[1..^1]
            : s;
}
