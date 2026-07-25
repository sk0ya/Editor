namespace Editor.Core.Engine;

public enum CommandState { Incomplete, Complete, Invalid }

public enum CommandParseDiagnosticCode
{
    InvalidInput
}

public sealed record CommandParseDiagnostic(
    CommandParseDiagnosticCode Code,
    string Input,
    string Message);

public sealed record CommandParseResult(
    CommandState State,
    ParsedCommand? Command,
    CommandGrammarMatch? PendingGrammar,
    CommandParseDiagnostic? Diagnostic);

public record struct ParsedCommand(
    int Count,
    string? Operator,       // d, c, y, >, <, =, g, z
    string Motion,          // hjkl, w, b, e, gg, G, f{c}, etc.
    char? Register,         // "a, "b, ...
    char? FindChar,         // for f/F/t/T motions
    bool LinewiseForced
);

public class CommandParser
{
    private readonly PendingInputController _pendingInput;
    private readonly CommandGrammar _grammar;
    private readonly CommandGrammar _builtInGrammar = CommandGrammar.CreateBuiltIn();
    private string _buffer = "";
    private char? _pendingRegister;
    private char? _pendingFindChar;
    private bool _ownsPendingInput;
    private char? _lastFindChar;
    private bool _lastFindForward;
    private bool _lastFindBefore;

    public char? LastFindChar => _lastFindChar;
    public bool LastFindForward => _lastFindForward;
    public bool LastFindBefore => _lastFindBefore;

    public string Buffer => _buffer;

    public CommandParser(
        PendingInputController? pendingInput = null,
        CommandGrammar? grammar = null)
    {
        _pendingInput = pendingInput ?? new PendingInputController();
        _grammar = grammar ?? new CommandGrammar();
    }

    public void Reset()
    {
        _buffer = "";
        _pendingRegister = null;
        _pendingFindChar = null;
        CancelPendingInput();
    }

    public (CommandState State, ParsedCommand? Command) Feed(string key)
    {
        // Awaiting find char (f/F/t/T)
        if (_ownsPendingInput && _pendingInput.Current is PendingInputState.FindCharacter)
        {
            if (key.Length == 1)
            {
                _pendingFindChar = key[0];
                _lastFindChar = key[0];
                CancelPendingInput();
                _buffer += key;
                return TryParse();
            }
            Reset();
            return (CommandState.Invalid, null);
        }

        // Awaiting register name ("x)
        if (_ownsPendingInput && _pendingInput.Current is PendingInputState.NormalRegister)
        {
            if (key.Length == 1)
            {
                _pendingRegister = key[0];
                CancelPendingInput();
                _buffer += key;
                return (CommandState.Incomplete, null);
            }
            Reset();
            return (CommandState.Invalid, null);
        }

        // Register prefix
        if (key == "\"" && string.IsNullOrEmpty(_buffer))
        {
            BeginPendingInput(new PendingInputState.NormalRegister());
            _buffer += key;
            return (CommandState.Incomplete, null);
        }

        _buffer += key;
        return TryParse();
    }

    public CommandParseResult FeedDetailed(string key)
    {
        var (state, command) = Feed(key);
        var grammarMatch = state == CommandState.Incomplete
            ? MatchCurrentGrammar()
            : null;
        var diagnostic = state == CommandState.Invalid
            ? new CommandParseDiagnostic(
                CommandParseDiagnosticCode.InvalidInput,
                _buffer,
                "Input does not match a registered command definition.")
            : null;
        return new CommandParseResult(state, command, grammarMatch, diagnostic);
    }

