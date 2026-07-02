namespace Editor.Controls.Git;

/// <summary>blame 1行分のコミット情報。<see cref="Display"/> が blame 左カラムの表示テキスト。
/// <see cref="OriginalLine"/> はそのコミット時点のファイルでの行番号（1始まり・不明なら 0）で、
/// ホストがコミット差分の該当行へジャンプするために使う。</summary>
public sealed record EditorBlameLine(
    string CommitHash, string Author, string Date, string Summary, int OriginalLine = 0)
{
    /// <summary>blame 左カラムに表示する注釈テキスト。</summary>
    public string Display => $"{CommitHash} ({Author}, {Date})";

    /// <summary>ホバーツールチップに表示するテキスト（コミットメッセージの要約行つき）。</summary>
    public string Tooltip => Summary.Length > 0 ? $"{Display}\n{Summary}" : Display;
}

public interface IEditorGitService
{
    Dictionary<int, GitLineState> GetDiff(string filePath);
    string? GetBranchName(string repoPath);
    string GetStatusOutput(string repoPath);
    string GetDiffOutput(string filePath);
    string GetLogOutput(string repoPath, int count = 30);
    (bool Success, string Output) RunPush(string repoPath);
    (bool Success, string Output) RunPull(string repoPath);
    (bool Success, string Output) RunCommit(string filePath, string message);
    (bool Success, string Output) StageHunk(string filePath, int line);
    (bool Success, string Output) UnstageHunk(string filePath, int line);
    /// <summary>ファイルの行ごとの blame 情報（0始まり行 → コミット情報）。コミットされていない行は含めない。</summary>
    Dictionary<int, EditorBlameLine> GetBlameLines(string filePath);
}
