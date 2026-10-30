using System.Collections.Generic;
using System.Threading.Tasks;
using Editor.Controls.Git;

namespace Editor.Controls;

/// <summary>
/// 頻繁に引く git（ガターの差分・ブランチ名・blame）の入口。ホストが <see cref="IAsyncEditorGitService"/> を
/// 持っていればそれを待ち、無ければ従来どおり同期版を <c>Task.Run</c> で背景に回す。
/// </summary>
public partial class VimEditorControl
{
    private Task<Dictionary<int, GitLineState>> GetGitDiffAsync(string filePath) =>
        _gitProvider is IAsyncEditorGitService async
            ? async.GetDiffAsync(filePath)
            : Task.Run(() => _gitProvider.GetDiff(filePath));

    private Task<string?> GetGitBranchNameAsync(string repoPath) =>
        _gitProvider is IAsyncEditorGitService async
            ? async.GetBranchNameAsync(repoPath)
            : Task.Run(() => _gitProvider.GetBranchName(repoPath));

    private Task<Dictionary<int, EditorBlameLine>> GetGitBlameLinesAsync(string filePath) =>
        _gitProvider is IAsyncEditorGitService async
            ? async.GetBlameLinesAsync(filePath)
            : Task.Run(() => _gitProvider.GetBlameLines(filePath));
}
