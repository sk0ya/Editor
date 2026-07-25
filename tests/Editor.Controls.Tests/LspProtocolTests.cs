using System.Text.Json;
using Editor.Controls.Lsp;

namespace Editor.Controls.Tests;

public sealed class LspProtocolTests
{
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
    public void Other_server_requests_keep_the_default_null_response()
    {
        using var json = JsonDocument.Parse("""{"registrations":[]}""");

        Assert.Null(LspProcess.CreateServerRequestResult(
            "client/registerCapability", json.RootElement));
    }
}
