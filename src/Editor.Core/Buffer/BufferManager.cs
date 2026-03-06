using System.Text;
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

    /// <summary>Line-ending format: "unix" (\n), "dos" (\r\n), or "mac" (\r).</summary>
    public string FileFormat { get; set; } = "unix";

    /// <summary>File encoding name (vim-style, e.g. "utf-8", "utf-16", "latin1").</summary>
    public string FileEncoding { get; set; } = "utf-8";

    private static int _nextId = 1;
    public VimBuffer() => Id = _nextId++;
    public VimBuffer(string filePath) : this()
    {
        FilePath = filePath;
        if (File.Exists(filePath))
        {
            var bytes = File.ReadAllBytes(filePath);
            FileEncoding = DetectEncodingFromBom(bytes);
            var enc = GetEncoding(FileEncoding);
            // Strip BOM bytes before decoding if present
            int bomLen = GetBomLength(bytes, enc);
            var raw = enc.GetString(bytes, bomLen, bytes.Length - bomLen);
            FileFormat = DetectFileFormat(raw);
            Text.SetText(raw);
            Text.MarkSaved();
        }
    }

    /// <summary>Detect encoding name from BOM bytes; returns "utf-8" if no BOM is found.</summary>
    public static string DetectEncodingFromBom(byte[] bytes)
    {
        if (bytes.Length >= 2)
        {
            if (bytes[0] == 0xFF && bytes[1] == 0xFE) return "utf-16le";
            if (bytes[0] == 0xFE && bytes[1] == 0xFF) return "utf-16be";
        }
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return "utf-8-bom";
        return "utf-8";
    }

    public static int GetBomLength(byte[] bytes, Encoding enc)
    {
        var preamble = enc.GetPreamble();
        if (preamble.Length == 0 || bytes.Length < preamble.Length) return 0;
        for (int i = 0; i < preamble.Length; i++)
            if (bytes[i] != preamble[i]) return 0;
        return preamble.Length;
    }

    /// <summary>Map a vim-style encoding name to a .NET <see cref="Encoding"/>.</summary>
    public static Encoding GetEncoding(string name) => name.ToLowerInvariant() switch
    {
        "utf-8-bom" or "utf-8bom"   => new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
        "utf-16" or "utf-16le"      => new UnicodeEncoding(bigEndian: false, byteOrderMark: true),
        "utf-16be"                  => new UnicodeEncoding(bigEndian: true,  byteOrderMark: true),
        "latin1" or "iso-8859-1"    => Encoding.GetEncoding(1252),
        "ascii"                     => Encoding.ASCII,
        "shift-jis" or "sjis"       => Encoding.GetEncoding("shift-jis"),
        "euc-jp"                    => Encoding.GetEncoding("euc-jp"),
        _                           => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
    };

    /// <summary>Detect line-ending style from raw file content in a single pass.</summary>
    public static string DetectFileFormat(string raw)
    {
        for (int i = 0; i < raw.Length; i++)
        {
            if (raw[i] == '\r')
                return i + 1 < raw.Length && raw[i + 1] == '\n' ? "dos" : "mac";
            if (raw[i] == '\n')
                return "unix";
        }
        return "unix";
    }

    public void Save(string? path = null)
    {
        path ??= FilePath ?? throw new InvalidOperationException("No file path specified.");
        FilePath = path;
        // GetText() joins lines with \n; replace with the desired line ending.
        var text = Text.GetText();
        var content = FileFormat switch
        {
            "dos" => text.Replace("\n", "\r\n"),
            "mac" => text.Replace("\n", "\r"),
            _     => text,
        };
        File.WriteAllText(path, content, GetEncoding(FileEncoding));
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
