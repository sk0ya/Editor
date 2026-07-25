using System.Text.Json.Serialization;

namespace Editor.Controls.Lsp;

/// <summary>
/// LSP の <c>WorkspaceFolder</c>。initialize で通知し、サーバーからの
/// <c>workspace/workspaceFolders</c> 要求にも同じ値を返す。
/// </summary>
/// <remarks>
/// このアセンブリは共有の <see cref="System.Text.Json.JsonSerializerOptions"/> を持たず、
/// 既定の命名 (PascalCase) でシリアライズされるため、プロパティ名は
/// <see cref="JsonPropertyNameAttribute"/> で仕様どおりの小文字に固定している。
/// </remarks>
internal sealed record LspWorkspaceFolder(
    [property: JsonPropertyName("uri")] string Uri,
    [property: JsonPropertyName("name")] string Name);
