using Editor.Core.Macros;

namespace Editor.Core.Engine;

/// <summary>
/// Pure key-mapping resolution helpers: parsing a map LHS/RHS (<c>nnoremap</c> etc.)
/// into <see cref="VimKeyStroke"/> sequences and matching accumulated input against
/// the configured maps. Stateless — <see cref="VimEngine"/> owns the pending-input
/// buffer and drives these from <c>TryApplyMapping</c>.
/// </summary>
public static class KeyMappingResolver
{
    public readonly record struct MapMatch(
        bool HasExactMatch,
        bool HasPrefix,
        bool HasLongerPrefix,
        string? MappedValue);

    public static MapMatch ResolveMapMatch(Dictionary<string, string> maps, IReadOnlyList<VimKeyStroke> input)
    {
        bool hasPrefix = false;
        bool hasLongerPrefix = false;
        string? mappedValue = null;
        int exactLength = -1;

        foreach (var kv in maps)
        {
            var lhs = ParseMappingSequence(kv.Key);
            if (lhs.Count == 0 || !StartsWith(lhs, input))
                continue;

            if (lhs.Count == input.Count)
            {
                if (lhs.Count > exactLength)
                {
                    exactLength = lhs.Count;
                    mappedValue = kv.Value;
                }
            }
            else
            {
                hasPrefix = true;
                if (exactLength >= 0)
                    hasLongerPrefix = true;
            }
        }

        return new MapMatch(
            HasExactMatch: exactLength >= 0,
            HasPrefix: hasPrefix,
            HasLongerPrefix: hasLongerPrefix,
            MappedValue: mappedValue);
    }

    private static bool StartsWith(IReadOnlyList<VimKeyStroke> candidate, IReadOnlyList<VimKeyStroke> input)
    {
        if (input.Count > candidate.Count)
            return false;

        for (int i = 0; i < input.Count; i++)
        {
            if (!AreSameStroke(candidate[i], input[i]))
                return false;
        }

        return true;
    }

    private static bool AreSameStroke(VimKeyStroke left, VimKeyStroke right) =>
        left.Ctrl == right.Ctrl &&
        left.Alt == right.Alt &&
        string.Equals(left.Key, right.Key, StringComparison.Ordinal) &&
        // For a single printable character the Shift state is already encoded in
        // the character itself (e.g. 'H' == Shift+h), so the Shift flag is
        // redundant. A map LHS is parsed with Shift=false (see ParseMappingSequence),
        // but key delivery routes disagree on the flag — the IME / OnKeyDown path
        // reports Shift=true for 'H' while the OnTextInput path reports false. Only
        // enforce Shift equality for named keys (Tab, Space, F-keys, …) where
        // <S-…> is a genuinely distinct chord; otherwise an uppercase-letter map
        // like `nnoremap H ^` silently fails whenever Shift is reported.
        (left.Key.Length == 1 || left.Shift == right.Shift);

    public static IReadOnlyList<VimKeyStroke> ParseMappingSequence(string sequence)
    {
        var strokes = new List<VimKeyStroke>();
        for (int i = 0; i < sequence.Length; i++)
        {
            if (sequence[i] == '<')
            {
                int end = sequence.IndexOf('>', i + 1);
                if (end > i)
                {
                    var token = sequence[i..(end + 1)];
                    if (TryParseMapToken(token, out var parsed))
                    {
                        strokes.Add(parsed);
                        i = end;
                        continue;
                    }
                }
            }

            strokes.Add(new VimKeyStroke(sequence[i].ToString(), false, false, false));
        }

        return strokes;
    }

    private static bool TryParseMapToken(string token, out VimKeyStroke stroke)
    {
        stroke = default;
        if (token.Length < 3 || token[0] != '<' || token[^1] != '>')
            return false;

        var inner = token[1..^1];
        if (string.IsNullOrWhiteSpace(inner))
            return false;

        var parts = inner.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool ctrl = false, shift = false, alt = false;
        string keyPart;

        if (parts.Length == 1)
        {
            keyPart = parts[0];
        }
        else
        {
            for (int i = 0; i < parts.Length - 1; i++)
            {
                switch (parts[i].ToLowerInvariant())
                {
                    case "c":
                    case "ctrl":
                    case "control":
                        ctrl = true;
                        break;
                    case "s":
                    case "shift":
                        shift = true;
                        break;
                    case "a":
                    case "alt":
                    case "m":
                    case "meta":
                        alt = true;
                        break;
                    default:
                        return false;
                }
            }

            keyPart = parts[^1];
        }

        var key = NormalizeMapKeyName(keyPart);
        if (key == null)
            return false;

        stroke = new VimKeyStroke(key, ctrl, shift, alt);
        return true;
    }

    private static string? NormalizeMapKeyName(string keyName)
    {
        if (string.IsNullOrWhiteSpace(keyName))
            return null;

        var lowered = keyName.ToLowerInvariant();
        return lowered switch
        {
            "esc" or "escape" => "Escape",
            "cr" or "enter" or "return" => "Return",
            "tab" => "Tab",
            "bs" or "backspace" or "back" => "Back",
            "del" or "delete" => "Delete",
            "space" => " ",
            "left" => "Left",
            "right" => "Right",
            "up" => "Up",
            "down" => "Down",
            "home" => "Home",
            "end" => "End",
            "lt" => "<",
            "bar" => "|",
            _ when keyName.Length == 1 => keyName,
            _ => null
        };
    }
}
