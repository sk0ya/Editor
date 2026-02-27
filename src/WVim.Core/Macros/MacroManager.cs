namespace WVim.Core.Macros;

public record struct VimKeyStroke(string Key, bool Ctrl, bool Shift, bool Alt)
{
    public override readonly string ToString()
    {
        var prefix = (Ctrl ? "C-" : "") + (Alt ? "A-" : "");
        return $"{prefix}{Key}";
    }
}

public class MacroManager
{
    private readonly Dictionary<char, List<VimKeyStroke>> _macros = [];
    private char _recordingRegister = '\0';
    private List<VimKeyStroke>? _recording;
    private char _lastPlayedRegister = '\0';

    public bool IsRecording => _recording != null;
    public char RecordingRegister => _recordingRegister;

    public void StartRecording(char register)
    {
        _recordingRegister = char.ToLower(register);
        _recording = [];
    }

    public void StopRecording()
    {
        if (_recording != null)
        {
            _macros[_recordingRegister] = _recording;
            _lastPlayedRegister = _recordingRegister;
        }
        _recording = null;
        _recordingRegister = '\0';
    }

    public void RecordKey(VimKeyStroke key)
    {
        _recording?.Add(key);
    }

    public List<VimKeyStroke>? GetMacro(char register)
    {
        var key = register == '@' ? _lastPlayedRegister : char.ToLower(register);
        return _macros.TryGetValue(key, out var macro) ? macro : null;
    }

    public void SetLastPlayed(char register) => _lastPlayedRegister = char.ToLower(register);
}
