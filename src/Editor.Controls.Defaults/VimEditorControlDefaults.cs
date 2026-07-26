using Editor.Controls.Git;
using Editor.Core.Engine;

namespace Editor.Controls;

public static class VimEditorControlDefaults
{
    private static readonly Lazy<VimEngineServices> ApplicationServices =
        new(() => VimEngineServices.CreateApplication());

    /// <summary>
    /// Options for a standalone editor: shared engine services and git, but <b>no LSP</b>.
    /// An LSP session is workspace-scoped (server processes, workspace roots, per-URI document
    /// reference counts) and only a host can own one — supply
    /// <see cref="VimEditorControlOptions.LspWorkspace"/> to turn it on.
    /// </summary>
    public static VimEditorControlOptions CreateOptions() =>
        new()
        {
            EngineServices = ApplicationServices.Value,
            GitServiceFactory = static () => new GitDiffProvider(),
        };
}
