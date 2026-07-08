using Editor.Core.Models;

namespace Editor.Core.Engine.ExCommands;

/// <summary>Handles the :Git* ex commands (blame/status/commit/stage/unstage/diff/log/push/pull).</summary>
public static class GitCommands
{
    public static bool TryHandle(string cmd, out ExResult result)
    {
        // :Git blame / :Gblame — toggle inline git blame annotations
        if (cmd is "Git blame" or "git blame" or "Gblame" or "gblame")
        {
            result = new ExResult(true, null, VimEvent.GitBlameRequested());
            return true;
        }

        // :Git status / :Gstatus / :gs — show repository status in a new buffer
        if (cmd is "Git status" or "git status" or "Gstatus" or "gstatus" or "gs")
        {
            result = new ExResult(true, null, VimEvent.GitStatusRequested());
            return true;
        }

        // :Git commit / :Gcommit — open commit message editor
        if (cmd is "Git commit" or "git commit" or "Gcommit" or "gcommit")
        {
            result = new ExResult(true, null, VimEvent.GitCommitRequested());
            return true;
        }

        // :Git stage / :Gstage — stage the current git hunk
        if (cmd is "Git stage" or "git stage" or "Gstage" or "gstage")
        {
            result = new ExResult(true, null, VimEvent.HunkStageRequested(true));
            return true;
        }

        // :Git unstage / :Gunstage — unstage the current git hunk
        if (cmd is "Git unstage" or "git unstage" or "Gunstage" or "gunstage")
        {
            result = new ExResult(true, null, VimEvent.HunkStageRequested(false));
            return true;
        }

        // :Git diff / :Gdiff — show git diff output in a new buffer
        if (cmd is "Git diff" or "git diff" or "Gdiff" or "gdiff")
        {
            result = new ExResult(true, null, VimEvent.GitDiffRequested());
            return true;
        }

        // :Git log / :Glog — show git log output in a new buffer
        if (cmd is "Git log" or "git log" or "Glog" or "glog")
        {
            result = new ExResult(true, null, VimEvent.GitLogRequested());
            return true;
        }

        // :Git push / :Gpush — push the current branch
        if (cmd is "Git push" or "git push" or "Gpush" or "gpush")
        {
            result = new ExResult(true, null, VimEvent.GitPushRequested());
            return true;
        }

        // :Git pull / :Gpull — pull into the current branch
        if (cmd is "Git pull" or "git pull" or "Gpull" or "gpull")
        {
            result = new ExResult(true, null, VimEvent.GitPullRequested());
            return true;
        }

        result = default!;
        return false;
    }
}
