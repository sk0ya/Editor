using Editor.Core.Folds;

namespace Editor.Core.Buffer;

public class VimBuffer
{
    public int Id { get; }
    public string? FilePath { get; set; }
    public string Name => FilePath != null ? Path.GetFileName(FilePath) : $"[No Name]";
    public TextBuffer Text { get; } = new();
    public UndoManager Undo { get; } = new();
    public FoldManager Folds { get; } = new();

    private static int _nextId = 1;
    public VimBuffer() => Id = _nextId++;
    public VimBuffer(string filePath) : this()
    {
        FilePath = filePath;
        if (File.Exists(filePath))
        {
            Text.SetText(File.ReadAllText(filePath));
            Text.MarkSaved();
        }
    }

    public void Save(string? path = null)
    {
        path ??= FilePath ?? throw new InvalidOperationException("No file path specified.");
        FilePath = path;
        File.WriteAllText(path, Text.GetText());
        Text.MarkSaved();
    }
}

public class BufferManager
{
    private readonly List<VimBuffer> _buffers = [];
    private int _currentIndex = 0;
    private int _alternateIndex = -1;

    public IReadOnlyList<VimBuffer> Buffers => _buffers;
    public VimBuffer Current => _buffers[_currentIndex];
    public int CurrentIndex => _currentIndex;

    public BufferManager()
    {
        _buffers.Add(new VimBuffer());
    }

    public VimBuffer NewBuffer()
    {
        var buf = new VimBuffer();
        _buffers.Add(buf);
        SwitchTo(_buffers.Count - 1);
        return buf;
    }

    public VimBuffer OpenFile(string path)
    {
        var existingIndex = _buffers.FindIndex(b => b.FilePath == path);
        if (existingIndex >= 0)
        {
            SwitchTo(existingIndex);
            return _buffers[existingIndex];
        }
        var buf = new VimBuffer(path);
        _buffers.Add(buf);
        SwitchTo(_buffers.Count - 1);
        return buf;
    }

    public bool GoToNext()
    {
        if (_buffers.Count <= 1) return false;
        SwitchTo((_currentIndex + 1) % _buffers.Count);
        return true;
    }

    public bool GoToPrev()
    {
        if (_buffers.Count <= 1) return false;
        SwitchTo((_currentIndex - 1 + _buffers.Count) % _buffers.Count);
        return true;
    }

    public bool GoTo(int n)
    {
        if (n < 0 || n >= _buffers.Count) return false;
        SwitchTo(n);
        return true;
    }

    public bool GoToAlternate()
    {
        if (_alternateIndex < 0 || _alternateIndex >= _buffers.Count) return false;
        SwitchTo(_alternateIndex);
        return true;
    }

    public bool CloseBuffer(int index = -1)
    {
        if (index < 0) index = _currentIndex;
        if (_buffers.Count <= 1) return false;
        _buffers.RemoveAt(index);
        if (_alternateIndex > index) _alternateIndex--;
        else if (_alternateIndex >= _buffers.Count) _alternateIndex = _buffers.Count - 1;
        if (_currentIndex > index) _currentIndex--;
        else _currentIndex = Math.Clamp(_currentIndex, 0, _buffers.Count - 1);
        return true;
    }

    private void SwitchTo(int index)
    {
        if (index != _currentIndex)
            _alternateIndex = _currentIndex;
        _currentIndex = index;
    }
}
