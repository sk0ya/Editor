using Editor.Core.Config;
using Editor.Core.Engine;

namespace Editor.Core.Tests;

/// <summary>
/// リネーム／移動でホストがバッファのパスだけ差し替えるときの挙動。
/// 本文・未保存の編集は保ったまま、拡張子が変わったらシンタックスが追随することを確認する。
/// </summary>
public class RebaseFilePathTests
{
    private static string WriteTemp(string extension, string text)
    {
        var path = Path.Combine(Path.GetTempPath(), "editor-rebase-" + Guid.NewGuid().ToString("N") + extension);
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public void RebaseFilePath_ExtensionChanged_ReDetectsSyntaxLanguage()
    {
        var path = WriteTemp(".txt", "class C { }\n");
        try
        {
            var engine = new VimEngine(new VimConfig());
            engine.LoadFile(path);
            Assert.Null(engine.Syntax.LanguageName);   // .txt には言語なし

            var renamed = Path.ChangeExtension(path, ".cs");
            engine.RebaseFilePath(renamed);

            Assert.Equal(renamed, engine.CurrentBuffer.FilePath);
            Assert.Equal("C#", engine.Syntax.LanguageName);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void RebaseFilePath_ExtensionRemoved_ClearsSyntaxLanguage()
    {
        var path = WriteTemp(".cs", "class C { }\n");
        try
        {
            var engine = new VimEngine(new VimConfig());
            engine.LoadFile(path);
            Assert.Equal("C#", engine.Syntax.LanguageName);

            engine.RebaseFilePath(Path.ChangeExtension(path, ".txt"));

            Assert.Null(engine.Syntax.LanguageName);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void RebaseFilePath_KeepsUnsavedEditsAndUndoHistory()
    {
        var path = WriteTemp(".txt", "one\n");
        try
        {
            var engine = new VimEngine(new VimConfig());
            engine.LoadFile(path);
            engine.ProcessKey("o", false, false, false);   // 新しい行を開いて追記する
            foreach (var key in "two")
                engine.ProcessKey(key.ToString(), false, false, false);
            engine.ProcessKey("Escape", false, false, false);

            engine.RebaseFilePath(Path.ChangeExtension(path, ".cs"));

            Assert.Contains("two", engine.CurrentBuffer.Text.GetText());
            Assert.True(engine.CurrentBuffer.Text.IsModified);   // ディスクを読み直していない
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void RebaseFilePath_EmptyPath_IsIgnored()
    {
        var path = WriteTemp(".cs", "class C { }\n");
        try
        {
            var engine = new VimEngine(new VimConfig());
            engine.LoadFile(path);

            engine.RebaseFilePath("");

            Assert.Equal(path, engine.CurrentBuffer.FilePath);
            Assert.Equal("C#", engine.Syntax.LanguageName);
        }
        finally { File.Delete(path); }
    }
}
