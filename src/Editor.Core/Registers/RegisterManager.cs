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
