using System.Reflection;
using Editor.Controls.Rendering;
using Editor.Core.Lsp;

namespace Editor.Controls.Tests;

public sealed class CodeLensScaleTests
{
    [Fact]
    public void Indexes_many_lenses_by_line_without_duplicate_line_failures()
    {
        WpfTestHost.Run(() =>
        {
            var lenses = Enumerable.Range(0, 20_000)
                .Select(line => new LspCodeLens(
                    new LspRange(new(line, 0), new(line, 0)),
                    new LspCodeActionCommand("test.run", $"Run {line}")))
                .ToArray();
            var canvas = new EditorCanvas();

            canvas.SetCodeLenses(lenses);

            var indexed = (System.Collections.IDictionary)typeof(EditorCanvas)
                .GetField("_codeLensesByLine", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(canvas)!;
            Assert.Equal(lenses.Length, indexed.Count);
        });
    }

    [Fact]
    public void Canvas_does_not_index_unresolved_code_lenses()
    {
        WpfTestHost.Run(() =>
        {
            var executable = new LspCodeLens(
                new LspRange(new(0, 0), new(0, 1)),
                new LspCodeActionCommand("test.run", "Run"));
            var unresolved = new LspCodeLens(
                new LspRange(new(1, 0), new(1, 1)),
                RawJson: "{}");
            var canvas = new EditorCanvas();

            canvas.SetCodeLenses([executable, unresolved]);

            var indexed = (System.Collections.IDictionary)typeof(EditorCanvas)
                .GetField("_codeLensesByLine", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(canvas)!;
            Assert.Single(indexed);
            Assert.Contains(0, indexed.Keys.Cast<int>());
        });
    }
}
