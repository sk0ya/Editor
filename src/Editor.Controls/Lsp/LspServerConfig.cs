namespace Editor.Controls.Lsp;

/// <summary>Maps file extensions to language server executable and arguments.</summary>
public record LspServerDef(string Executable, string[] Args, string LanguageId);

public static class LspServerConfig
{
    private static readonly Dictionary<string, LspServerDef> _byExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        { ".cs",   new LspServerDef("csharp-ls",                      [],             "csharp") },
        { ".py",   new LspServerDef("pylsp",                          [],             "python") },
        { ".ts",   new LspServerDef("typescript-language-server",     ["--stdio"],    "typescript") },
        { ".tsx",  new LspServerDef("typescript-language-server",     ["--stdio"],    "typescriptreact") },
        { ".js",   new LspServerDef("typescript-language-server",     ["--stdio"],    "javascript") },
        { ".jsx",  new LspServerDef("typescript-language-server",     ["--stdio"],    "javascriptreact") },
        { ".rs",   new LspServerDef("rust-analyzer",                  [],             "rust") },
        { ".go",   new LspServerDef("gopls",                          [],             "go") },
        { ".lua",  new LspServerDef("lua-language-server",            [],             "lua") },
        { ".cpp",  new LspServerDef("clangd",                         [],             "cpp") },
        { ".c",    new LspServerDef("clangd",                         [],             "c") },
        { ".h",    new LspServerDef("clangd",                         [],             "c") },
        { ".hpp",  new LspServerDef("clangd",                         [],             "cpp") },
        { ".rb",   new LspServerDef("solargraph",                     ["stdio"],      "ruby") },
    };

    public static LspServerDef? GetForExtension(string extension) =>
        _byExtension.TryGetValue(extension, out var def) ? def : null;
}
