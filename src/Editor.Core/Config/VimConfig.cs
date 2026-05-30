using Editor.Core.Engine;
using Editor.Core.Buffer;
using Editor.Core.Snippets;

namespace Editor.Core.Config;

public class VimConfig
{
    private const int ModelineScanLineCount = 5;
    private const int MaxForListItems = 1000;
    private const int MaxForIterations = 10000;
    private const int MaxFunctionCallDepth = 20;
    private const int MaxExecuteDepth = 20;

    public VimOptions Options { get; } = new();
    public Dictionary<string, string> NormalMaps { get; } = [];
    public Dictionary<string, string> InsertMaps { get; } = [];
    public Dictionary<string, string> VisualMaps { get; } = [];
    public Dictionary<string, string> Abbreviations { get; } = [];
    public Dictionary<string, string> Variables { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, VimFunctionDefinition> Functions { get; } = new(StringComparer.OrdinalIgnoreCase);
    public SnippetManager Snippets { get; } = new();
    public VimAutocmdRegistry Autocmds { get; } = new();
    public IReadOnlyList<string> ScriptNames => _scriptNames.AsReadOnly();

    // The mapleader character (default backslash, set by `let mapleader=...`)
    public string Leader { get; private set; } = "\\";
    private string? _currentAugroup;
    private readonly List<string> _scriptNames = [];
    private readonly HashSet<string> _activeScriptPaths = new(StringComparer.OrdinalIgnoreCase);
    private string? _currentScriptDirectory;
    private int _functionCallDepth;
    private int _executeDepth;

    public static VimConfig LoadFromFile(string path)
    {
        var cfg = new VimConfig();
        cfg.SourceFile(path);
        return cfg;
    }

    public static VimConfig LoadDefault()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var vimrcPath = Path.Combine(home, ".vimrc");
        if (!File.Exists(vimrcPath))
            vimrcPath = Path.Combine(home, "_vimrc");
        if (!File.Exists(vimrcPath))
            vimrcPath = Path.Combine(home, "init.vim");

        var cfg = new VimConfig();
        cfg.SourceFile(vimrcPath);

        // Allow project-local overrides when running from a workspace.
        var localVimrcPath = Path.Combine(Environment.CurrentDirectory, ".vimrc");
        if (File.Exists(localVimrcPath))
            cfg.SourceFile(localVimrcPath);

        return cfg;
    }

    public void RecordScript(string path)
    {
        var fullPath = Path.GetFullPath(path);
        _scriptNames.Add(fullPath);
    }

    public void ParseLines(IEnumerable<string> lines)
    {
        var normalizedLines = lines.Select(NormalizeConfigLine).ToArray();
        var iterationBudget = MaxForIterations;
        ProcessBlock(normalizedLines, 0, normalizedLines.Length, ref iterationBudget);
    }

