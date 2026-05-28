namespace Editor.Controls.Git;

public interface IEditorGitService
{
    Dictionary<int, GitLineState> GetDiff(string filePath);
    string? GetBranchName(string repoPath);
    string GetDiffOutput(string filePath);
    string GetLogOutput(string repoPath, int count = 30);
    (bool Success, string Output) RunCommit(string filePath, string message);
    Dictionary<int, string> GetBlameAnnotations(string filePath);
}
