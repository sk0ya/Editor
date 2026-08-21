using System.Reflection;
using System.Windows;
using Editor.Core.Syntax;

namespace Editor.Controls.Tests;

/// <summary>
/// A host that renames the open file (explorer rename, move) tells the control with
/// <see cref="VimEditorControl.RebaseFilePath"/>. Setting the buffer's path alone left the
/// canvas painted with the *old* extension's language — the visible symptom being C# code
/// still rendered as plain text after a .txt → .cs rename.
/// </summary>
public sealed class RebaseFilePathRenderTests
{
    [Fact]
    public void Renaming_to_a_source_extension_repaints_with_the_new_language()
    {
        WithEditor((editor, dir) =>
        {
            var txt = WriteFile(dir, "sample.txt", "class C { int x; }");
            editor.LoadFile(txt);
            Assert.Empty(TokensOnFirstLine(editor));

            var cs = Path.Combine(dir, "sample.cs");
            File.Move(txt, cs);
            editor.RebaseFilePath(cs);

            Assert.Equal("C#", editor.Engine.Syntax.LanguageName);
            Assert.Contains(TokensOnFirstLine(editor), t => t.Kind == TokenKind.Keyword);
        });
    }

    [Fact]
    public void Renaming_away_from_a_source_extension_drops_the_highlighting()
    {
        WithEditor((editor, dir) =>
        {
            var cs = WriteFile(dir, "sample2.cs", "class C { int x; }");
            editor.LoadFile(cs);
            Assert.NotEmpty(TokensOnFirstLine(editor));

            var txt = Path.Combine(dir, "sample2.txt");
            File.Move(cs, txt);
            editor.RebaseFilePath(txt);

            Assert.Null(editor.Engine.Syntax.LanguageName);
            Assert.Empty(TokensOnFirstLine(editor));
        });
    }

    [Fact]
    public void Rebasing_does_not_reread_the_file()
    {
        WithEditor((editor, dir) =>
        {
            var path = WriteFile(dir, "dirty.txt", "one");
            editor.LoadFile(path);
            editor.SetText("edited by the user");

            var renamed = Path.Combine(dir, "dirty.cs");
            File.Move(path, renamed);
            editor.RebaseFilePath(renamed);

            Assert.Equal("edited by the user", editor.Engine.CurrentBuffer.Text.GetText().TrimEnd('\n'));
            Assert.Equal(renamed, editor.FilePath);
        });
    }

    /// <summary>What the canvas is actually painting for line 0 — the rendered result, not the engine's opinion.</summary>
    private static SyntaxToken[] TokensOnFirstLine(VimEditorControl editor)
    {
        var canvas = typeof(VimEditorControl)
            .GetField("Canvas", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(editor)!;
        var byLine = (Dictionary<int, SyntaxToken[]>)canvas.GetType()
            .GetField("_tokensByLine", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(canvas)!;
        return byLine.TryGetValue(0, out var tokens) ? tokens : [];
    }

    private static string WriteFile(string dir, string name, string text)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, text);
        return path;
    }

    private static void WithEditor(Action<VimEditorControl, string> action)
        => WpfTestHost.Run(() =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "editor-rebase-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var editor = new VimEditorControl();
            Window? window = null;
            try
            {
                window = WpfTestHost.Load(editor);
                action(editor, dir);
            }
            finally
            {
                try { window?.Close(); }
                finally
                {
                    if (window != null) window.Content = null;
                    editor.Dispose();
                    try { Directory.Delete(dir, recursive: true); } catch { }
                }
            }
        });
}
