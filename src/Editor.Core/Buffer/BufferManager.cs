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

    /// <summary>True when the backing file was detected as binary; its content is not loaded and the buffer is read-only.</summary>
    public bool IsBinary { get; private set; }

    // ─── Virtual document (not backed by a file on disk) ───
    /// <summary>True when this buffer is an in-memory document with no backing file.</summary>
    public bool IsVirtual { get; set; }
    /// <summary>Host-supplied identifier for a virtual document (null for file-backed buffers).</summary>
    public string? DocumentId { get; set; }
    /// <summary>Display title for a virtual document (shown instead of a file name).</summary>
    public string? DisplayName { get; set; }

    static VimBuffer()
    {
        // Shift-JIS / EUC-JP live in the legacy code-pages provider, which .NET does not load
        // by default. Register it once so GetEncoding("shift-jis"/"euc-jp") resolves.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    private static int _nextId = 1;
    public VimBuffer() => Id = _nextId++;
    public VimBuffer(string filePath, string? preferredEncoding = null) : this()
    {
        FilePath = filePath;
        if (File.Exists(filePath))
        {
            var bytes = File.ReadAllBytes(filePath);
            // An explicit caller-supplied encoding wins only when the file carries no BOM;
            // otherwise auto-detect (BOM, then UTF-8/Shift-JIS/EUC-JP content analysis).
            FileEncoding = DetectEncodingFromBom(bytes) == "utf-8" && !string.IsNullOrWhiteSpace(preferredEncoding)
                ? preferredEncoding
                : DetectEncoding(bytes);
            // UTF-16 text legitimately contains NUL bytes, so only run the NUL-byte scan when the
            // BOM did not already identify the file as a known (UTF-16) text encoding.
            bool isUtf16 = FileEncoding is "utf-16" or "utf-16le" or "utf-16be";
            if (!isUtf16 && IsBinaryContent(bytes))
            {
                // Binary file: create the buffer but do not decode the content into the editor.
                IsBinary = true;
                Text.SetText($"[Binary file — {bytes.Length:N0} bytes, not loaded]");
                Text.MarkSaved();
                return;
            }
            var enc = GetEncoding(FileEncoding);
            // Strip BOM bytes before decoding if present
            int bomLen = GetBomLength(bytes, enc);
            var raw = enc.GetString(bytes, bomLen, bytes.Length - bomLen);
            FileFormat = DetectFileFormat(raw);
            Text.SetText(raw);
            Text.MarkSaved();
        }
    }

    /// <summary>Stable identity used to prevent persistent undo data attaching to another document.</summary>
    public string PersistentDocumentId => FilePath != null
        ? "file:" + Path.GetFullPath(FilePath).Replace('\\', '/').ToUpperInvariant()
        : DocumentId != null ? "virtual:" + DocumentId : "buffer:" + Id;

    /// <summary>Saves this document's history to an explicit host-owned sidecar path.</summary>
    public void SaveUndoHistory(string historyPath) => Undo.SaveHistory(historyPath, Text, PersistentDocumentId);

    /// <summary>Loads history only when both this document identity and its exact current text match.</summary>
    public UndoImportResult LoadUndoHistory(string historyPath, UndoPersistenceLimits? limits = null) =>
        Undo.LoadHistory(historyPath, Text, PersistentDocumentId, limits);

    /// <summary>
    /// Heuristically detect binary content by scanning the first 8KB for a NUL byte
    /// (the same approach Git uses). Text files virtually never contain NUL bytes.
    /// </summary>
    public static bool IsBinaryContent(byte[] bytes)
    {
        int scan = Math.Min(bytes.Length, 8000);
        for (int i = 0; i < scan; i++)
            if (bytes[i] == 0) return true;
        return false;
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

    /// <summary>
    /// Detect the encoding name of a file's raw bytes. A BOM wins if present; otherwise the
    /// content is analysed so that BOM-less Japanese files (Shift-JIS / EUC-JP) are recognised
    /// instead of being silently mis-decoded as UTF-8. Falls back to "utf-8" when the bytes are
    /// pure ASCII, valid UTF-8, or no encoding is plausible.
    /// </summary>
    public static string DetectEncoding(byte[] bytes)
    {
        var bom = DetectEncodingFromBom(bytes);
        if (bom != "utf-8") return bom;

        int scan = Math.Min(bytes.Length, 65536);

        // Pure ASCII (no high bytes) is decoded identically by every candidate; treat as utf-8.
        bool hasHighByte = false;
        for (int i = 0; i < scan; i++)
            if (bytes[i] >= 0x80) { hasHighByte = true; break; }
        if (!hasHighByte) return "utf-8";

        // Well-formed UTF-8 multibyte content is decisive — SJIS/EUC byte patterns almost never
        // satisfy the UTF-8 continuation rules, so a clean pass means the file really is UTF-8.
        if (IsValidUtf8(bytes, scan)) return "utf-8";

        int sjis = ScoreShiftJis(bytes, scan);
        int euc  = ScoreEucJp(bytes, scan);
        if (sjis < 0 && euc < 0) return "utf-8"; // neither is plausible — leave the default
        if (euc < 0) return "shift-jis";
        if (sjis < 0) return "euc-jp";
        return euc > sjis ? "euc-jp" : "shift-jis";
    }

    /// <summary>True when the first <paramref name="scan"/> bytes form valid UTF-8 containing at
    /// least one multibyte sequence (a trailing sequence truncated by the scan window is ignored).</summary>
    private static bool IsValidUtf8(byte[] bytes, int scan)
    {
        int i = 0;
        bool sawMultibyte = false;
        while (i < scan)
        {
            byte b = bytes[i];
            if (b < 0x80) { i++; continue; }

            int trail;
            if ((b & 0xE0) == 0xC0) { if (b < 0xC2) return false; trail = 1; } // reject overlong
            else if ((b & 0xF0) == 0xE0) trail = 2;
            else if ((b & 0xF8) == 0xF0) { if (b > 0xF4) return false; trail = 3; }
            else return false;

            for (int k = 1; k <= trail; k++)
            {
                if (i + k >= scan) return sawMultibyte;          // truncated at window edge
                if ((bytes[i + k] & 0xC0) != 0x80) return false; // bad continuation byte
            }
            sawMultibyte = true;
            i += trail + 1;
        }
        return sawMultibyte;
    }

    /// <summary>Count plausible Shift-JIS double-byte characters; returns -1 if an invalid byte
    /// sequence disqualifies the encoding.</summary>
    private static int ScoreShiftJis(byte[] bytes, int scan)
    {
        int i = 0, count = 0;
        while (i < scan)
        {
            byte b = bytes[i];
            if (b < 0x80) { i++; continue; }
            if (b is >= 0xA1 and <= 0xDF) { i++; continue; } // half-width katakana (single byte)
            bool lead = b is >= 0x81 and <= 0x9F or >= 0xE0 and <= 0xFC;
            if (!lead) return -1;
            if (i + 1 >= scan) break;
            byte t = bytes[i + 1];
            if (t is >= 0x40 and <= 0x7E or >= 0x80 and <= 0xFC) { count++; i += 2; }
            else return -1;
        }
        return count;
    }

    /// <summary>Count plausible EUC-JP double/triple-byte characters; returns -1 if an invalid
    /// byte sequence disqualifies the encoding.</summary>
    private static int ScoreEucJp(byte[] bytes, int scan)
    {
        int i = 0, count = 0;
        while (i < scan)
        {
            byte b = bytes[i];
            if (b < 0x80) { i++; continue; }
            if (b == 0x8E) // half-width katakana: 0x8E + 0xA1..0xDF
            {
                if (i + 1 >= scan) break;
                if (bytes[i + 1] is >= 0xA1 and <= 0xDF) { count++; i += 2; } else return -1;
            }
            else if (b == 0x8F) // JIS X 0212: 0x8F + two 0xA1..0xFE bytes
            {
                if (i + 2 >= scan) break;
                if (bytes[i + 1] is >= 0xA1 and <= 0xFE && bytes[i + 2] is >= 0xA1 and <= 0xFE)
                    { count++; i += 3; } else return -1;
            }
            else if (b is >= 0xA1 and <= 0xFE)
            {
                if (i + 1 >= scan) break;
                if (bytes[i + 1] is >= 0xA1 and <= 0xFE) { count++; i += 2; } else return -1;
            }
            else return -1;
        }
        return count;
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
        "latin1" or "iso-8859-1"    => Encoding.Latin1,
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

    /// <summary>Fired synchronously just before the buffer's content is written to disk.</summary>
    public event Action<string>? BeforeWrite;

    /// <summary>Fired synchronously just after the buffer's content has been written to disk.</summary>
    public event Action<string>? AfterWrite;

    public void Save(string? path = null)
    {
        // Binary files are never decoded into the buffer, so writing the placeholder text back
        // would destroy the original file. Refuse to save.
        if (IsBinary)
            throw new InvalidOperationException("E21: Cannot write a binary file (read-only).");
        path ??= FilePath ?? throw new InvalidOperationException("No file path specified.");
        BeforeWrite?.Invoke(path);
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
        AfterWrite?.Invoke(path);
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

    /// <summary>Fired synchronously just before any buffer's content is written to disk (drives the <c>BufWritePre</c> autocmd).</summary>
    public event Action<VimBuffer, string>? BufferWillWrite;

    /// <summary>Fired synchronously just after any buffer's content has been written to disk (drives the <c>BufWritePost</c> autocmd).</summary>
    public event Action<VimBuffer, string>? BufferDidWrite;

    public BufferManager()
    {
        _buffers.Add(Track(new VimBuffer()));
    }

    public VimBuffer NewBuffer()
    {
        var buf = Track(new VimBuffer());
        _buffers.Add(buf);
        SwitchTo(_buffers.Count - 1);
        return buf;
    }

    public VimBuffer OpenFile(string path, string? preferredEncoding = null)
    {
        var existingIndex = _buffers.FindIndex(b => b.FilePath == path);
        if (existingIndex >= 0)
        {
            SwitchTo(existingIndex);
            return _buffers[existingIndex];
        }
        var buf = Track(new VimBuffer(path, preferredEncoding));
        _buffers.Add(buf);
        SwitchTo(_buffers.Count - 1);
        return buf;
    }

    private VimBuffer Track(VimBuffer buf)
    {
        buf.BeforeWrite += path => BufferWillWrite?.Invoke(buf, path);
        buf.AfterWrite += path => BufferDidWrite?.Invoke(buf, path);
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