    private (CommandState, ParsedCommand?) TryParse()
    {
        var buf = _buffer;
        // Strip register prefix
        string working = buf;
        if (_pendingRegister.HasValue)
            working = buf[2..]; // skip "x

        // Parse count
        int i = 0;
        while (i < working.Length && char.IsDigit(working[i]) && !(i == 0 && working[i] == '0'))
            i++;
        int count = i > 0 && int.TryParse(working[..i], out var n) ? n : 1;
        string rest = working[i..];

        if (rest.Length == 0) return (CommandState.Incomplete, null);
        if (TryParseRegistered(rest, count, null, out var registered))
            return registered;

        // Surround actions with literal arguments.
        if (rest.StartsWith("cs"))
        {
            if (rest.Length < 4) return (CommandState.Incomplete, null);
            return Finalize(count, null, rest[..4]); // motion = "cs{from}{to}"
        }
        if (rest.StartsWith("ds"))
        {
            if (rest.Length < 3) return (CommandState.Incomplete, null);
            return Finalize(count, null, rest[..3]); // motion = "ds{char}"
        }

        var op = FindOperator(rest)?.Sequence;

        if (op != null)
        {
            string afterOp = rest[op.Length..];

            // Double-operator: dd, cc, yy, >>, <<
            if (afterOp.Length > 0 && afterOp[0] == op[^1])
            {
                return Finalize(count, op, op, linewise: true);
            }

            // Linewise: dV, cV etc. — not standard, skip

            if (afterOp.Length == 0)
            {
                return (CommandState.Incomplete, null);
            }

            int motionCount = 1;
            int motionCountEnd = 0;
            while (motionCountEnd < afterOp.Length &&
                   char.IsDigit(afterOp[motionCountEnd]) &&
                   !(motionCountEnd == 0 && afterOp[motionCountEnd] == '0'))
            {
                motionCountEnd++;
            }

            if (motionCountEnd > 0)
            {
                motionCount = int.TryParse(afterOp[..motionCountEnd], out var parsedMotionCount) ? parsedMotionCount : 1;
                afterOp = afterOp[motionCountEnd..];
                count *= motionCount;

                if (afterOp.Length == 0)
                    return (CommandState.Incomplete, null);

                if (afterOp.Length > 0 && afterOp[0] == op[^1])
                    return Finalize(count, op, op, linewise: true);
            }

            // Motion after operator
            return ParseMotion(afterOp, count, op);
        }

        // No operator — standalone motion or action
        return ParseMotion(rest, count, null);
    }

    private (CommandState, ParsedCommand?) ParseMotion(string s, int count, string? op)
    {
        if (s.Length == 0) return (CommandState.Incomplete, null);
        if (TryParseRegistered(s, count, op, out var registered))
            return registered;

        if (op != null && s.Length >= 2 && s[0] is 'i' or 'a')
            return (CommandState.Invalid, null);

        // Two-char motions
        if (s == "g" || s == "z") return (CommandState.Incomplete, null);

        // Find char motions
        if (s[0] is 'f' or 'F' or 't' or 'T')
        {
            if (s.Length < 2)
            {
                BeginPendingInput(new PendingInputState.FindCharacter(s[0]));
                // Remember direction
                _lastFindForward = s[0] is 'f' or 't';
                _lastFindBefore = s[0] is 't' or 'T';
                return (CommandState.Incomplete, null);
            }
            _lastFindChar = s[1];
            _lastFindForward = s[0] is 'f' or 't';
            _lastFindBefore = s[0] is 't' or 'T';
            return Finalize(count, op, s[0..1], findChar: s[1]);
        }

        // r needs next char
        if (s == "r")
        {
            BeginPendingInput(new PendingInputState.ReplaceCharacter());
            return (CommandState.Incomplete, null);
        }

        if (s.StartsWith("g") || s.StartsWith("z") || s.StartsWith("Z"))
            return (CommandState.Invalid, null);
        if (s.Length == 2 && s[0] == 'r') return Finalize(count, op, s, findChar: s[1]);

        // m and ` and ' need next char
        if (s == "m")
        {
            BeginPendingInput(new PendingInputState.SetMark());
            return (CommandState.Incomplete, null);
        }
        if (s is "`" or "'")
        {
            BeginPendingInput(new PendingInputState.JumpToMark(s == "'"));
            return (CommandState.Incomplete, null);
        }
        if (s.Length == 2 && s[0] is 'm' or '`' or '\'') return Finalize(count, op, s);

        // @ and q need register
        if (s is "q" or "@") return (CommandState.Incomplete, null);
        if (s.Length == 2 && s[0] is 'q' or '@') return Finalize(count, op, s);

        // ] and [ prefixed motions: ]s (next misspell), [s (prev misspell),
        // ]] ][ [[ [] (section jumps), ]} ]) [{ [( (block jumps)
        if (s is "]" or "[") return (CommandState.Incomplete, null);
        if (s.Length >= 2 && s[0] is ']' or '[')
        {
            // Three-char sequences like ][  are handled as two-char bracket motions
            // All two-char [ and ] prefixed motions complete here
            return Finalize(count, op, s[..2]);
        }

        return Finalize(count, op, s);
    }

