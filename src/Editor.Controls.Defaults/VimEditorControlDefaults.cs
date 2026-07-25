using Editor.Controls.Git;
using Editor.Controls.Lsp;
using Editor.Core.Engine;

namespace Editor.Controls;

public static class VimEditorControlDefaults
{
    private static readonly Lazy<VimEngineServices> ApplicationServices =
        new(() => VimEngineServices.CreateApplication());

    public static VimEditorControlOptions CreateOptions() =>
        new()
        {
            EngineServices = ApplicationServices.Value,
            GitServiceFactory = static () => new GitDiffProvider(),
            LspManagerFactory = dispatcher =>
                new LspManager(dispatcher, ApplicationServices.Value.LspServers)
        };
}
