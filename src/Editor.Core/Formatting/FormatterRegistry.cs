using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Editor.Core.Formatting;

/// <summary>
/// A CLI formatter invocation for one file extension. <see cref="Executable"/> is run with
/// <see cref="Args"/>, the buffer text is written to its stdin, and the formatted document is read
/// back from stdout. Any <c>{file}</c> token inside <see cref="Args"/> is replaced with the current
/// file path at run time (so e.g. <c>prettier --stdin-filepath {file}</c> can pick a parser by name).
/// </summary>
public record FormatterDef(string Executable, string[] Args);

/// <summary>One row in the effective extension→formatter table.</summary>
public record FormatterEntry(string Extension, FormatterDef Def);

/// <summary>
/// Extension→CLI-formatter registry. Unlike <c>LspServerRegistry</c> it ships **no built-in active
/// mappings** — a formatter is used only once the user (or the host) configures one, persisted as JSON
/// so it survives restarts. The editor owns this configuration; the <c>:Fmt*</c> ex commands and
/// <see cref="HandleFormatDocumentAsync"/>'s "investigate known candidates" path write to it. Known
/// *candidates* (for suggestions) live separately in <see cref="KnownFormatters"/>, never auto-applied.
/// </summary>
public sealed class FormatterRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, FormatterDef> _overrides = new(StringComparer.OrdinalIgnoreCase);
    private readonly string? _storePath;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Creates a registry. When <paramref name="storePath"/> is given, changes are loaded from and saved
    /// to that JSON file; when null the registry is in-memory only (used by tests).
    /// </summary>
    public FormatterRegistry(string? storePath = null)
    {
        _storePath = storePath;
        Load();
    }

    private static readonly object _defaultGate = new();
    private static FormatterRegistry? _default;

    /// <summary>
    /// The process-wide registry shared by the running editor and the <c>:Fmt*</c> ex commands.
    /// First access (lazily) creates one persisting to <see cref="DefaultStorePath"/>; a host can override
    /// that location by calling <see cref="ConfigureDefault"/> at startup (before any editor control is built).
    /// </summary>
    public static FormatterRegistry Default
    {
        get { lock (_defaultGate) return _default ??= new FormatterRegistry(DefaultStorePath()); }
    }

    /// <summary>
    /// Replace the process-wide <see cref="Default"/> registry so it persists to <paramref name="storePath"/>
    /// (pass null for an in-memory, non-persisting Default). Lets a host keep formatter config inside its own
    /// data folder instead of <c>%APPDATA%/sk0ya.Editor</c>. Call once at startup, before opening any editor control.
    /// </summary>
    public static void ConfigureDefault(string? storePath)
    {
        lock (_defaultGate) _default = new FormatterRegistry(storePath);
    }

    /// <summary>The default persisted config path when no host override is set: <c>%APPDATA%/sk0ya.Editor/formatters.json</c>.</summary>
    public static string DefaultStorePath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "sk0ya.Editor", "formatters.json");

    /// <summary>Resolve the formatter for a file extension (e.g. ".md"), or null when none is configured.</summary>
    public FormatterDef? GetForExtension(string extension)
    {
        var ext = NormalizeExt(extension);
        if (ext.Length == 0) return null;
        lock (_gate) return _overrides.TryGetValue(ext, out var def) ? def : null;
    }

    /// <summary>The effective table, sorted by extension.</summary>
    public IReadOnlyList<FormatterEntry> List()
    {
        lock (_gate)
            return _overrides
                .Select(kv => new FormatterEntry(kv.Key, kv.Value))
                .OrderBy(e => e.Extension, StringComparer.OrdinalIgnoreCase)
                .ToList();
    }

    /// <summary>Add or replace the formatter for an extension.</summary>
    public void Set(string extension, FormatterDef def)
    {
        var ext = NormalizeExt(extension);
        if (ext.Length == 0) throw new ArgumentException("Extension must not be empty.", nameof(extension));
        lock (_gate)
        {
            _overrides[ext] = def;
            Save();
        }
    }

    /// <summary>Remove the formatter for an extension. Returns true when something was actually removed.</summary>
    public bool Remove(string extension)
    {
        var ext = NormalizeExt(extension);
        if (ext.Length == 0) return false;
        lock (_gate)
        {
            if (!_overrides.Remove(ext)) return false;
            Save();
            return true;
        }
    }

    /// <summary>Alias for <see cref="Remove"/> — there is no built-in default to fall back to.</summary>
    public bool Reset(string extension) => Remove(extension);

    /// <summary>Normalize a user-supplied extension to a leading-dot, lower-invariant form (".MD" → ".md", "md" → ".md").</summary>
    public static string NormalizeExt(string extension)
    {
        var ext = extension?.Trim() ?? "";
        if (ext.Length == 0) return "";
        if (ext[0] != '.') ext = "." + ext;
        return ext.ToLowerInvariant();
    }

    // ── Persistence ─────────────────────────────────────────────────────────

    private sealed class StoreDto
    {
        public Dictionary<string, FormatterDef> Formatters { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private void Load()
    {
        if (_storePath is null || !File.Exists(_storePath)) return;
        try
        {
            var dto = JsonSerializer.Deserialize<StoreDto>(File.ReadAllText(_storePath), JsonOptions);
            if (dto is null) return;
            lock (_gate)
            {
                _overrides.Clear();
                foreach (var (ext, def) in dto.Formatters)
                {
                    var key = NormalizeExt(ext);
                    if (key.Length > 0 && def is not null && !string.IsNullOrWhiteSpace(def.Executable))
                        _overrides[key] = new FormatterDef(def.Executable, def.Args ?? []);
                }
            }
        }
        catch
        {
            // Corrupt/unreadable config must never break the editor — fall back to "no formatters".
        }
    }

    private void Save()
    {
        if (_storePath is null) return;   // in-memory registry (tests)
        try
        {
            var dir = Path.GetDirectoryName(_storePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var dto = new StoreDto
            {
                Formatters = new Dictionary<string, FormatterDef>(_overrides, StringComparer.OrdinalIgnoreCase),
            };
            File.WriteAllText(_storePath, JsonSerializer.Serialize(dto, JsonOptions));
        }
        catch
        {
            // Best effort: a failed save (locked file, no disk) must not abort the ex command.
        }
    }
}