    private (CommandState, ParsedCommand?) Finalize(int count = 1, string? op = null, string motion = "",
        bool linewise = false, char? findChar = null)
    {
        var cmd = new ParsedCommand(count, op, motion, _pendingRegister, findChar ?? _pendingFindChar, linewise);
        Reset();
        return (CommandState.Complete, cmd);
    }

    private (CommandState, ParsedCommand?) Finalize() => Finalize(1, null, _buffer);

    private bool TryParseRegistered(
        string sequence,
        int count,
        string? op,
        out (CommandState State, ParsedCommand? Command) result)
    {
        var match = _grammar.Match(sequence);
        if (match.Kind == CommandGrammarMatchKind.None)
            match = _builtInGrammar.Match(sequence);
        if (match.Kind == CommandGrammarMatchKind.None)
        {
            result = default;
            return false;
        }
        if (match.Kind == CommandGrammarMatchKind.Prefix)
        {
            result = (CommandState.Incomplete, null);
            return true;
        }

        var definition = match.Definition!;
        if (definition.Kind == CommandDefinitionKind.Operator)
        {
            result = default;
            return false;
        }

        var valid = op == null
            ? definition.Kind is CommandDefinitionKind.Action or CommandDefinitionKind.Motion
                or CommandDefinitionKind.TextObject
            : definition.Kind is CommandDefinitionKind.Motion or CommandDefinitionKind.TextObject;
        if (!valid && op != null && match.HasLongerMatches)
        {
            result = (CommandState.Incomplete, null);
            return true;
        }
        result = valid
            ? Finalize(count, op, definition.Output ?? sequence)
            : (CommandState.Invalid, null);
        return true;
    }

    private CommandDefinition? FindOperator(string sequence)
    {
        var custom = _grammar.FindLongestPrefix(sequence, CommandDefinitionKind.Operator);
        var builtIn = _builtInGrammar.FindLongestPrefix(
            sequence, CommandDefinitionKind.Operator);
        return custom is null || (builtIn?.Sequence.Length ?? 0) > custom.Sequence.Length
            ? builtIn
            : custom;
    }

    private CommandGrammarMatch? MatchCurrentGrammar()
    {
        var sequence = _buffer;
        if (_pendingRegister.HasValue && sequence.Length >= 2)
            sequence = sequence[2..];
        var countLength = 0;
        while (countLength < sequence.Length &&
               char.IsDigit(sequence[countLength]) &&
               !(countLength == 0 && sequence[countLength] == '0'))
            countLength++;
        sequence = sequence[countLength..];

        var match = _grammar.Match(sequence);
        if (match.Kind == CommandGrammarMatchKind.None)
            match = _builtInGrammar.Match(sequence);
        return match.Kind == CommandGrammarMatchKind.None ? null : match;
    }

    private void BeginPendingInput(PendingInputState state)
    {
        _pendingInput.Begin(state);
        _ownsPendingInput = true;
    }

    private void CancelPendingInput()
    {
        if (!_ownsPendingInput)
            return;
        _pendingInput.Cancel();
        _ownsPendingInput = false;
    }
}
