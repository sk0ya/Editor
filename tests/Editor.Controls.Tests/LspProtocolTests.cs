using System.Text.Json;
using Editor.Controls.Lsp;
using Editor.Core.Lsp;

namespace Editor.Controls.Tests;

public sealed class LspProtocolTests
{
    [Fact]
    public void Completion_trigger_characters_are_read_from_server_capabilities()
    {
        using var json = JsonDocument.Parse("""{"completionProvider":{"triggerCharacters":[".",":","."]}}""");
        Assert.Equal([".", ":"], LspClient.ParseCompletionTriggerCharacters(json.RootElement));
    }

    [Fact]
    public void Completion_parser_preserves_ranking_edit_snippet_and_commit_metadata()
    {
        using var json = JsonDocument.Parse("""
        [{"label":"WriteLine","kind":2,"sortText":"001","filterText":"write","preselect":true,
          "insertTextFormat":2,"deprecated":true,"commitCharacters":[".","("],
          "textEdit":{"range":{"start":{"line":2,"character":3},"end":{"line":2,"character":5}},
                      "newText":"WriteLine(${1:value})$0"}}]
        """);
        var item = Assert.Single(LspClient.ParseCompletionResult(json.RootElement));
        Assert.Equal("001", item.SortText);
        Assert.True(item.Preselect);
        Assert.True(item.Deprecated);
        Assert.Equal(InsertTextFormat.Snippet, item.TextFormat);
        Assert.Equal([".", "("], item.CommitCharacters);
        Assert.Equal(new LspPosition(2, 3), item.TextEdit!.Range.Start);
        Assert.Equal("WriteLine(${1:value})$0", item.TextEdit.NewText);
    }

    [Fact]
    public void Initialize_workspace_folder_contains_root_uri_and_directory_name()
    {
        var folders = LspClient.CreateWorkspaceFolders("file:///C:/Projects/Loomo");
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(folders));
        var folder = json.RootElement[0];