    public string? ParseCommand(string cmd)
    {
        if (cmd.StartsWith("set ", StringComparison.OrdinalIgnoreCase))
        {
            // `set` can take multiple space-separated options on one line
            var args = cmd[4..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            foreach (var arg in args)
                Options.Apply(arg);
            return null;
        }

        // let mapleader = "..." or let mapleader = '\<Space>'
        if (IsMapLeaderLetCommand(cmd))
        {
            Leader = ParseLeader(cmd);
            Variables["mapleader"] = Leader;
            return null;
        }

        if (TryParseLetAssignment(cmd, out var varName, out var varValue, out var letError))
        {
            if (letError != null) return letError;
            if (varName != null && varValue != null)
                Variables[varName] = varValue;
            return null;
        }

        // Map commands — strip modifier flags (<silent>, <nowait>, <buffer>, <expr>, <unique>)
        // before splitting into LHS/RHS.
        if (TryParseMapCommand(cmd, out var dict, out var rest))
        {
            ParseMap(dict!, rest!, Leader);
            return null;
        }

        // :iab / :iabbrev / :abbreviate / :ab — add abbreviation
        if (TryParseAbbrevCommand(cmd, out var abbrevLhs, out var abbrevRhs))
        {
            if (abbrevLhs != null && abbrevRhs != null)
                Abbreviations[abbrevLhs] = abbrevRhs;
            return null;
        }

        // :iunabbrev / :iuna / :unabbreviate / :una — remove abbreviation
        if (TryParseUnabbrevCommand(cmd, out var unabbrevLhs))
        {
            if (unabbrevLhs != null)
                Abbreviations.Remove(unabbrevLhs);
            return null;
        }

        // :snippet {trigger} {body} — define a user snippet
        if (cmd.StartsWith("snippet ", StringComparison.OrdinalIgnoreCase))
        {
            var snippetArgs = cmd[8..].Trim();
            var snippetSpace = snippetArgs.IndexOf(' ');
            if (snippetSpace > 0)
            {
                var trigger = snippetArgs[..snippetSpace].Trim();
                var body = snippetArgs[(snippetSpace + 1)..]; // preserve body as-is (may contain \n)
                if (!string.IsNullOrEmpty(trigger))
                    Snippets.Register(trigger, body);
            }
            return null;
        }

        // :unsnippet {trigger} — remove a user snippet
        if (cmd.StartsWith("unsnippet ", StringComparison.OrdinalIgnoreCase))
        {
            Snippets.Unregister(cmd[10..].Trim());
            return null;
        }

        if (TryParseAugroupCommand(cmd))
            return null;

        if (TryParseAutocmdCommand(cmd, out var autocmdError))
            return autocmdError;

        if (TryParseCallCommand(cmd, out var callName, out var callArgs))
        {
            var iterationBudget = MaxForIterations;
            return ExecuteFunctionCall(callName, callArgs, ref iterationBudget);
        }

        if (TryParseExecuteCommand(cmd, out var executeExpression))
        {
            if (_executeDepth >= MaxExecuteDepth)
                return "E169: Command too recursive";

            var resolved = EvalLetExpression(executeExpression);
            if (string.IsNullOrWhiteSpace(resolved))
                return "E471: Argument required";

            _executeDepth++;
            try { return ParseCommand(resolved); }
            finally { _executeDepth--; }
        }

        if (cmd.StartsWith("echo ", StringComparison.OrdinalIgnoreCase) ||
            cmd.StartsWith("echomsg ", StringComparison.OrdinalIgnoreCase) ||
            cmd.Equals("echo", StringComparison.OrdinalIgnoreCase) ||
            cmd.Equals("echomsg", StringComparison.OrdinalIgnoreCase))
        {
            // Source-time messages are currently not surfaced by VimConfig, but
            // evaluating the expression keeps :echo valid inside functions.
            var space = cmd.IndexOf(' ');
            if (space >= 0)
                _ = EvalLetExpression(cmd[(space + 1)..].Trim());
            return null;
        }

        if (cmd.StartsWith("colorscheme ", StringComparison.OrdinalIgnoreCase))
        {
            Options.ColorScheme = cmd[12..].Trim();
            return null;
        }
        if (cmd.StartsWith("syntax ", StringComparison.OrdinalIgnoreCase))
        {
            Options.Syntax = cmd[7..].Trim().Equals("on", StringComparison.OrdinalIgnoreCase);
            return null;
        }
        if (cmd.StartsWith("source ", StringComparison.OrdinalIgnoreCase) ||
            cmd.StartsWith("so ", StringComparison.OrdinalIgnoreCase))
        {
            var sourcePath = cmd[(cmd.IndexOf(' ') + 1)..].Trim();
            if (sourcePath.Length > 0)
            {
                var resolved = ResolveScriptPath(sourcePath);
                SourceFile(resolved);
            }
            return null;
        }

        // Silently ignore unsupported Vimscript commands such as filetype,
        // scriptencoding, tnoremap, onoremap, cnoremap, cmap.
        return null;
    }

    public void ApplyModelines(VimBuffer buffer)
    {
        if (!Options.Modeline)
            return;

        foreach (var line in GetModelineCandidateLines(buffer.Text))
        {
            if (!TryExtractModelineOptions(line, out var settings))
                continue;

            foreach (var setting in settings.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                Options.Apply(setting);
        }

        buffer.FileFormat = Options.FileFormat;
        buffer.FileEncoding = Options.FileEncoding;
    }

    public void SourceFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath) || !_activeScriptPaths.Add(fullPath))
            return;

        RecordScript(fullPath);

