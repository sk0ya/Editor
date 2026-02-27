namespace WVim.Core.Buffer;

public class VimBuffer
{
    public int Id { get; }
    public string? FilePath { get; set; }
    public string Name => FilePath != null ? Path.GetFileName(FilePath) : $"[No Name]";
    public TextBuffer Text { get; } = new();
    public UndoManager Undo { get; } = new();

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
        _currentIndex = _buffers.Count - 1;
        return buf;
    }

    public VimBuffer OpenFile(string path)
    {
        // Check if already open
        var existing = _buffers.FirstOrDefault(b => b.FilePath == path);
        if (existing != null)
        {
            _currentIndex = _buffers.IndexOf(existing);
            return existing;
        }
        var buf = new VimBuffer(path);
        _buffers.Add(buf);
        _currentIndex = _buffers.Count - 1;
        return buf;
    }

    public bool GoToNext()
    {
        if (_buffers.Count <= 1) return false;
        _currentIndex = (_currentIndex + 1) % _buffers.Count;
        return true;
    }

    public bool GoToPrev()
    {
        if (_buffers.Count <= 1) return false;
        _currentIndex = (_currentIndex - 1 + _buffers.Count) % _buffers.Count;
        return true;
    }

    public bool GoTo(int n)
    {
        if (n < 0 || n >= _buffers.Count) return false;
        _currentIndex = n;
        return true;
    }

    public bool CloseBuffer(int index = -1)
    {
        if (index < 0) index = _currentIndex;
        if (_buffers.Count <= 1) return false;
        _buffers.RemoveAt(index);
        _currentIndex = Math.Clamp(_currentIndex, 0, _buffers.Count - 1);
        return true;
    }
}
