using System.Collections;
using System.Reflection;
using Editor.Controls.Rendering;
using Editor.Core.Lsp;
using Editor.Core.Syntax;

namespace Editor.Controls.Tests;

public sealed class EditorCanvasSemanticTokenTests
{
    [Fact]
    public void Semantic_type_does_not_overwrite_a_lexical_string_range()
    {
        WpfTestHost.Run(() =>
        {
            const string line = "var text = \"x\";";
            var canvas = new EditorCanvas();
            canvas.SetLines([line]);
            canvas.SetTokens([new LineTokens(0, [
                new SyntaxToken(0, 3, TokenKind.Keyword),
                new SyntaxToken(11, 3, TokenKind.String),
            ])]);
            canvas.SetSemanticTokens([new SemanticToken(0, 11, 3, "class", [])]);

            var method = typeof(EditorCanvas).GetMethod(
                "BuildColorSegments", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var segments = (IEnumerable)method.Invoke(canvas, [0, line.Length])!;
            var stringSegment = segments.Cast<object>().Single(item =>
                (int)item.GetType().GetField("Item1")!.GetValue(item)! == 11);

            Assert.Same(canvas.Theme.GetTokenBrush(TokenKind.String),
                stringSegment.GetType().GetField("Item3")!.GetValue(stringSegment));
        });
    }

    [Fact]
    public void Semantic_modifiers_are_preserved_as_italic_and_deprecated_styles()
    {
        WpfTestHost.Run(() =>
        {
            const string line = "OldApi";
            var canvas = new EditorCanvas();
            canvas.SetLines([line]);
            canvas.SetSemanticTokens([
                new SemanticToken(0, 0, line.Length, "method", ["readonly", "deprecated"])
            ]);

            var method = typeof(EditorCanvas).GetMethod(
                "BuildColorSegments", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var segments = (IEnumerable)method.Invoke(canvas, [0, line.Length])!;
            var segment = segments.Cast<object>().Single();

            Assert.True((bool)segment.GetType().GetField("Item4")!.GetValue(segment)!);
            Assert.True((bool)segment.GetType().GetField("Item5")!.GetValue(segment)!);
        });
    }
}
