namespace Editor.Controls.Git;

internal sealed class NullEditorGitService : IEditorGitService
{
    public static NullEditorGitService Instance { get; } = new();

    private NullEditorGitService()
    {
    }

    public Dictionary<int, GitLineState> GetDiff(string filePath) => [];

    public string? GetBranchName(string repoPath) => null;

    public string GetStatusOutput(string repoPath) => "(git integration is not configured)";

    public string GetDiffOutput(string filePath) => "(git integration is not configured)";

    public string GetLogOutput(string repoPath, int count = 30) => "(git integration is not configured)";

    public (bool Success, string Output) RunCommit(string filePath, string message) =>
        (false, "git integration is not configured");

    public (bool Success, string Output) StageHunk(string filePath, int line) =>
        (false, "git integration is not configured");

    public (bool Success, string Output) UnstageHunk(string filePath, int line) =>
        (false, "git integration is not configured");

    public Dictionary<int, string> GetBlameAnnotations(string filePath) => [];
}
