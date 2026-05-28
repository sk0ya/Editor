namespace Editor.Controls.Git;

public interface IEditorGitService
{
    Dictionary<int, GitLineState> GetDiff(string filePath);
    string? GetBranchName(string repoPath);
    string GetDiffOutput(string filePath);
    string GetLogOutput(string repoPath, int count = 30);
    (bool Success, string Output) RunCommit(string filePath, string message);
    (bool Success, string Output) StageHunk(string filePath, int line);
    (bool Success, string Output) UnstageHunk(string filePath, int line);
    Dictionary<int, string> GetBlameAnnotations(string filePath);
}