        var previousDirectory = _currentScriptDirectory;
        _currentScriptDirectory = Path.GetDirectoryName(fullPath);
        try
        {
            ParseLines(File.ReadAllLines(fullPath));
        }
        finally
        {
            _currentScriptDirectory = previousDirectory;
            _activeScriptPaths.Remove(fullPath);
        }
    }

    private static string NormalizeConfigLine(string rawLine)
    {
        var line = rawLine.Trim();
        if (string.IsNullOrEmpty(line) || line.StartsWith('"'))
            return "";

        // Remove inline comments: only strip " when preceded by whitespace
        // (avoids cutting strings like let mapleader="\<Space>")
        char quote = '\0';
        var escaped = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];

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

                if (quote == '\'' && ch == '\'' && i + 1 < line.Length && line[i + 1] == '\'')
                {
                    i++;
                    continue;
                }

                if (ch == quote)
                    quote = '\0';

                continue;
            }

            if (ch == '\'')
            {
                quote = ch;
                continue;
            }

            if (ch == '"')
            {
                if (IsInlineCommentQuote(line, i))
                    return line[..i].Trim();

                quote = ch;
            }
        }

        return line;
    }

    private static bool IsInlineCommentQuote(string line, int quoteIndex)
    {
        if (quoteIndex == 0 || !char.IsWhiteSpace(line[quoteIndex - 1]))
            return false;

        var previous = quoteIndex - 1;
        while (previous >= 0 && char.IsWhiteSpace(line[previous]))
            previous--;

        if (previous < 0)
            return true;

        return line[previous] is not ('=' or '[' or '(' or ',');
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

    private int ProcessBlock(IReadOnlyList<string> lines, int start, int end, ref int iterationBudget)
    {
        var index = start;
        while (index < end)
        {
            var line = lines[index];
            if (line.Length == 0)
            {
                index++;
                continue;
            }

            if (IsBlockTerminator(line))
                return index;

            if (TryParseIfCommand(line, out var ifExpression))
            {
                index = ExecuteIfBlock(lines, index, end, ifExpression, ref iterationBudget);
                continue;
            }

            if (TryParseForCommand(line, out var loopVariable, out var listExpression))
            {
                index = ExecuteForBlock(lines, index, end, loopVariable, listExpression, ref iterationBudget);
                continue;
            }

            if (TryParseFunctionCommand(line, out var functionName, out var parameters))
            {
                index = DefineFunctionBlock(lines, index, end, functionName, parameters);
                continue;
            }

            ParseCommand(line);
            index++;
        }

        return index;
    }

    private int ExecuteIfBlock(
        IReadOnlyList<string> lines,
        int ifIndex,
        int end,
        string expression,
        ref int iterationBudget)
    {
        var (elseIndex, endifIndex) = FindIfBlock(lines, ifIndex, end);
        var blockEnd = endifIndex >= 0 ? endifIndex : end;

        if (EvaluateCondition(expression))
        {
            var trueEnd = elseIndex >= 0 ? elseIndex : blockEnd;
            ProcessBlock(lines, ifIndex + 1, trueEnd, ref iterationBudget);
        }
        else if (elseIndex >= 0)
        {
            ProcessBlock(lines, elseIndex + 1, blockEnd, ref iterationBudget);
        }

        return endifIndex >= 0 ? endifIndex + 1 : end;
    }

    private int ExecuteForBlock(
        IReadOnlyList<string> lines,
        int forIndex,
        int end,
        string variableName,
        string listExpression,
        ref int iterationBudget)
    {
        var endforIndex = FindForBlockEnd(lines, forIndex, end);
        if (endforIndex < 0)
            return end;

        if (!IsValidVariableName(variableName) ||
            !TryParseListLiteral(listExpression, out var items))
            return endforIndex + 1;

        foreach (var item in items.Take(MaxForListItems))
        {
            if (iterationBudget <= 0)
                break;

            iterationBudget--;
            Variables[variableName] = item;
            ProcessBlock(lines, forIndex + 1, endforIndex, ref iterationBudget);
        }

        return endforIndex + 1;
    }

    private int DefineFunctionBlock(
        IReadOnlyList<string> lines,
        int functionIndex,
        int end,
        string functionName,
        IReadOnlyList<string> parameters)
    {
        var endfunctionIndex = FindFunctionBlockEnd(lines, functionIndex, end);
        if (endfunctionIndex < 0)
            return end;

        if (IsValidFunctionName(functionName) && parameters.All(IsValidArgumentName))
            Functions[functionName] = new VimFunctionDefinition(
                functionName,
                parameters.ToArray(),
                lines.Skip(functionIndex + 1).Take(endfunctionIndex - functionIndex - 1).ToArray());

        return endfunctionIndex + 1;
    }

    private string? ExecuteFunctionCall(string functionName, IReadOnlyList<string> argumentExpressions, ref int iterationBudget)
    {
        if (!Functions.TryGetValue(functionName, out var function))
            return "E117: Unknown function: " + functionName;

        if (_functionCallDepth >= MaxFunctionCallDepth)
            return "E132: Function call depth is higher than 'maxfuncdepth'";

        if (argumentExpressions.Count != function.Parameters.Count)
            return "E118: Too many arguments for function: " + functionName;

        var boundVariables = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["a:0"] = Variables.TryGetValue("a:0", out var previousCount) ? previousCount : null
        };

        var evaluatedArgs = argumentExpressions.Select(EvalLetExpression).ToArray();
        for (var i = 0; i < function.Parameters.Count; i++)
        {
            var scopedName = "a:" + function.Parameters[i];
            boundVariables[scopedName] = Variables.TryGetValue(scopedName, out var previous) ? previous : null;
            Variables[scopedName] = evaluatedArgs[i];
        }
        Variables["a:0"] = evaluatedArgs.Length.ToString();

        _functionCallDepth++;
        try
        {
            ProcessBlock(function.Body, 0, function.Body.Count, ref iterationBudget);
            return null;
        }
        finally
        {
            _functionCallDepth--;
            foreach (var (name, previousValue) in boundVariables)
            {
                if (previousValue == null)
                    Variables.Remove(name);
                else
                    Variables[name] = previousValue;
            }
        }
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

    private static int FindFunctionBlockEnd(IReadOnlyList<string> lines, int functionIndex, int end)
    {
        var depth = 0;

        for (var index = functionIndex + 1; index < end; index++)
        {
            var line = lines[index];
            if (line.Length == 0)
                continue;

            if (TryParseFunctionCommand(line, out _, out _))
            {
                depth++;
                continue;
            }

            if (line.Equals("endfunction", StringComparison.OrdinalIgnoreCase))
            {
                if (depth == 0)
                    return index;

                depth--;
            }
        }

        return -1;
    }

    private static bool IsBlockTerminator(string line) =>
        line.Equals("else", StringComparison.OrdinalIgnoreCase) ||
        line.Equals("endif", StringComparison.OrdinalIgnoreCase) ||
        line.Equals("endfor", StringComparison.OrdinalIgnoreCase) ||
        line.Equals("endfunction", StringComparison.OrdinalIgnoreCase);

    private static bool TryParseFunctionCommand(string line, out string name, out IReadOnlyList<string> parameters)
    {
        name = "";
        parameters = [];

        if (!line.StartsWith("function", StringComparison.OrdinalIgnoreCase))
            return false;

        var restStart = "function".Length;
        if (line.Length > restStart && line[restStart] == '!')
            restStart++;
        else if (line.Length > restStart && !char.IsWhiteSpace(line[restStart]))
            return false;

        var rest = line.Length > restStart ? line[restStart..].Trim() : "";
        var openParen = rest.IndexOf('(');
        if (openParen <= 0 || !rest.EndsWith(')'))
            return false;

        name = rest[..openParen].Trim();
        var rawParameters = rest[(openParen + 1)..^1].Trim();
        if (rawParameters.Length == 0)
            return true;

        if (!TrySplitCommaSeparated(rawParameters, out var parts))
            return false;

        parameters = parts.Select(p => p.Trim()).Where(p => p.Length > 0).ToArray();
        return parameters.Count == parts.Count;
    }

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

    private static bool TryParseExecuteCommand(string cmd, out string expression)
    {
        expression = "";
        if (!cmd.StartsWith("execute", StringComparison.OrdinalIgnoreCase) ||
            (cmd.Length > 7 && !char.IsWhiteSpace(cmd[7])))
            return false;

        expression = cmd.Length > 7 ? cmd[7..].Trim() : "";
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

    private static bool TryParseListLiteral(string expression, out List<string> items)
    {
        items = [];
        expression = expression.Trim();
        if (expression.Length < 2 || expression[0] != '[' || expression[^1] != ']')
            return false;

        var content = expression[1..^1].Trim();
        if (content.Length == 0)
            return true;

        if (!TrySplitListItems(content, out var rawItems))
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

    private static bool TrySplitListItems(string content, out List<string> items)
    {
        if (!TrySplitCommaSeparated(content, out items, rejectNestedListBrackets: true))
            return false;

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

    private static IEnumerable<string> GetModelineCandidateLines(TextBuffer text)
    {
        var count = Math.Min(ModelineScanLineCount, text.LineCount);
        var indexes = new SortedSet<int>();

        for (int i = 0; i < count; i++)
            indexes.Add(i);

        for (int i = Math.Max(0, text.LineCount - count); i < text.LineCount; i++)
            indexes.Add(i);

        foreach (var index in indexes)
            yield return text.GetLine(index);
    }

    private static bool TryExtractModelineOptions(string line, out string settings)
    {
        settings = "";

        var markerIndex = line.IndexOf("vim:", StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
            return false;

        var rest = line[(markerIndex + "vim:".Length)..].TrimStart();
        if (!rest.StartsWith("set ", StringComparison.OrdinalIgnoreCase))
            return false;

        var optionParts = new List<string>();
        foreach (var rawPart in rest[4..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (rawPart == ":")
            {
                settings = string.Join(' ', optionParts);
                return settings.Length > 0;
            }

            if (rawPart.EndsWith(':'))
            {
                var optionPart = rawPart[..^1];
                if (optionPart.Length > 0)
                    optionParts.Add(optionPart);

                settings = string.Join(' ', optionParts);
                return settings.Length > 0;
            }

            optionParts.Add(rawPart);
        }

        settings = "";
        return false;
    }

    private string ResolveScriptPath(string path)
    {
        path = path.Trim('"', '\'');
        if (Path.IsPathRooted(path))
            return path;

        var baseDir = _currentScriptDirectory ?? Environment.CurrentDirectory;
        return Path.Combine(baseDir, path);
    }

    private bool TryParseAugroupCommand(string cmd)
    {
        if (!cmd.StartsWith("augroup", StringComparison.OrdinalIgnoreCase))
            return false;

        var rest = cmd.Length > 7 ? cmd[7..].Trim() : "";
        if (rest.Equals("END", StringComparison.OrdinalIgnoreCase))
            _currentAugroup = null;
        else if (rest.Length > 0)
            _currentAugroup = rest;

        return true;
    }

    private bool TryParseAutocmdCommand(string cmd, out string? error)
    {
        error = null;
        if (!cmd.StartsWith("autocmd", StringComparison.OrdinalIgnoreCase) &&
            !cmd.StartsWith("au ", StringComparison.OrdinalIgnoreCase) &&
            !cmd.Equals("au", StringComparison.OrdinalIgnoreCase))
            return false;

        var rest = cmd.StartsWith("autocmd", StringComparison.OrdinalIgnoreCase)
            ? cmd[7..].Trim()
            : cmd.Length > 2 ? cmd[2..].Trim() : "";

        if (rest.StartsWith('!'))
        {
            rest = rest[1..].Trim();
            if (rest.Length == 0)
            {
                Autocmds.Clear(_currentAugroup);
                return true;
            }

            var clearParts = rest.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            string? clearGroup = _currentAugroup;
            string? clearEvent = null;
            string? clearPattern = null;
            if (clearParts.Length > 0)
            {
                if (IsAutocmdEventList(clearParts[0]))
                {
                    clearEvent = clearParts[0];
                    clearPattern = clearParts.Length > 1 ? clearParts[1].Trim() : null;
                }
                else
                {
                    clearGroup = clearParts[0];
                    clearEvent = clearParts.Length > 1 ? clearParts[1] : null;
                    clearPattern = clearParts.Length > 2 ? clearParts[2].Trim() : null;
                }
            }

            Autocmds.Clear(clearGroup, clearEvent, clearPattern);
            return true;
        }

        if (rest.Length == 0)
            return true;

        var parts = rest.Split(' ', 4, StringSplitOptions.RemoveEmptyEntries);
        string group;
        string eventList;
        string patternList;
        string command;

        if (parts.Length >= 3 && IsAutocmdEventList(parts[0]))
        {
            group = _currentAugroup ?? "";
            eventList = parts[0];
            patternList = parts[1];
            command = parts.Length == 4 ? $"{parts[2]} {parts[3]}" : parts[2];
        }
        else if (parts.Length >= 4 && IsAutocmdEventList(parts[1]))
        {
            group = parts[0];
            eventList = parts[1];
            patternList = parts[2];
            command = parts[3];
        }
        else
        {
            error = "E216: No such group or event";
            return true;
        }

        foreach (var eventName in eventList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var pattern in patternList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                Autocmds.Add(group, eventName, pattern, command);
        }

        return true;
    }

    private static bool IsAutocmdEventList(string value)
    {
        foreach (var eventName in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!SupportedAutocmdEvents.Contains(eventName))
                return false;
        }

        return value.Length > 0;
    }

    private static readonly HashSet<string> SupportedAutocmdEvents = new(StringComparer.OrdinalIgnoreCase)
    {
        "BufReadPre",
        "BufRead",
        "BufReadPost",
        "BufEnter",
        "FileType",
    };

    private bool TryParseMapCommand(string cmd, out Dictionary<string, string>? dict, out string? rest)
    {
        dict = null;
        rest = null;

        // Determine the map command prefix and target dictionary
        Dictionary<string, string>? target = null;
        string? suffix = null;

        var prefixes = new (string prefix, Dictionary<string, string>? maps)[]
        {
            ("nnoremap ",  NormalMaps),
            ("nmap ",      NormalMaps),
            ("inoremap ",  InsertMaps),
            ("imap ",      InsertMaps),
            ("vnoremap ",  VisualMaps),
            ("vmap ",      VisualMaps),
            ("xnoremap ",  VisualMaps),  // x = visual (no select) — close enough
            ("xmap ",      VisualMaps),
            // operator-pending, terminal, command-line: silently skip
            ("onoremap ",  null),
            ("omap ",      null),
            ("tnoremap ",  null),
            ("tmap ",      null),
            ("cnoremap ",  null),
            ("cmap ",      null),
        };

        foreach (var (p, maps) in prefixes)
        {
            if (cmd.StartsWith(p, StringComparison.OrdinalIgnoreCase))
            {
                target = maps;
                suffix = cmd[p.Length..];
                break;
            }
        }

        if (suffix == null) return false;

        dict = target; // may be null → silently ignore
        rest = suffix;
        return true;
    }

    private static readonly string[] MapFlags = ["<silent>", "<nowait>", "<buffer>", "<expr>", "<unique>", "<special>"];

    private static void ParseMap(Dictionary<string, string>? maps, string rest, string leader)
    {
        // Strip modifier flags
        rest = rest.Trim();
        bool stripped;
        do
        {
            stripped = false;
            foreach (var flag in MapFlags)
            {
                if (rest.StartsWith(flag, StringComparison.OrdinalIgnoreCase))
                {
                    rest = rest[flag.Length..].TrimStart();
                    stripped = true;
                    break;
                }
            }
        } while (stripped);

        var parts = rest.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2) return;

        var lhs = ExpandLeader(parts[0], leader);
        var rhs = ExpandLeader(parts[1], leader);

        // Replace <Cmd>...<CR> with :...<CR> so the map executes an ex command
        rhs = rhs.Replace("<Cmd>", ":", StringComparison.OrdinalIgnoreCase);

        if (maps != null)
            maps[lhs] = rhs;
    }

    private static string ExpandLeader(string s, string leader)
    {
        // <Leader> → the leader character (escaped for map use)
        if (s.Contains("<Leader>", StringComparison.OrdinalIgnoreCase))
            s = s.Replace("<Leader>", leader, StringComparison.OrdinalIgnoreCase);
        return s;
    }

    private static string ParseLeader(string cmd)
    {
        // let mapleader = "\<Space>" or let mapleader = " " or let mapleader = ","
        var eqIdx = cmd.IndexOf('=');
        if (eqIdx < 0) return "\\";
        var val = cmd[(eqIdx + 1)..].Trim().Trim('"', '\'');

        // Handle special sequences
        return val.ToLowerInvariant() switch
        {
            "\\<space>" or "<space>" => " ",
            "\\<cr>" or "<cr>"       => "\r",
            "\\<tab>" or "<tab>"     => "\t",
            _ when val.Length == 1   => val,
            _ => "\\"
        };
    }

    private bool TryParseLetAssignment(string cmd, out string? name, out string? value, out string? error)
    {
        name = null;
        value = null;
        error = null;

        if (!cmd.StartsWith("let ", StringComparison.OrdinalIgnoreCase) && cmd != "let")
            return false;

        var rest = cmd.Length > 3 ? cmd[3..].Trim() : "";
        if (rest.Length == 0)
            return true;

        var eqIdx = rest.IndexOf('=');
        if (eqIdx < 0)
        {
            error = "E121: Undefined variable: " + rest;
            return true;
        }

        name = rest[..eqIdx].Trim();
        var expr = rest[(eqIdx + 1)..].Trim();
        if (!IsValidVariableName(name))
        {
            error = "E461: Illegal variable name: " + name;
            return true;
        }

        value = EvalLetExpression(expr);
        return true;
    }

    private static bool IsMapLeaderLetCommand(string cmd)
    {
        if (!cmd.StartsWith("let ", StringComparison.OrdinalIgnoreCase))
            return false;

        var rest = cmd[3..].Trim();
        var eqIdx = rest.IndexOf('=');
        if (eqIdx < 0)
            return rest.Equals("mapleader", StringComparison.OrdinalIgnoreCase);

        var name = rest[..eqIdx].Trim();
        return name.Equals("mapleader", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsValidVariableName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var bare = name.Length > 2 && name[1] == ':' ? name[2..] : name;
        if (bare.Length == 0 || !(char.IsLetter(bare[0]) || bare[0] == '_')) return false;
        return bare.All(ch => char.IsLetterOrDigit(ch) || ch == '_');
    }

    private static bool IsValidFunctionName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var bare = name.Length > 2 && name[1] == ':' ? name[2..] : name;
        if (bare.Length == 0 || !(char.IsLetter(bare[0]) || bare[0] == '_')) return false;
        return bare.All(ch => char.IsLetterOrDigit(ch) || ch is '_' or '#');
    }

    private static bool IsValidArgumentName(string name) =>
        name.Length > 0 &&
        (char.IsLetter(name[0]) || name[0] == '_') &&
        name.All(ch => char.IsLetterOrDigit(ch) || ch == '_');

    private static string StripQuotes(string s) =>
        s.Length >= 2 && ((s[0] == '"' && s[^1] == '"') || (s[0] == '\'' && s[^1] == '\''))
            ? s[1..^1]
            : s;

    private string EvalLetExpression(string expr)
    {
        expr = expr.Trim();

        if (expr.Length >= 2 &&
            ((expr[0] == '"' && expr[^1] == '"') ||
             (expr[0] == '\'' && expr[^1] == '\'')))
            return StripQuotes(expr);

        if (TryGetVariable(expr, out var variableValue))
            return variableValue;

        return ExpressionEvaluator.Evaluate(expr) ?? expr;
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

    private bool TryGetVariable(string name, out string value)
    {
        if (Variables.TryGetValue(name, out value!))
            return true;

        if (!name.Contains(':') && Variables.TryGetValue("g:" + name, out value!))
            return true;

        value = "";
        return false;
    }

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

    private static readonly string[] AbbrevPrefixes =
        ["iabbrev ", "iab ", "abbreviate ", "ab "];

    private static readonly string[] UnabbrevPrefixes =
        ["iunabbrev ", "iuna ", "unabbreviate ", "una "];

    // Returns true and sets suffix if cmd starts with any of the given prefixes.
    private static bool TryStripPrefix(string cmd, string[] prefixes, out string suffix)
    {
        foreach (var prefix in prefixes)
        {
            if (cmd.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                suffix = cmd[prefix.Length..].Trim();
                return true;
            }
        }
        suffix = "";
        return false;
    }

    private bool TryParseAbbrevCommand(string cmd, out string? lhs, out string? rhs)
    {
        lhs = null; rhs = null;
        if (!TryStripPrefix(cmd, AbbrevPrefixes, out var rest)) return false;
        var parts = rest.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2) { lhs = parts[0]; rhs = parts[1]; }
        return true;
    }

    private bool TryParseUnabbrevCommand(string cmd, out string? lhs)
    {
        if (!TryStripPrefix(cmd, UnabbrevPrefixes, out var rest)) { lhs = null; return false; }
        lhs = rest;
        return true;
    }
}

public sealed record VimFunctionDefinition(
    string Name,
    IReadOnlyList<string> Parameters,
    IReadOnlyList<string> Body);
