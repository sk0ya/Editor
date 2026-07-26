using Editor.Controls.Git;
using Editor.Core.Config;
using Editor.Core.Lsp;
using Editor.Core.Editing;
using Editor.Core.Registers;
using Editor.Core.Extensibility;
using Editor.Core.Syntax;
using Editor.Core.Engine;

namespace Editor.Controls;

public sealed class VimEditorControlOptions
{
    public Func<VimConfig>? ConfigFactory { get; init; }
    public Func<IClipboardProvider>? ClipboardProviderFactory { get; init; }
    public Func<IEditorGitService>? GitServiceFactory { get; init; }
    /// <summary>
    /// The host's LSP session. Supply it to turn LSP on: the control takes one
    /// <see cref="ILspDocument"/> handle per buffer from it and keeps only view state itself.
    /// When null, LSP is off for this control.
    /// </summary>
    public ILspWorkspace? LspWorkspace { get; init; }

    /// <summary>
    /// Write access to the host's extension→server table, backing the <c>:LspAdd</c>/<c>:LspRemove</c>/
    /// <c>:LspList</c>/<c>:LspReset</c> ex commands. Must be the same table
    /// <see cref="LspWorkspace"/> resolves servers from; when null those commands report that they
    /// are unavailable rather than editing a table nobody reads.
    /// </summary>
    public ILspServerAdmin? LspServerAdmin { get; init; }
    public SyntaxLanguageRegistry? SyntaxLanguages { get; init; }
    public EditorCommandRegistry? Commands { get; init; }
    public IServiceProvider? CommandServices { get; init; }
    public VimEngineServices? EngineServices { get; init; }

    /// <summary>
    /// Rules for saving a pasted clipboard image and the Markdown link written in its place
    /// (relative directory + file-name templates). When null the control uses defaults
    /// (<c>images/{filename}-{datetime}.png</c>); the effective instance is exposed and
    /// mutable via <see cref="VimEditorControl.ImagePasteOptions"/>.
    /// </summary>
    public ImagePasteOptions? ImagePasteOptions { get; init; }
}
