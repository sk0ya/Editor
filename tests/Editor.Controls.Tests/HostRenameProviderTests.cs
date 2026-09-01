using Editor.Controls.Lsp;
using Editor.Core.Lsp;

namespace Editor.Controls.Tests;

public sealed class HostRenameProviderTests
{
    [Fact]
    public async Task NullLspView_uses_the_host_rename_provider()
    {
        var called = false;
        var view = new NullLspView
        {
            HostPrepareRenameProvider = (line, character, _) =>
            {
                Assert.Equal(2, line);
                Assert.Equal(4, character);
                return Task.FromResult<LspRange?>(
                    new LspRange(new(2, 0), new(2, 6)));
            },
            HostRenameProvider = (line, character, newName, _) =>
            {
                called = true;
                Assert.Equal(2, line);
                Assert.Equal(4, character);
                Assert.Equal("Renamed", newName);
                return Task.FromResult<LspWorkspaceEdit?>(new LspWorkspaceEdit(
                    new Dictionary<string, IReadOnlyList<LspTextEdit>>
                    {
                        ["file:///sample.cs"] =
                        [new LspTextEdit(new LspRange(new(2, 0), new(2, 6)), newName)],
                    }));
            },
        };

        Assert.True(view.HasHostRenameProvider);
        Assert.True(view.HasHostPrepareRenameProvider);
        var range = await view.PrepareRenameAsync(2, 4);
        var edit = await view.RequestRenameAsync(2, 4, "Renamed");

        Assert.Equal(2, range!.Start.Line);
        Assert.True(called);
        Assert.NotNull(edit);
        Assert.Single(edit!.Changes);
    }

    [Fact]
    public async Task NullLspView_uses_host_definition_and_references_providers()
    {
        var view = new NullLspView
        {
            HostDefinitionProvider = (_, _, _) =>
                Task.FromResult<(string Uri, int Line, int Column)?>(
                    (LspUri.FromPath("C:\\work\\Definition.cs"), 4, 2)),
            HostReferencesProvider = (_, _, _) =>
                Task.FromResult<IReadOnlyList<LspLocation>>([
                    new LspLocation("file:///C:/work/Definition.cs",
                        new LspRange(new(0, 0), new(0, 4))),
                ]),
            HostImplementationProvider = (_, _, _) =>
                Task.FromResult<IReadOnlyList<LspLocation>>([
                    new LspLocation("file:///C:/work/Implementation.cs",
                        new LspRange(new(1, 0), new(1, 4))),
                ]),
            HostTypeDefinitionProvider = (_, _, _) =>
                Task.FromResult<IReadOnlyList<LspLocation>>([
                    new LspLocation("file:///C:/work/Type.cs",
                        new LspRange(new(2, 0), new(2, 4))),
                ]),
            HostDeclarationProvider = (_, _, _) =>
                Task.FromResult<IReadOnlyList<LspLocation>>([
                    new LspLocation("file:///C:/work/Declaration.cs",
                        new LspRange(new(3, 0), new(3, 4))),
                ]),
            HostHoverProvider = (_, _, _) =>
                Task.FromResult<string?>("Service.Read()\n値を返すサービス。"),
            HostCompletionProvider = (_, _, _) =>
                Task.FromResult<IReadOnlyList<LspCompletionItem>>([
                    new LspCompletionItem("Read", CompletionItemKind.Method,
                        InsertText: "Read",
                        TextEdit: new LspTextEdit(
                            new LspRange(new(0, 8), new(0, 10)), "Read")),
                ]),
        };

        var location = await view.RequestDefinitionLocationAsync(1, 2);
        var local = await view.RequestDefinitionAsync(1, 2);
        var references = await view.RequestReferencesAsync(1, 2);
        var implementations = await view.RequestImplementationAsync(1, 2);
        var typeDefinition = await view.RequestTypeDefinitionAsync(1, 2);
        var declaration = await view.RequestDeclarationAsync(1, 2);

        Assert.True(view.HasHostDefinitionProvider);
        Assert.True(view.HasHostReferencesProvider);
        Assert.Equal(LspUri.FromPath("C:\\work\\Definition.cs"), location!.Value.Uri);
        Assert.EndsWith("Definition.cs", local!.Value.FilePath, StringComparison.OrdinalIgnoreCase);
        Assert.Single(references);
        Assert.True(view.HasHostImplementationProvider);
        Assert.True(view.HasHostTypeDefinitionProvider);
        Assert.True(view.HasHostDeclarationProvider);
        Assert.Equal("file:///C:/work/Implementation.cs", implementations[0].Uri);
        Assert.Equal("file:///C:/work/Type.cs", typeDefinition[0].Uri);
        Assert.Equal("file:///C:/work/Declaration.cs", declaration[0].Uri);
        Assert.True(view.HasHostHoverProvider);
        Assert.Equal("Service.Read()\n値を返すサービス。", await view.RequestHoverAsync(1, 2));
        Assert.True(view.HasHostCompletionProvider);
        Assert.Equal("", await view.RequestCompletionAsync(1, 2));
        Assert.Equal("Read", view.GetSelectedCompletion()!.Label);
    }

    [Fact]
    public async Task NullLspView_uses_host_document_highlight_provider()
    {
        IReadOnlyList<DocumentHighlight>? received = null;
        var view = new NullLspView
        {
            HostDocumentHighlightProvider = (line, character, _) =>
            {
                Assert.Equal(3, line);
                Assert.Equal(5, character);
                return Task.FromResult<IReadOnlyList<DocumentHighlight>>([
                    new DocumentHighlight(
                        new LspRange(new(3, 4), new(3, 10)),
                        DocumentHighlightKind.Read),
                ]);
            },
        };
        view.DocumentHighlightsChanged += highlights => received = highlights;

        Assert.True(view.HasHostDocumentHighlightProvider);
        await view.RequestDocumentHighlightAsync("file:///sample.cs", 3, 5);

        var highlight = Assert.Single(received!);
        Assert.Equal(DocumentHighlightKind.Read, highlight.Kind);
        Assert.Equal(3, highlight.Range.Start.Line);
    }

    [Fact]
    public async Task NullLspView_uses_host_semantic_tokens_when_enabled()
    {
        SemanticToken[]? received = null;
        var view = new NullLspView
        {
            HostSemanticTokensProvider = _ => Task.FromResult<IReadOnlyList<SemanticToken>>([
                new SemanticToken(1, 2, 6, "class", []),
            ]),
        };
        view.SemanticTokensChanged += tokens => received = tokens;

        view.SetSemanticTokensEnabled(true);
        view.RequestSemanticTokens();
        for (var i = 0; received is null && i < 20; i++)
            await Task.Delay(10);

        var token = Assert.Single(received!);
        Assert.Equal(1, token.Line);
        Assert.Equal(2, token.StartChar);
        Assert.Equal("class", token.TokenType);
    }
}
