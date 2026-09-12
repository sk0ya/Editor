using Editor.Core.Lsp;
using Editor.Core.Navigation;

namespace Editor.Core.Tests;

public class BreadcrumbTests
{
    private static string[] Lines(string text) =>
        text.Replace("\r\n", "\n").Split('\n');

    private static string[] Path(string text, string file, int line, int col)
    {
        var symbols = DocumentSymbolExtractor.Extract(Lines(text), file);
        return BreadcrumbBuilder.GetSegments(symbols, line, col).Select(s => s.Name).ToArray();
    }

    // ── BreadcrumbBuilder ──

    [Fact]
    public void Builder_FlatSymbols_OrdersOutermostFirst()
    {
        // Flat list (no children), class range encloses the method range.
        var cls = new DocumentSymbol("Bar", SymbolKind.Class,
            new LspRange(new LspPosition(0, 0), new LspPosition(10, 0)),
            new LspRange(new LspPosition(0, 6), new LspPosition(0, 9)), null);
        var method = new DocumentSymbol("Baz", SymbolKind.Method,
            new LspRange(new LspPosition(3, 0), new LspPosition(6, 0)),
            new LspRange(new LspPosition(3, 9), new LspPosition(3, 12)), null);

        var segs = BreadcrumbBuilder.GetSegments([cls, method], 4, 8);
        Assert.Equal(["Bar", "Baz"], segs.Select(s => s.Name));
    }

    [Fact]
    public void Builder_CsharpLsZeroRangeFileRoot_DescendsIntoChildren()
    {
        // csharp-ls returns a File root named after the file with an EMPTY (0,0)-(0,0) range,
        // nesting the real symbols underneath. The breadcrumb must descend past it.
        var method = new DocumentSymbol("GetLine", SymbolKind.Method,
            new LspRange(new LspPosition(18, 0), new LspPosition(22, 0)),
            new LspRange(new LspPosition(18, 18), new LspPosition(18, 25)), null);
        var cls = new DocumentSymbol("TextBuffer", SymbolKind.Class,
            new LspRange(new LspPosition(4, 0), new LspPosition(260, 0)),
            new LspRange(new LspPosition(4, 17), new LspPosition(4, 27)), [method]);
        var fileRoot = new DocumentSymbol("TextBuffer.cs", SymbolKind.File,
            new LspRange(new LspPosition(0, 0), new LspPosition(0, 0)),
            new LspRange(new LspPosition(0, 0), new LspPosition(0, 0)), [cls]);

        var segs = BreadcrumbBuilder.GetSegments([fileRoot], 20, 8);
        Assert.Equal(["TextBuffer", "GetLine"], segs.Select(s => s.Name));
    }

    [Fact]
    public void Builder_NoContainingSymbol_ReturnsEmpty()
    {
        var cls = new DocumentSymbol("Bar", SymbolKind.Class,
            new LspRange(new LspPosition(0, 0), new LspPosition(2, 0)),
            new LspRange(new LspPosition(0, 6), new LspPosition(0, 9)), null);
        Assert.Empty(BreadcrumbBuilder.GetSegments([cls], 50, 0));
    }

    [Fact]
    public void Builder_SegmentCarriesSelectionStart()
    {
        var method = new DocumentSymbol("Baz", SymbolKind.Method,
            new LspRange(new LspPosition(3, 0), new LspPosition(6, 0)),
            new LspRange(new LspPosition(3, 9), new LspPosition(3, 12)), null);
        var seg = BreadcrumbBuilder.GetSegments([method], 4, 0).Single();
        Assert.Equal(3, seg.Line);
        Assert.Equal(9, seg.Column);
    }

    // ── DocumentSymbolExtractor (brace) ──

    private const string CSharp = """
        namespace Foo
        {
            class Bar
            {
                void Baz()
                {
                    int x = 1;
                }
            }
        }
        """;

    [Fact]
    public void CSharp_InsideMethod_FullPath()
        => Assert.Equal(["Foo", "Bar", "Baz"], Path(CSharp, "a.cs", 6, 20));

    [Fact]
    public void CSharp_OnClassLine_StopsAtClass()
        => Assert.Equal(["Foo", "Bar"], Path(CSharp, "a.cs", 2, 4));

    [Fact]
    public void CSharp_SameLineBrace_Works()
    {
        var text = """
            class A {
                int Method() {
                    return 0;
                }
            }
            """;
        Assert.Equal(["A", "Method"], Path(text, "a.cs", 2, 8));
    }

    [Fact]
    public void CSharp_ControlFlowBlocksAreNotSymbols()
    {
        var text = """
            class A {
                void M() {
                    if (true) {
                        var y = 1;
                    }
                }
            }
            """;
        // Inside the if-block: should still be A › M, not an "if" segment.
        Assert.Equal(["A", "M"], Path(text, "a.cs", 3, 12));
    }

    // ── DocumentSymbolExtractor (python) ──

    [Fact]
    public void Python_InsideMethod_FullPath()
    {
        var text = """
            class Foo:
                def bar(self):
                    x = 1
            """;
        Assert.Equal(["Foo", "bar"], Path(text, "a.py", 2, 8));
    }

    [Fact]
    public void Python_TopLevelFunction()
    {
        var text = """
            def main():
                run()
            """;
        Assert.Equal(["main"], Path(text, "a.py", 1, 4));
    }

