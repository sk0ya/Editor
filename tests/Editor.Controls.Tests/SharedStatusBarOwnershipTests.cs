using System.Windows;

namespace Editor.Controls.Tests;

/// <summary>
/// A shared <see cref="VimStatusBar"/> is written to by every editor that hangs off it. Without an
/// owner, a background editor repainting itself overwrites the current file's name and line count —
/// which is exactly what made a host's status bar lag one tab behind after a tab switch.
/// </summary>
public sealed class SharedStatusBarOwnershipTests
{
    [Fact]
    public void Claiming_editor_shows_its_own_file()
    {
        WithTwoEditors((bar, first, second) =>
        {
            LoadFile(first, "alpha.txt", "a\nb\nc");
            LoadFile(second, "beta.txt", "x");

            second.SyncStatusBar();

            AssertShows("beta.txt", bar);
        });
    }

    [Fact]
    public void Background_editor_cannot_overwrite_the_shared_bar()
    {
        WithTwoEditors((bar, first, second) =>
        {
            LoadFile(first, "alpha.txt", "a\nb\nc");
            LoadFile(second, "beta.txt", "x");
            second.SyncStatusBar();

            // The background editor repaints (buffer change, save, re-render...). Before ownership
            // this call put "alpha.txt" back on the shared bar.
            first.SetText("a\nb\nc\nd");

            AssertShows("beta.txt", bar);
        });
    }

    [Fact]
    public void Claim_moves_with_each_activation()
    {
        WithTwoEditors((bar, first, second) =>
        {
            LoadFile(first, "alpha.txt", "a");
            LoadFile(second, "beta.txt", "x");

            second.SyncStatusBar();
            AssertShows("beta.txt", bar);

            first.SyncStatusBar();
            AssertShows("alpha.txt", bar);
        });
    }

    [Fact]
    public void Unclaimed_bar_accepts_any_editor()
    {
        // Nobody has claimed it yet: keep the previous behaviour so a single-editor host that never
        // calls SyncStatusBar still gets a populated bar.
        WithTwoEditors((bar, first, _) =>
        {
            LoadFile(first, "alpha.txt", "a");

            AssertShows("alpha.txt", bar);
        });
    }

    [Fact]
    public void Detaching_the_bar_releases_the_claim()
    {
        WithTwoEditors((bar, first, second) =>
        {
            LoadFile(first, "alpha.txt", "a");
            LoadFile(second, "beta.txt", "x");
            first.SyncStatusBar();

            // The host stops sharing the bar with this editor; it must not stay pinned to it, or no
            // other editor could ever write to the bar again.
            first.SetSharedStatusBar(null);
            second.SyncStatusBar();

            AssertShows("beta.txt", bar);
        });
    }

    /// <summary>The bar renders "name [format]" (and "[+]" when modified); assert on the name.</summary>
    private static void AssertShows(string expectedFileName, VimStatusBar bar)
        => Assert.StartsWith(expectedFileName, bar.FileText.Text, StringComparison.Ordinal);

    private static void LoadFile(VimEditorControl editor, string fileName, string text)
    {
        var path = System.IO.Path.Combine(TempDir, fileName);
        System.IO.File.WriteAllText(path, text);
        editor.LoadFile(path);
    }

    private static string TempDir { get; } = CreateTempDir();

    private static string CreateTempDir()
    {
        var dir = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "editor-statusbar-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Two editors sharing one status bar — the split / multi-tab host arrangement.</summary>
    private static void WithTwoEditors(Action<VimStatusBar, VimEditorControl, VimEditorControl> action)
        => WpfTestHost.Run(() =>
        {
            var bar = new VimStatusBar();
            var first = new VimEditorControl();
            var second = new VimEditorControl();
            var panel = new System.Windows.Controls.StackPanel();
            panel.Children.Add(first);
            panel.Children.Add(second);
            panel.Children.Add(bar);
            Window? window = null;
            try
            {
                window = WpfTestHost.Load(panel);
                first.SetSharedStatusBar(bar);
                second.SetSharedStatusBar(bar);
                action(bar, first, second);
            }
            finally
            {
                try { window?.Close(); }
                finally
                {
                    if (window != null) window.Content = null;
                    first.Dispose();
                    second.Dispose();
                }
            }
        });
}
