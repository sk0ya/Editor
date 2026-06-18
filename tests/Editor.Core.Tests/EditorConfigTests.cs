using Editor.Core.Buffer;
using Editor.Core.Config;
using Editor.Core.Engine;

namespace Editor.Core.Tests;

public class EditorConfigTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "EditorConfigTests_" + Guid.NewGuid().ToString("N"));

    public EditorConfigTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void LoadForFile_AppliesMatchingSection()
    {
        File.WriteAllText(Path.Combine(_root, ".editorconfig"), """
            root = true

            [*.cs]
            indent_style = space
            indent_size = 2
            tab_width = 8
            end_of_line = crlf
            charset = utf-8-bom
            """);
        var filePath = Path.Combine(_root, "Program.cs");
        File.WriteAllText(filePath, "class C {}\n");

        var options = new VimOptions();
        var buffer = new VimBuffer(filePath);

        EditorConfig.ApplyForFile(filePath, options, buffer);

        Assert.True(options.ExpandTab);
        Assert.Equal(2, options.ShiftWidth);
        Assert.Equal(8, options.TabStop);
        Assert.Equal("dos", options.FileFormat);
        Assert.Equal("utf-8-bom", options.FileEncoding);
        Assert.Equal("dos", buffer.FileFormat);
        Assert.Equal("utf-8-bom", buffer.FileEncoding);
    }

    [Fact]
    public void LoadForFile_NearerConfigOverridesParent()
    {
        var srcDir = Path.Combine(_root, "src");
        Directory.CreateDirectory(srcDir);
        File.WriteAllText(Path.Combine(_root, ".editorconfig"), """
            root = true

            [*.cs]
            indent_size = 4
            """);
        File.WriteAllText(Path.Combine(srcDir, ".editorconfig"), """
            [*.cs]
            indent_size = 2
            """);
        var filePath = Path.Combine(srcDir, "Program.cs");
        File.WriteAllText(filePath, "class C {}\n");

        var settings = EditorConfig.LoadForFile(filePath);
        var options = new VimOptions();
        settings.ApplyTo(options);

        Assert.Equal(2, options.ShiftWidth);
    }

    [Fact]
    public void LoadForFile_RootTrueStopsParentSearch()
    {
        var srcDir = Path.Combine(_root, "src");
        Directory.CreateDirectory(srcDir);
        File.WriteAllText(Path.Combine(_root, ".editorconfig"), """
            [*.cs]
            indent_size = 8
            """);
        File.WriteAllText(Path.Combine(srcDir, ".editorconfig"), """
            root = true

            [*.cs]
            indent_size = 2
            """);
        var filePath = Path.Combine(srcDir, "Program.cs");
        File.WriteAllText(filePath, "class C {}\n");

        var settings = EditorConfig.LoadForFile(filePath);
        var options = new VimOptions();
        settings.ApplyTo(options);

        Assert.Equal(2, options.ShiftWidth);
    }

    [Fact]
    public void LoadFile_AppliesEditorConfigAfterDetectedFileSettings()
    {
        File.WriteAllText(Path.Combine(_root, ".editorconfig"), """
            root = true

            [*.txt]
            indent_style = tab
            tab_width = 3
            indent_size = tab
            end_of_line = lf
            charset = latin1
            """);
        var filePath = Path.Combine(_root, "notes.txt");
        File.WriteAllText(filePath, "one\r\ntwo\r\n");

        var engine = new VimEngine(new VimConfig());
        engine.LoadFile(filePath);

        Assert.False(engine.Options.ExpandTab);
        Assert.Equal(3, engine.Options.TabStop);
        Assert.Equal(3, engine.Options.ShiftWidth);
        Assert.Equal("unix", engine.Options.FileFormat);
        Assert.Equal("latin1", engine.Options.FileEncoding);
        Assert.Equal("unix", engine.CurrentBuffer.FileFormat);
        Assert.Equal("latin1", engine.CurrentBuffer.FileEncoding);
    }

    [Fact]
    public void LoadFile_DecodesBomlessFileUsingEditorConfigCharset()
    {
        File.WriteAllText(Path.Combine(_root, ".editorconfig"), """
            root = true

            [*.txt]
            charset = latin1
            """);
        var filePath = Path.Combine(_root, "notes.txt");
        File.WriteAllBytes(filePath, [0x63, 0x61, 0x66, 0xE9]);

        var engine = new VimEngine(new VimConfig());
        engine.LoadFile(filePath);

        Assert.Equal("caf\u00e9", engine.CurrentBuffer.Text.GetLine(0));
        Assert.Equal("latin1", engine.Options.FileEncoding);
        Assert.Equal("latin1", engine.CurrentBuffer.FileEncoding);
    }

    [Fact]
    public void LoadForFile_UnsetClearsInheritedProperty()
    {
        var srcDir = Path.Combine(_root, "src");
        Directory.CreateDirectory(srcDir);
        File.WriteAllText(Path.Combine(_root, ".editorconfig"), """
            root = true

            [*.cs]
            indent_size = 8
            tab_width = 8
            charset = latin1
            """);
        File.WriteAllText(Path.Combine(srcDir, ".editorconfig"), """
            [*.cs]
            indent_size = unset
            tab_width = unset
            charset = unset
            """);
        var filePath = Path.Combine(srcDir, "Program.cs");
        File.WriteAllText(filePath, "class C {}\n");

        var settings = EditorConfig.LoadForFile(filePath);
        var options = new VimOptions();
        settings.ApplyTo(options);

        Assert.Equal(2, options.ShiftWidth);
        Assert.Equal(2, options.TabStop);
        Assert.Equal("utf-8", options.FileEncoding);
    }

    [Fact]
    public void LoadForFile_SupportsLeadingSlashPathPatterns()
    {
        var srcDir = Path.Combine(_root, "src");
        Directory.CreateDirectory(srcDir);
        File.WriteAllText(Path.Combine(_root, ".editorconfig"), """
            root = true

            [/src/*.cs]
            indent_size = 7
            """);
        var filePath = Path.Combine(srcDir, "Program.cs");
        File.WriteAllText(filePath, "class C {}\n");

        var settings = EditorConfig.LoadForFile(filePath);
        var options = new VimOptions();
        settings.ApplyTo(options);

        Assert.Equal(7, options.ShiftWidth);
    }

    [Fact]
    public void LoadForFile_SupportsPathAndBracePatterns()
    {
        var srcDir = Path.Combine(_root, "src");
        Directory.CreateDirectory(srcDir);
        File.WriteAllText(Path.Combine(_root, ".editorconfig"), """
            root = true

            [src/*.{cs,txt}]
            indent_size = 6
            """);
        var filePath = Path.Combine(srcDir, "notes.txt");
        File.WriteAllText(filePath, "text\n");

        var settings = EditorConfig.LoadForFile(filePath);
        var options = new VimOptions();
        settings.ApplyTo(options);

        Assert.Equal(6, options.ShiftWidth);
    }
}
