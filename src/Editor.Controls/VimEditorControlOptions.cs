using System.Windows.Threading;
using Editor.Controls.Git;
using Editor.Controls.Lsp;
using Editor.Core.Config;
using Editor.Core.Editing;
using Editor.Core.Registers;
using Editor.Core.Extensibility;
using Editor.Core.Syntax;

namespace Editor.Controls;

public sealed class VimEditorControlOptions
{
    public Func<VimConfig>? ConfigFactory { get; init; }
    public Func<IClipboardProvider>? ClipboardProviderFactory { get; init; }
    public Func<IEditorGitService>? GitServiceFactory { get; init; }
    public Func<Dispatcher, IEditorLspManager>? LspManagerFactory { get; init; }
    public SyntaxLanguageRegistry? SyntaxLanguages { get; init; }
    public EditorCommandRegistry? Commands { get; init; }
    public IServiceProvider? CommandServices { get; init; }

    /// <summary>
    /// Rules for saving a pasted clipboard image and the Markdown link written in its place
    /// (relative directory + file-name templates). When null the control uses defaults
    /// (<c>images/{filename}-{datetime}.png</c>); the effective instance is exposed and
    /// mutable via <see cref="VimEditorControl.ImagePasteOptions"/>.
    /// </summary>
    public ImagePasteOptions? ImagePasteOptions { get; init; }
}
