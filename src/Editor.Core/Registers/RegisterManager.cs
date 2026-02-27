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

    public RegisterManager(IClipboardProvider? clipboard = null)
    {
        _clipboard = clipboard;
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
    }

    public Register Get(char name)
    {
        if (name == '"' || name == '\0') return _unnamed;

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
}
