using Editor.Core.Config;

namespace Editor.Core.Registers;

public interface IClipboardProvider
{
    string GetText();
    void SetText(string text);
}

public class RegisterManager
{
    private readonly Dictionary<char, Register> _registers = [];
    private Register _unnamed = Register.Empty;
    private IClipboardProvider? _clipboard;
    private VimOptions? _options;

    public RegisterManager(VimOptions? options = null)
    {
        _options = options;
    }

    public void SetClipboardProvider(IClipboardProvider provider) => _clipboard = provider;

    public void Set(char name, Register register)
    {
        if (name == '"' || name == '\0')
        {
            _unnamed = register;
            _registers['0'] = register;
            return;
        }

        if (name >= 'A' && name <= 'Z')
        {
            var lower = char.ToLower(name);
            if (_registers.TryGetValue(lower, out var existing))
            {
                var sep = existing.Type == RegisterType.Line ? "\n" : "";
                _registers[lower] = new Register(existing.Text + sep + register.Text, register.Type);
            }
            else
            {
                _registers[lower] = register;
            }
            _unnamed = _registers[lower];
            return;
        }

        if (name == '+' || name == '*')
        {
            try { _clipboard?.SetText(register.Text); } catch { }
            _unnamed = register;
            return;
        }

        if (name == '_') return; // blackhole

        if (name == '.' || name == ':' || name == '%') return; // read-only registers; see SetLastInserted/SetLastCommand/SetCurrentFileName

        _registers[name] = register;
        _unnamed = register;
    }

    public void SetYank(char name, Register register)
    {
        _registers['0'] = register;
        Set(name == '\0' ? '"' : name, register);

        // If clipboard=unnamed/unnamedplus, mirror unnamed yanks to the system clipboard.
        if (name == '\0' || name == '"')
        {
            var cb = _options?.Clipboard ?? "";
            if (cb.Contains("unnamed", StringComparison.OrdinalIgnoreCase))
                try { _clipboard?.SetText(register.Text); } catch { }
        }
    }

    /// <summary>
    /// Records a delete/change operation's text. An explicit register name behaves exactly like
    /// <see cref="Set"/>; otherwise (unnamed delete) the text always updates the unnamed register
    /// and additionally lands in the numbered-register ring, mirroring Vim: linewise, blockwise, or
    /// multi-line deletes shift into "1 (pushing "1-"8 down into "2-"9, dropping the oldest), while
    /// single-line characterwise deletes go to the small-delete register "- instead (Vim never treats
    /// a blockwise delete as "small", even a single-line/single-column one), leaving the ring alone.
    /// </summary>
    public void SetDelete(char name, Register register)
    {
        if (name != '"' && name != '\0')
        {
            Set(name, register);
            return;
        }

        bool isSmallDelete = register.Type == RegisterType.Character && !register.Text.Contains('\n');
        if (isSmallDelete)
        {
            _registers['-'] = register;
        }
        else
        {
            for (char c = '9'; c > '1'; c--)
            {
                if (_registers.TryGetValue((char)(c - 1), out var prev))
                    _registers[c] = prev;
                else
                    _registers.Remove(c);
            }
            _registers['1'] = register;
        }

        _unnamed = register;

        var cb = _options?.Clipboard ?? "";
        if (cb.Contains("unnamed", StringComparison.OrdinalIgnoreCase))
            try { _clipboard?.SetText(register.Text); } catch { }
    }

    /// <summary>
    /// Stores the literal text typed during the most recently finished Insert-mode session into
    /// the read-only "." register. Bypasses the guard in <see cref="Set"/>; only <see cref="Engine.VimEngine"/>
    /// should call this — it is never reachable from a user-supplied register name.
    /// </summary>
    public void SetLastInserted(string text) =>
        _registers['.'] = string.IsNullOrEmpty(text) ? Register.Empty : new Register(text, RegisterType.Character);

    /// <summary>
    /// Stores the most recently executed Ex command line (without the leading ':') into the
    /// read-only ":" register. Bypasses the guard in <see cref="Set"/>.
    /// </summary>
    public void SetLastCommand(string cmd) =>
        _registers[':'] = string.IsNullOrEmpty(cmd) ? Register.Empty : new Register(cmd, RegisterType.Character);

    /// <summary>
    /// Stores the current buffer's file path into the read-only "%" register. Bypasses the guard
    /// in <see cref="Set"/>. A null/empty path (e.g. an unnamed buffer) stores an empty register.
    /// </summary>
    public void SetCurrentFileName(string? path) =>
        _registers['%'] = string.IsNullOrEmpty(path) ? Register.Empty : new Register(path, RegisterType.Character);

    public Register Get(char name)
    {
        if (name == '"' || name == '\0')
        {
            var cb = _options?.Clipboard ?? "";
            if (cb.Contains("unnamed", StringComparison.OrdinalIgnoreCase) && _clipboard != null)
            {
                try
                {
                    var text = _clipboard.GetText();
                    if (text == _unnamed.Text)
                        return _unnamed;
                    if (!string.IsNullOrEmpty(text))
                        return new Register(text, RegisterType.Character);
                }
                catch { }
            }
            return _unnamed;
        }

        if (name == '+' || name == '*')
        {
            try
            {
                var text = _clipboard?.GetText() ?? "";
                return new Register(text, RegisterType.Character);
            }
            catch { return Register.Empty; }
        }

        if (name == '_') return Register.Empty;

        var key = char.ToLower(name);
        return _registers.TryGetValue(key, out var reg) ? reg : Register.Empty;
    }

    public Register GetUnnamed() => _unnamed;

    /// <summary>
    /// Returns all non-empty registers as (name, register) pairs for display by :registers.
    /// Includes the unnamed register (""), named registers (a-z), and yank register (0).
    /// The clipboard register (+) is included when accessible (requires STA thread); silently
    /// skipped if the provider is unavailable or the call fails.
    /// </summary>
    public IReadOnlyList<(char Name, Register Value)> GetAll()
    {
        var result = new List<(char, Register)>();

        // Unnamed register
        if (!string.IsNullOrEmpty(_unnamed.Text))
            result.Add(('"', _unnamed));

        // Yank and named registers (0-9, a-z); '"' is never a key in _registers
        foreach (var key in _registers.Keys.OrderBy(k => k))
        {
            var reg = _registers[key];
            if (!string.IsNullOrEmpty(reg.Text))
                result.Add((key, reg));
        }

        // Clipboard register — may fail on non-STA threads; swallow silently
        try
        {
            var clipText = _clipboard?.GetText() ?? "";
            if (!string.IsNullOrEmpty(clipText))
                result.Add(('+', new Register(clipText, RegisterType.Character)));
        }
        catch { }

        return result;
    }
}
