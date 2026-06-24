using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Editor.Core.Lsp;

/// <summary>Maps a file extension to a language server executable and its arguments.</summary>
public record LspServerDef(string Executable, string[] Args, string LanguageId);

/// <summary>Where an effective mapping came from: a built-in default, a user override, or a hidden built-in.</summary>
public enum LspServerOrigin { BuiltIn, Custom, Removed }

/// <summary>One row in the effective extension→server table, with its origin for display.</summary>
public record LspServerEntry(string Extension, LspServerDef Server, LspServerOrigin Origin);

/// <summary>
/// Extension→language-server registry. Holds the built-in defaults plus user changes
/// (additions, replacements, and removals of built-ins), persisted as JSON so the configured
/// set of servers survives restarts. The editor owns this configuration — hosts only enable LSP
/// (by supplying an <c>LspManagerFactory</c>); users add/remove servers via the <c>:Lsp*</c> ex commands.
/// </summary>
public sealed class LspServerRegistry
{
    /// <summary>The original hardcoded table — the floor the user can override or hide but never lose.</summary>
    private static readonly IReadOnlyDictionary<string, LspServerDef> Builtins =
        new Dictionary<string, LspServerDef>(StringComparer.OrdinalIgnoreCase)
        {
            { ".cs",       new LspServerDef("csharp-ls",                  [],          "csharp") },
            { ".py",       new LspServerDef("pylsp",                      [],          "python") },
            { ".ts",       new LspServerDef("typescript-language-server", ["--stdio"], "typescript") },
            { ".tsx",      new LspServerDef("typescript-language-server", ["--stdio"], "typescriptreact") },
            { ".js",       new LspServerDef("typescript-language-server", ["--stdio"], "javascript") },
            { ".jsx",      new LspServerDef("typescript-language-server", ["--stdio"], "javascriptreact") },
            { ".rs",       new LspServerDef("rust-analyzer",              [],          "rust") },
            { ".go",       new LspServerDef("gopls",                      [],          "go") },
            { ".lua",      new LspServerDef("lua-language-server",        [],          "lua") },
            { ".cpp",      new LspServerDef("clangd",                     [],          "cpp") },
            { ".c",        new LspServerDef("clangd",                     [],          "c") },
            { ".h",        new LspServerDef("clangd",                     [],          "c") },
            { ".hpp",      new LspServerDef("clangd",                     [],          "cpp") },
            { ".rb",       new LspServerDef("solargraph",                 ["stdio"],   "ruby") },
            { ".md",       new LspServerDef("marksman",                   ["server"],  "markdown") },
            { ".markdown", new LspServerDef("marksman",                   ["server"],  "markdown") },
        };

    private readonly object _gate = new();
    private readonly Dictionary<string, LspServerDef> _overrides = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _removed = new(StringComparer.OrdinalIgnoreCase);
    private readonly string? _storePath;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Creates a registry. When <paramref name="storePath"/> is given, user changes are loaded from and
    /// saved to that JSON file; when null the registry is in-memory only (used by tests).
    /// </summary>
    public LspServerRegistry(string? storePath = null)
    {
        _storePath = storePath;
        Load();
    }

    private static readonly Lazy<LspServerRegistry> _default =
        new(() => new LspServerRegistry(DefaultStorePath()));

    /// <summary>The process-wide registry shared by the running editor and the <c>:Lsp*</c> ex commands.</summary>
    public static LspServerRegistry Default => _default.Value;

    /// <summary>The persisted config path: <c>%APPDATA%/sk0ya.Editor/lsp-servers.json</c>.</summary>
    public static string DefaultStorePath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "sk0ya.Editor", "lsp-servers.json");

    /// <summary>Resolve the language server for a file extension (e.g. ".cs"), or null when none is configured.</summary>
    public LspServerDef? GetForExtension(string extension)
    {
        var ext = NormalizeExt(extension);
        if (ext.Length == 0) return null;
        lock (_gate)
        {
            if (_overrides.TryGetValue(ext, out var def)) return def;   // user override wins
            if (_removed.Contains(ext)) return null;                    // built-in hidden by the user
            return Builtins.TryGetValue(ext, out var b) ? b : null;     // built-in default
        }
    }

    /// <summary>The effective table (built-ins merged with user changes, including hidden built-ins), sorted by extension.</summary>
    public IReadOnlyList<LspServerEntry> List()
    {
        lock (_gate)
        {
            var rows = new Dictionary<string, LspServerEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var (ext, def) in Builtins)
            {
                if (_removed.Contains(ext))
                    rows[ext] = new LspServerEntry(ext, def, LspServerOrigin.Removed);
                else
                    rows[ext] = new LspServerEntry(ext, def, LspServerOrigin.BuiltIn);
            }
            foreach (var (ext, def) in _overrides)
                rows[ext] = new LspServerEntry(ext, def, LspServerOrigin.Custom);

            return rows.Values
                .OrderBy(e => e.Extension, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    /// <summary>Add a new server for an extension, or replace whatever is currently mapped (built-in or custom).</summary>
    public void Set(string extension, LspServerDef def)
    {
        var ext = NormalizeExt(extension);
        if (ext.Length == 0) throw new ArgumentException("Extension must not be empty.", nameof(extension));
        lock (_gate)
        {
            _overrides[ext] = def;
            _removed.Remove(ext);
            Save();
        }
    }

    /// <summary>
    /// Remove the server for an extension. A custom override is dropped; a built-in is hidden (recorded so it
    /// stays hidden after restart). Returns true when something was actually removed/hidden.
    /// </summary>
    public bool Remove(string extension)
    {
        var ext = NormalizeExt(extension);
        if (ext.Length == 0) return false;
        lock (_gate)
        {
            bool changed = false;
            if (_overrides.Remove(ext)) changed = true;
            if (Builtins.ContainsKey(ext) && _removed.Add(ext)) changed = true;
            if (changed) Save();
            return changed;
        }
    }

    /// <summary>Drop any user change for an extension, restoring the built-in default (if any). Returns true when something changed.</summary>
    public bool Reset(string extension)
    {
        var ext = NormalizeExt(extension);
        if (ext.Length == 0) return false;
        lock (_gate)
        {
            bool changed = _overrides.Remove(ext) | _removed.Remove(ext);
            if (changed) Save();
            return changed;
        }
    }

    /// <summary>Normalize a user-supplied extension to a leading-dot, lower-invariant form (".CS" → ".cs", "cs" → ".cs").</summary>
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
        public Dictionary<string, LspServerDef> Overrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Removed { get; set; } = [];
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
                _removed.Clear();
                foreach (var (ext, def) in dto.Overrides)
                {
                    var key = NormalizeExt(ext);
                    if (key.Length > 0 && def is not null && !string.IsNullOrWhiteSpace(def.Executable))
                        _overrides[key] = new LspServerDef(def.Executable, def.Args ?? [], def.LanguageId ?? "");
                }
                foreach (var ext in dto.Removed)
                {
                    var key = NormalizeExt(ext);
                    if (key.Length > 0) _removed.Add(key);
                }
            }
        }
        catch
        {
            // Corrupt/unreadable config must never break the editor — fall back to built-ins only.
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
                Overrides = new Dictionary<string, LspServerDef>(_overrides, StringComparer.OrdinalIgnoreCase),
                Removed = _removed.OrderBy(e => e, StringComparer.OrdinalIgnoreCase).ToList(),
            };
            File.WriteAllText(_storePath, JsonSerializer.Serialize(dto, JsonOptions));
        }
        catch
        {
            // Best effort: a failed save (locked file, no disk) must not abort the ex command.
        }
    }
}
