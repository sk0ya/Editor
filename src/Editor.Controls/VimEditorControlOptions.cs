using System.Windows.Threading;
using Editor.Controls.Git;
using Editor.Controls.Lsp;
using Editor.Core.Config;
using Editor.Core.Registers;

namespace Editor.Controls;

public sealed class VimEditorControlOptions
{
    public Func<VimConfig>? ConfigFactory { get; init; }
    public Func<IClipboardProvider>? ClipboardProviderFactory { get; init; }
    public Func<IEditorGitService>? GitServiceFactory { get; init; }
    public Func<Dispatcher, IEditorLspManager>? LspManagerFactory { get; init; }
}
