using System.Collections.Generic;
using System.Threading.Tasks;

namespace Editor.Controls.Git;

/// <summary>
/// <see cref="IEditorGitService"/> の非同期版（任意）。ホストの実装がこれも持つなら、エディタはファイルを開くたび・
/// 保存のたび・カーソル行の blame のたびに引く 3 つ（ガターの差分・ブランチ名・blame）をこちらで待つ。
/// <para>同期版しか無いと、エディタは <c>Task.Run</c> の中でそれを呼ぶので、git が動いている間ずっとスレッドプールの
/// スレッドを 1 本塞ぐ（大きなファイルの blame は秒単位）。プロセスの出力を非同期に読めるホストなら、待つ間は
/// スレッドを返せる。<c>:Git</c> 系のコマンドは人が打ったときにしか走らないので同期版のまま。</para>
/// </summary>
public interface IAsyncEditorGitService : IEditorGitService
{
    Task<Dictionary<int, GitLineState>> GetDiffAsync(string filePath);
    Task<string?> GetBranchNameAsync(string repoPath);
    /// <summary>ファイルの行ごとの blame 情報（0始まり行 → コミット情報）。コミットされていない行は含めない。</summary>
    Task<Dictionary<int, EditorBlameLine>> GetBlameLinesAsync(string filePath);
}