    [Fact]
    public void UnsupportedExtension_ReturnsEmpty()
        => Assert.Empty(Path("just some text\nmore text", "a.txt", 1, 0));

    // ── DocumentSymbolExtractor (markdown) ──

    private const string Markdown = """
        # Title

        intro

        ## Section A

        body of A

        ### Sub A1

        deep content

        ## Section B

        body of B
        """;

    [Fact]
    public void Markdown_NestedHeadingPath()
        => Assert.Equal(["Title", "Section A", "Sub A1"], Path(Markdown, "doc.md", 11, 0));

    [Fact]
    public void Markdown_SiblingSectionResetsDepth()
        => Assert.Equal(["Title", "Section B"], Path(Markdown, "doc.md", 14, 0));

    [Fact]
    public void Markdown_IgnoresHeadingsInFencedCode()
    {
        var text = """
            # Real

            ```
            # not a heading
            ```

            text under Real
            """;
        Assert.Equal(["Real"], Path(text, "doc.md", 6, 0));
    }
    // ── DocumentSymbolExtractor (XML) ──

    private const string Xml = """
        <?xml version="1.0" encoding="utf-8"?>
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net9.0</TargetFramework>
          </PropertyGroup>
          <ItemGroup>
            <PackageReference Include="xunit" Version="2.9.0" />
          </ItemGroup>
        </Project>
        """;

    [Fact]
    public void Xml_InsideNestedElement_FullPath()
        => Assert.Equal(["Project", "PropertyGroup", "TargetFramework"], Path(Xml, "a.csproj", 3, 25));

    [Fact]
    public void Xml_SelfClosingElementIsASegment()
        => Assert.Equal(["Project", "ItemGroup", "PackageReference#xunit"], Path(Xml, "a.csproj", 6, 20));

    [Fact]
    public void Xml_BetweenSiblings_StopsAtParent()
        => Assert.Equal(["Project"], Path(Xml, "a.csproj", 5, 0));

    [Fact]
    public void Xml_DetectedByDeclarationWhenExtensionIsUnknown()
        => Assert.Equal(["Project", "PropertyGroup", "TargetFramework"], Path(Xml, "dump.txt", 3, 25));

    [Fact]
    public void Xml_XamlWithoutDeclaration_UsesNameAttribute()
    {
        var text = """
            <Window x:Class="App.MainWindow">
              <Grid x:Name="Root">
                <Button Content="OK" />
              </Grid>
            </Window>
            """;
        Assert.Equal(["Window", "Grid#Root", "Button"], Path(text, "MainWindow.xaml", 2, 10));
    }

    [Fact]
    public void Xml_IgnoresTagsInCommentsAndCdata()
    {
        var text = """
            <root>
              <!-- <ghost> -->
              <data><![CDATA[ <ghost2> ]]></data>
              <real>x</real>
            </root>
            """;
        Assert.Equal(["root", "real"], Path(text, "a.xml", 3, 9));
    }

    [Fact]
    public void Xml_AngleBracketInAttributeValueDoesNotEndTag()
    {
        var text = """
            <root>
              <rule pattern="a > b">
                <hit/>
              </rule>
            </root>
            """;
        Assert.Equal(["root", "rule", "hit"], Path(text, "a.xml", 2, 6));
    }

    [Fact]
    public void Xml_SiblingsOnOneLine_ResolveByColumn()
    {
        var text = "<a><b/><c>x</c></a>";
        Assert.Equal(["a", "b"], Path(text, "a.xml", 0, 4));
        Assert.Equal(["a", "c"], Path(text, "a.xml", 0, 11));
    }

    [Fact]
    public void Xml_UnclosedTagWhileTyping_StillGivesAPath()
    {
        var text = """
            <root>
              <section>
                text
            """;
        Assert.Equal(["root", "section"], Path(text, "a.xml", 2, 4));
    }

    [Fact]
    public void Xml_StrayClosingTagIsIgnored()
    {
        var text = """
            <root>
              </nope>
              <ok>x</ok>
            </root>
            """;
        Assert.Equal(["root", "ok"], Path(text, "a.xml", 2, 7));
    }

    [Fact]
    public void Xml_DoctypeWithInternalSubsetIsSkipped()
    {
        var text = """
            <?xml version="1.0"?>
            <!DOCTYPE root [
              <!ELEMENT root (#PCDATA)>
            ]>
            <root>
              <child>x</child>
            </root>
            """;
        Assert.Equal(["root", "child"], Path(text, "a.xml", 5, 10));
    }

    [Fact]
    public void Xml_LongIncludePath_KeepsTheFileName()
    {
        var text = """
            <Project>
              <ItemGroup>
                <ProjectReference Include="..\..\..\Editor\src\Editor.Controls\Editor.Controls.csproj" />
              </ItemGroup>
            </Project>
            """;
        Assert.Equal(["Project", "ItemGroup", "ProjectReference#Editor.Controls.csproj"],
            Path(text, "a.csproj", 2, 30));
    }

    [Fact]
    public void Xml_SegmentJumpsToTagName()
    {
        var symbols = DocumentSymbolExtractor.Extract(Lines(Xml), "a.csproj");
        var seg = BreadcrumbBuilder.GetSegments(symbols, 3, 25).Last();
        Assert.Equal(3, seg.Line);
        Assert.Equal(5, seg.Column); // just past '<' — the tag name itself
    }
}
