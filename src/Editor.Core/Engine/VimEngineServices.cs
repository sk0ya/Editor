using Editor.Core.Editing;
using Editor.Core.Extensibility;
using Editor.Core.Formatting;
using Editor.Core.Lsp;
using Editor.Core.Registers;
using Editor.Core.Syntax;

namespace Editor.Core.Engine;

/// <summary>
/// Explicit ownership boundary for the mutable registries and host services used
/// by a <see cref="VimEngine"/>. Use <see cref="CreateIsolated"/> per engine or
/// share one instance intentionally across an application.
/// </summary>
public sealed class VimEngineServices
{
    public required SyntaxLanguageRegistry SyntaxLanguages { get; init; }
    public required EditAssistRegistry EditAssists { get; init; }
    public required VimKeyBindingRegistry KeyBindings { get; init; }
    public required NormalCommandRegistry NormalCommands { get; init; }
    public required EditorCommandRegistry EditorCommands { get; init; }
    public required CommandGrammar CommandGrammar { get; init; }
    public required LspServerRegistry LspServers { get; init; }
    public required FormatterRegistry Formatters { get; init; }
    public IServiceProvider? CommandServices { get; init; }
    public Func<IClipboardProvider?>? ClipboardProviderFactory { get; init; }

    public static VimEngineServices CreateIsolated() => new()
    {
        SyntaxLanguages = SyntaxLanguageRegistry.CreateDefault(),
        EditAssists = new EditAssistRegistry(),
        KeyBindings = new VimKeyBindingRegistry(),
        NormalCommands = new NormalCommandRegistry(),
        EditorCommands = new EditorCommandRegistry(),
        CommandGrammar = new CommandGrammar(),
        LspServers = new LspServerRegistry(),
        Formatters = new FormatterRegistry(),
    };

    public static VimEngineServices CreateApplication(
        string? lspStorePath = null,
        string? formatterStorePath = null) => new()
    {
        SyntaxLanguages = SyntaxLanguageRegistry.CreateDefault(),
        EditAssists = new EditAssistRegistry(),
        KeyBindings = new VimKeyBindingRegistry(),
        NormalCommands = new NormalCommandRegistry(),
        EditorCommands = new EditorCommandRegistry(),
        CommandGrammar = new CommandGrammar(),
        LspServers = new LspServerRegistry(
            lspStorePath ?? LspServerRegistry.DefaultStorePath()),
        Formatters = new FormatterRegistry(
            formatterStorePath ?? FormatterRegistry.DefaultStorePath()),
    };
}
