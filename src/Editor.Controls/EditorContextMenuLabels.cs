namespace Editor.Controls;

/// <summary>
/// 右クリックメニューのネイティブ項目の見出し。既定は英語で、ホストが
/// <see cref="VimEditorControlOptions.ContextMenuLabels"/> から差し替えられる。
/// <para>ホストは <see cref="VimEditorControl.ContextMenuBuilding"/> で自分の言語の項目を足すので、
/// ここが固定の英語だと 1 つのメニューに 2 言語が混ざる（日本語アプリに埋め込んだときに顕著）。
/// 見出しだけを外に出し、キー表記（<c>yy</c> / <c>Ctrl+R</c> 等）は Vim の綴りなので訳さない。</para>
/// </summary>
public sealed record EditorContextMenuLabels
{
    public static EditorContextMenuLabels Default { get; } = new();

    public string CopySelection { get; init; } = "Copy Selection";
    public string CopyLine { get; init; } = "Copy Line";
    public string CutSelection { get; init; } = "Cut Selection";
    public string CutLine { get; init; } = "Cut Line";
    public string Paste { get; init; } = "Paste";
    public string Undo { get; init; } = "Undo";
    public string Redo { get; init; } = "Redo";
    public string SelectAll { get; init; } = "Select All";
    /// <summary>「移動」サブメニューの見出し（定義／実装／型定義／宣言／参照をこの下にまとめる）。</summary>
    public string Navigate { get; init; } = "Go To";
    public string GoToDefinition { get; init; } = "Go to Definition";
    public string GoToImplementation { get; init; } = "Go to Implementation";
    public string GoToTypeDefinition { get; init; } = "Go to Type Definition";
    public string GoToDeclaration { get; init; } = "Go to Declaration";
    public string FindReferences { get; init; } = "Find References";
    public string RenameSymbol { get; init; } = "Rename Symbol";
    public string CodeActions { get; init; } = "Code Actions";
    public string FixAllInFile { get; init; } = "Fix All in File";
    public string HoverInfo { get; init; } = "Hover Info";
    public string FormatDocument { get; init; } = "Format Document";
    public string FormatSelection { get; init; } = "Format Selection";
}