        Assert.Equal("file:///C:/Projects/Loomo", folder.GetProperty("uri").GetString());
        Assert.Equal("Loomo", folder.GetProperty("name").GetString());
    }

    [Fact]
    public void Initialize_workspace_folders_use_all_host_provided_roots()
    {
        var folders = LspClient.CreateWorkspaceFolders(
            "file:///C:/Projects/Loomo",
            [@"C:\Projects\Loomo", @"C:\Projects\Editor"]);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(folders));

        Assert.Equal(2, json.RootElement.GetArrayLength());
        Assert.Equal("file:///C:/Projects/Loomo",
            json.RootElement[0].GetProperty("uri").GetString());
        Assert.Equal("Loomo", json.RootElement[0].GetProperty("name").GetString());
        Assert.Equal("file:///C:/Projects/Editor",
            json.RootElement[1].GetProperty("uri").GetString());
        Assert.Equal("Editor", json.RootElement[1].GetProperty("name").GetString());
    }

    [Fact]
    public void Workspace_configuration_returns_one_value_for_each_requested_item()
    {
        using var json = JsonDocument.Parse(
            """{"items":[{"section":"a"},{"section":"b"},{"section":"c"}]}""");

        var result = LspProcess.CreateServerRequestResult(
            "workspace/configuration", json.RootElement);
        var values = Assert.IsType<object?[]>(result);

        Assert.Equal(3, values.Length);
        Assert.All(values, Assert.Null);
    }

    [Fact]
    public void Workspace_folders_request_echoes_the_folders_sent_at_initialize()
    {
        var folders = LspClient.CreateWorkspaceFolders("file:///C:/Projects/Loomo");
        using var json = JsonDocument.Parse("{}");

        // 応答は object? 宣言のプロパティに載って送られるので、その経路で
        // 仕様どおりの小文字プロパティ名になることまで確認する。
        object? result = LspProcess.CreateServerRequestResult(
            "workspace/workspaceFolders", json.RootElement, folders);

        Assert.Same(folders, result);

        using var response = JsonDocument.Parse(JsonSerializer.Serialize(new { result }));
        var folder = response.RootElement.GetProperty("result")[0];
        Assert.Equal("file:///C:/Projects/Loomo", folder.GetProperty("uri").GetString());
        Assert.Equal("Loomo", folder.GetProperty("name").GetString());
    }

    [Fact]
    public void Apply_edit_request_answers_with_the_required_applied_flag()
    {
        using var json = JsonDocument.Parse("""{"edit":{"changes":{}}}""");

        var result = LspProcess.CreateServerRequestResult(
            "workspace/applyEdit", json.RootElement);

        using var response = JsonDocument.Parse(JsonSerializer.Serialize(result));
        Assert.False(response.RootElement.GetProperty("applied").GetBoolean());
    }

    [Fact]
    public void Workspace_edit_preserves_document_version_for_stale_edit_detection()
    {
        using var json = JsonDocument.Parse("""
            {"documentChanges":[{"textDocument":{"uri":"file:///C:/work/a.cs","version":7},
              "edits":[{"range":{"start":{"line":0,"character":0},"end":{"line":0,"character":1}},"newText":"A"}]}]}
            """);

        var edit = Assert.IsType<Editor.Core.Lsp.LspWorkspaceEdit>(
            LspClient.ParseWorkspaceEdit(json.RootElement));

        Assert.Equal(7, edit.DocumentVersions!["file:///C:/work/a.cs"]);
        Assert.Single(edit.Changes["file:///C:/work/a.cs"]);
    }

    // tsserver 系は "file:///c%3A/…" を返す。素の文字列のままキーにすると、
    // 編集対象がどのファイルなのかホスト側で解決できなくなる（rename が動かない原因）。
    [Fact]
    public void Workspace_edit_changes_keys_are_normalized_to_a_usable_file_uri()
    {
        using var json = JsonDocument.Parse("""
            {"changes":{"file:///c%3A/work/a.ts":
              [{"range":{"start":{"line":0,"character":16},"end":{"line":0,"character":25}},"newText":"greetPerson"}]}}
            """);

        var edit = Assert.IsType<LspWorkspaceEdit>(LspClient.ParseWorkspaceEdit(json.RootElement));

        var key = Assert.Single(edit.Changes.Keys);
        Assert.Equal(@"c:\work\a.ts", LspUri.TryToLocalPath(key));
        Assert.True(LspUri.MatchesPath(key, @"C:\work\a.ts"));
    }

    [Fact]
    public void Workspace_edit_document_changes_keys_are_normalized_too()
    {
        using var json = JsonDocument.Parse("""
            {"documentChanges":[{"textDocument":{"uri":"file:///c%3A/work/a.ts","version":3},
              "edits":[{"range":{"start":{"line":1,"character":0},"end":{"line":1,"character":1}},"newText":"X"}]}]}
            """);

        var edit = Assert.IsType<LspWorkspaceEdit>(LspClient.ParseWorkspaceEdit(json.RootElement));

        var key = Assert.Single(edit.Changes.Keys);
        Assert.True(LspUri.MatchesPath(key, @"C:\work\a.ts"));
        // ホストは同じキーで版を引く。両辞書のキーが揃っていないと版検証が黙って抜ける。
        Assert.Equal(3, edit.DocumentVersions![key]);
    }

    [Fact]
    public void Workspace_edit_keys_survive_a_drive_letter_case_difference()
    {
        using var json = JsonDocument.Parse("""
            {"changes":{"file:///c:/work/a.ts":
              [{"range":{"start":{"line":0,"character":0},"end":{"line":0,"character":1}},"newText":"X"}]}}
            """);

        var edit = Assert.IsType<LspWorkspaceEdit>(LspClient.ParseWorkspaceEdit(json.RootElement));

        Assert.True(edit.Changes.ContainsKey("file:///C:/work/a.ts"));
    }

    [Fact]
    public void Other_server_requests_keep_the_default_null_response()
    {
        using var json = JsonDocument.Parse("""{"registrations":[]}""");

        Assert.Null(LspProcess.CreateServerRequestResult(
            "client/registerCapability", json.RootElement));
    }

    [Fact]
    public void Hierarchy_followup_preserves_server_data()
    {
        using var source = JsonDocument.Parse("""{"textDocument":{"uri":"file:///a.cs"},"position":{"line":4,"character":2}}""");
        var range = new Editor.Core.Lsp.LspRange(
            new Editor.Core.Lsp.LspPosition(4, 0),
            new Editor.Core.Lsp.LspPosition(8, 1));

        var item = LspClient.SerializeHierarchyItem(
            "Run", 6, "file:///a.cs", range, range,
            source.RootElement.Clone());
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(item));

        var data = json.RootElement.GetProperty("data");
        Assert.Equal("file:///a.cs",
            data.GetProperty("textDocument").GetProperty("uri").GetString());
        Assert.Equal(4, data.GetProperty("position").GetProperty("line").GetInt32());
    }

    [Fact]
    public void Incremental_sync_replaces_the_previous_whole_document_with_a_valid_range()
    {
        var change = LspClient.CreateContentChange(2, "first\r\nsecond", "updated");
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(change));

        var range = json.RootElement.GetProperty("range");
        Assert.Equal(0, range.GetProperty("start").GetProperty("line").GetInt32());
        Assert.Equal(0, range.GetProperty("start").GetProperty("character").GetInt32());
        Assert.Equal(1, range.GetProperty("end").GetProperty("line").GetInt32());
        Assert.Equal(6, range.GetProperty("end").GetProperty("character").GetInt32());
        Assert.Equal("first\r\nsecond".Length, json.RootElement.GetProperty("rangeLength").GetInt32());
        Assert.Equal("updated", json.RootElement.GetProperty("text").GetString());
    }

    [Fact]
    public void Full_sync_keeps_the_range_less_change_shape()
    {
        var change = LspClient.CreateContentChange(1, "before", "after");
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(change));

        Assert.False(json.RootElement.TryGetProperty("range", out _));
        Assert.Equal("after", json.RootElement.GetProperty("text").GetString());
    }

    [Theory]
    [InlineData("""{"textDocumentSync":2}""", 2)]
    [InlineData("""{"textDocumentSync":{"change":2}}""", 2)]
    [InlineData("""{}""", 1)]
    public void Initialize_reads_the_server_text_document_sync_kind(string jsonText, int expected)
    {
        using var json = JsonDocument.Parse(jsonText);
        Assert.Equal(expected, LspClient.ParseTextDocumentSyncKind(json.RootElement));
    }

    [Fact]
    public void Code_action_provider_capability_yields_kinds_and_resolve_support()
    {
        using var json = JsonDocument.Parse("""
        {"codeActionProvider":{"codeActionKinds":["quickfix","refactor.extract"],"resolveProvider":true}}
        """);
        var (kinds, resolve) = LspClient.ParseCodeActionProvider(json.RootElement);

        Assert.Equal(["quickfix", "refactor.extract"], kinds);
        Assert.True(resolve);
    }

    [Fact]
    public void Code_action_provider_reported_as_bare_true_declares_no_kinds()
    {
        using var json = JsonDocument.Parse("""{"codeActionProvider":true}""");
        var (kinds, resolve) = LspClient.ParseCodeActionProvider(json.RootElement);

        Assert.Empty(kinds);
        Assert.False(resolve);
    }

    /// <summary>Roslyn 系は一覧では title/kind/data だけを返し、edit は codeAction/resolve で作る。
    /// data を落とすと解決要求が組み立てられず「適用しても何も起きない」になる。</summary>
    [Fact]
    public void Unresolved_code_action_keeps_its_raw_json_and_is_marked_for_resolve()
    {
        using var json = JsonDocument.Parse("""
        {"title":"メソッドの抽出","kind":"refactor.extract","data":{"handle":17}}
        """);
        var action = LspClient.ParseCodeAction(json.RootElement)!;

        Assert.Equal("メソッドの抽出", action.Title);
        Assert.Equal("refactor.extract", action.Kind);
        Assert.Null(action.Edit);
        Assert.True(action.NeedsResolve);
        Assert.Contains("\"handle\":17", action.RawJson!.Replace(" ", ""));
    }

    [Fact]
    public void Code_action_literal_reads_the_nested_command_object()
    {
        using var json = JsonDocument.Parse("""
        {"title":"関数へ抽出","kind":"refactor.extract",
         "command":{"title":"適用","command":"_typescript.applyRefactoring","arguments":[{"file":"a.ts"},3]}}
        """);
        var action = LspClient.ParseCodeAction(json.RootElement)!;

        Assert.Equal("_typescript.applyRefactoring", action.Command!.Command);
        Assert.Equal(["""{"file":"a.ts"}""", "3"], action.Command.ArgumentsJson!.Select(a => a.Replace(" ", "")));
    }

    /// <summary>旧仕様の Command（command が文字列で title と同階層）を CodeAction リテラルと取り違えない。</summary>
    [Fact]
    public void Legacy_command_shaped_action_is_read_from_the_item_itself()
    {
        using var json = JsonDocument.Parse("""
        {"title":"インポートの整理","command":"editor.organizeImports","arguments":["file:///a.ts"]}
        """);
        var action = LspClient.ParseCodeAction(json.RootElement)!;

        Assert.Equal("editor.organizeImports", action.Command!.Command);
        Assert.Equal(["\"file:///a.ts\""], action.Command.ArgumentsJson!);
    }

    [Fact]
    public void Disabled_code_action_carries_its_reason()
    {
        using var json = JsonDocument.Parse("""
        {"title":"インライン化","kind":"refactor.inline","disabled":{"reason":"選択が式ではありません"}}
        """);
        var action = LspClient.ParseCodeAction(json.RootElement)!;

        Assert.Equal("選択が式ではありません", action.DisabledReason);
    }

    /// <summary>「クラスに抽出」はファイル作成と本文編集がひと組で来る。作成を落とすと
    /// 新しいファイルへの編集だけが宙に浮く（＝適用しても何も起きない）。</summary>
    [Fact]
    public void Workspace_edit_keeps_create_operations_alongside_text_edits()
    {
        using var json = JsonDocument.Parse("""
        {"documentChanges":[
          {"kind":"create","uri":"file:///c%3A/p/New.cs","options":{"ignoreIfExists":true}},
          {"textDocument":{"uri":"file:///c%3A/p/New.cs","version":null},
           "edits":[{"range":{"start":{"line":0,"character":0},"end":{"line":0,"character":0}},
                     "newText":"class New {}"}]}]}
        """);
        var edit = LspClient.ParseWorkspaceEdit(json.RootElement)!;

        var operation = Assert.Single(edit.FileOperations!);
        Assert.Equal(LspFileOperationKind.Create, operation.Kind);
        Assert.True(operation.IgnoreIfExists);
        // URI は %3A のまま扱わない（LspUri 正規化。§30.14）。
        Assert.Equal("file:///c:/p/New.cs", operation.Uri);
        Assert.Single(edit.Changes);
    }

    [Fact]
    public void Workspace_edit_reads_rename_and_delete_operations()
    {
        using var json = JsonDocument.Parse("""
        {"documentChanges":[
          {"kind":"rename","oldUri":"file:///c%3A/p/A.cs","newUri":"file:///c%3A/p/B.cs"},
          {"kind":"delete","uri":"file:///c%3A/p/C.cs","options":{"recursive":true}}]}
        """);
        var edit = LspClient.ParseWorkspaceEdit(json.RootElement)!;

        Assert.Equal(2, edit.FileOperations!.Count);
        Assert.Equal("file:///c:/p/B.cs", edit.FileOperations[0].NewUri);
        Assert.Equal(LspFileOperationKind.Delete, edit.FileOperations[1].Kind);
        Assert.True(edit.FileOperations[1].Recursive);
        Assert.Empty(edit.Changes);
    }

    [Theory]
    [InlineData("refactor.extract.function", "refactor", true)]
    [InlineData("refactor.extract.function", "refactor.extract", true)]
    [InlineData("refactor.extract", "refactor.extract", true)]
    [InlineData("refactoring", "refactor", false)]
    [InlineData("quickfix", "refactor", false)]
    [InlineData(null, "refactor", false)]
    public void Code_action_kinds_match_by_dotted_hierarchy(string? kind, string prefix, bool expected)
        => Assert.Equal(expected, LspCodeActionKinds.Matches(kind, prefix));
}
