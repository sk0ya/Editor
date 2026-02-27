namespace Editor.Core.Engine;

public enum CommandState { Incomplete, Complete, Invalid }

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
    private string _buffer = "";
    private char? _pendingRegister;
    private char? _pendingFindChar;
    private bool _awaitingFindChar;
    private bool _awaitingRegister;
    private char? _lastFindChar;
    private bool _lastFindForward;
    private bool _lastFindBefore;

    public char? LastFindChar => _lastFindChar;
    public bool LastFindForward => _lastFindForward;
    public bool LastFindBefore => _lastFindBefore;

    public string Buffer => _buffer;

    public void Reset()
    {
        _buffer = "";
        _pendingRegister = null;
        _pendingFindChar = null;
        _awaitingFindChar = false;
        _awaitingRegister = false;
    }

    public (CommandState State, ParsedCommand? Command) Feed(string key)
    {
        // Awaiting find char (f/F/t/T)
        if (_awaitingFindChar)
        {
            if (key.Length == 1)
            {
                _pendingFindChar = key[0];
                _lastFindChar = key[0];
                _awaitingFindChar = false;
                _buffer += key;
                return TryParse();
            }
            Reset();
            return (CommandState.Invalid, null);
        }

        // Awaiting register name ("x)
        if (_awaitingRegister)
        {
            if (key.Length == 1)
            {
                _pendingRegister = key[0];
                _awaitingRegister = false;
                _buffer += key;
                return (CommandState.Incomplete, null);
            }
            Reset();
            return (CommandState.Invalid, null);
        }

        // Register prefix
        if (key == "\"" && string.IsNullOrEmpty(_buffer))
        {
            _awaitingRegister = true;
            _buffer += key;
            return (CommandState.Incomplete, null);
        }

        _buffer += key;
        return TryParse();
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

        // Check for operator
        string? op = rest[0] switch
        {
            'd' or 'c' or 'y' or '>' or '<' or '=' => rest[0..1],
            _ => null
        };

        // g and z are multi-key motion prefixes, not operators
        if (rest.StartsWith("gg")) return Finalize(count, null, "gg");
        if (rest == "g") return (CommandState.Incomplete, null);
        if (rest.StartsWith("g") && rest.Length >= 2)
        {
            return rest[1] switch
            {
                'e' => Finalize(count, null, "ge"),
                'j' => Finalize(count, null, "gj"),
                'k' => Finalize(count, null, "gk"),
                't' => Finalize(count, null, "gt"),
                'T' => Finalize(count, null, "gT"),
                _ => (CommandState.Invalid, null)
            };
        }
        if (rest == "z") return (CommandState.Incomplete, null);
        if (rest.StartsWith("z") && rest.Length >= 2)
        {
            return rest[1] switch
            {
                'z' => Finalize(count, null, "zz"),
                't' => Finalize(count, null, "zt"),
                'b' => Finalize(count, null, "zb"),
                _ => (CommandState.Invalid, null)
            };
        }

        if (op != null)
        {
            string afterOp = rest[1..];

            // Double-operator: dd, cc, yy, >>, <<
            if (afterOp.Length > 0 && afterOp[0].ToString() == op)
            {
                return Finalize(count, op, op, linewise: true);
            }

            // Linewise: dV, cV etc. — not standard, skip

            if (afterOp.Length == 0)
            {
                // Special: gd, gw, gc etc.
                if (op == "g" || op == "z")
                    return (CommandState.Incomplete, null);
                return (CommandState.Incomplete, null);
            }

            // g-prefix motions
            if (op == "g")
            {
                return afterOp[0] switch
                {
                    'g' => Finalize(count, null, "gg"),
                    'e' => Finalize(count, null, "ge"),
                    'j' => Finalize(count, null, "gj"),
                    'k' => Finalize(count, null, "gk"),
                    't' => Finalize(count, null, "gt"),
                    'T' => Finalize(count, null, "gT"),
                    _ => (CommandState.Invalid, null)
                };
            }

            // z-prefix
            if (op == "z")
            {
                return afterOp[0] switch
                {
                    'z' => Finalize(count, null, "zz"),
                    't' => Finalize(count, null, "zt"),
                    'b' => Finalize(count, null, "zb"),
                    _ => (CommandState.Invalid, null)
                };
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

        // Text objects for operators: iw/aw (and WORD variants)
        if (op != null && s is "i" or "a")
            return (CommandState.Incomplete, null);
        if (op != null && s.Length == 2 && (s[0] is 'i' or 'a'))
        {
            return s[1] switch
            {
                'w' or 'W' => Finalize(count, op, s),
                _ => (CommandState.Invalid, null)
            };
        }

        // Two-char motions
        if (s == "g" || s == "z") return (CommandState.Incomplete, null);

        // Find char motions
        if (s[0] is 'f' or 'F' or 't' or 'T')
        {
            if (s.Length < 2)
            {
                _awaitingFindChar = true;
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

        string motion = s switch
        {
            "h" or "j" or "k" or "l" or
            "w" or "b" or "e" or "W" or "B" or "E" or
            "0" or "^" or "$" or
            "G" or "H" or "M" or "L" or
            "{" or "}" or "%" or ";" or "," or
            "~" or "x" or "X" or "p" or "P" or
            "u" or "\x12" or  // Ctrl+R
            "." or "n" or "N" or "*" or "#" or
            "J" or "a" or "A" or "i" or "I" or
            "o" or "O" or "s" or "S" or "C" or "D" or "Y" or
            "r" or "R" or "m" or "`" or "'" or
            "q" or "@" or
            "v" or "V" or "\x16" // Ctrl+V
            => s,
            "gg" => "gg",
            "zz" or "zt" or "zb" => s,
            _ => s
        };

        // r needs next char
        if (s == "r") return (CommandState.Incomplete, null);
        if (s.Length == 2 && s[0] == 'r') return Finalize(count, op, s, findChar: s[1]);

        // m and ` and ' need next char
        if (s is "m" or "`" or "'") return (CommandState.Incomplete, null);
        if (s.Length == 2 && s[0] is 'm' or '`' or '\'') return Finalize(count, op, s);

        // @ and q need register
        if (s is "q" or "@") return (CommandState.Incomplete, null);
        if (s.Length == 2 && s[0] is 'q' or '@') return Finalize(count, op, s);

        return Finalize(count, op, motion);
    }

    private (CommandState, ParsedCommand?) Finalize(int count = 1, string? op = null, string motion = "",
        bool linewise = false, char? findChar = null)
    {
        var cmd = new ParsedCommand(count, op, motion, _pendingRegister, findChar ?? _pendingFindChar, linewise);
        Reset();
        return (CommandState.Complete, cmd);
    }

    private (CommandState, ParsedCommand?) Finalize() => Finalize(1, null, _buffer);
}
