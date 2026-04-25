using Editor.Controls.Git;
using Editor.Controls.Lsp;

namespace Editor.Controls;

public static class VimEditorControlDefaults
{
    public static VimEditorControlOptions CreateOptions() =>
        new()
        {
            GitServiceFactory = static () => new GitDiffProvider(),
            LspManagerFactory = dispatcher => new LspManager(dispatcher)
        };
}
