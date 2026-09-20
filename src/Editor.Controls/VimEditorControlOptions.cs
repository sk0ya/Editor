using Editor.Controls.Git;
using Editor.Core.Config;
using Editor.Core.Lsp;
using Editor.Core.Editing;
using Editor.Core.Registers;
using Editor.Core.Extensibility;
using Editor.Core.Syntax;
using Editor.Core.Engine;

namespace Editor.Controls;

public sealed class VimEditorControlOptions
{
    public Func<VimConfig>? ConfigFactory { get; init; }
    public Func<IClipboardProvider>? ClipboardProviderFactory { get; init; }
    public Func<IEditorGitService>? GitServiceFactory { get; init; }
    /// <summary>
    /// The host's LSP session. Supply it to turn LSP on: the control takes one
    /// <see cref="ILspDocument"/> handle per buffer from it and keeps only view state itself.
    /// When null, LSP is off for this control.
    /// </summary>
    public ILspWorkspace? LspWorkspace { get; init; }

    /// <summary>
    /// Write access to the host's extension→server table, backing the <c>:LspAdd</c>/<c>:LspRemove</c>/
    /// <c>:LspList</c>/<c>:LspReset</c> ex commands. Must be the same table
    /// <see cref="LspWorkspace"/> resolves servers from; when null those commands report that they
    /// are unavailable rather than editing a table nobody reads.
    /// </summary>
    public ILspServerAdmin? LspServerAdmin { get; init; }

    /// <summary>
    /// 入力の先読みをホストから供給する。キャレットの先に薄く出す提案を返し、内蔵の
    /// <see cref="Editor.Core.Completion.BufferLinePredictor"/> の答えを置き換える
    /// （内蔵の提案が先に出るので、遅い供給元を待つあいだ何も出ない時間はできない）。
    /// 出せないときは null を返す。打鍵の途中で何度も呼ばれ、古くなった要求はキャンセルされる。
    /// </summary>
    public Func<Editor.Core.Completion.InlineSuggestionContext, CancellationToken,
        Task<Editor.Core.Completion.InlineSuggestion?>>? InlineSuggestionProvider { get; init; }

    /// <summary>Optional host-side semantic completion provider used when LSP has no usable result.</summary>
    public Func<string, string, int, int, CancellationToken,
        Task<IReadOnlyList<LspCompletionItem>>>? HostCompletionProvider { get; init; }

    /// <summary>Optional host-side semantic-token provider used when LSP has no usable result.</summary>
    public Func<string, string, CancellationToken,
        Task<IReadOnlyList<SemanticToken>>>? HostSemanticTokensProvider { get; init; }

    /// <summary>
    /// ホストが右クリックメニューへ自前の「名前の変更」を足すときに true にする。
    /// コントロールはネイティブの "Rename Symbol" 項目を出さなくなるので、同じ操作が2つ並ばない
    /// （ホストがリファクタリング一式を自分のメニューへまとめる場合に使う。Loomo 設計書 §32）。
    /// <c>:Rename</c>・<c>gR</c> などのコマンド経路はこのフラグに関係なく残る。
    /// </summary>
    public bool HostProvidesRenameMenuItem { get; init; }

    /// <summary>
    /// 右クリックメニューのネイティブ項目の見出し。null なら英語の既定
    /// （<see cref="EditorContextMenuLabels.Default"/>）。ホストは <c>ContextMenuBuilding</c> で
    /// 自分の言語の項目を足すので、ここを差し替えないと 1 つのメニューに 2 言語が混ざる。
    /// </summary>
    public EditorContextMenuLabels? ContextMenuLabels { get; init; }

    /// <summary>
    /// Optional host-side semantic rename provider. It is tried when the current LSP server is
    /// unavailable or returns an empty workspace edit, so language-specific hosts can keep the
    /// generic rename dialog and workspace-edit application path.
    /// </summary>
    public Func<string, string, int, int, string, CancellationToken, Task<LspWorkspaceEdit?>>? HostRenameProvider { get; init; }

    /// <summary>Optional host-side rename-range provider used when LSP prepareRename is unavailable.</summary>
    public Func<string, string, int, int, CancellationToken, Task<LspRange?>>? HostPrepareRenameProvider { get; init; }

    /// <summary>Optional host-side semantic definition provider used when LSP has no result.</summary>
    public Func<string, string, int, int, CancellationToken,
        Task<(string Uri, int Line, int Column)?>>? HostDefinitionProvider { get; init; }

    /// <summary>Optional host-side semantic references provider used when LSP has no result.</summary>
    public Func<string, string, int, int, CancellationToken,
        Task<IReadOnlyList<LspLocation>>>? HostReferencesProvider { get; init; }

    /// <summary>Optional host-side semantic implementation provider used when LSP has no result.</summary>
    public Func<string, string, int, int, CancellationToken,
        Task<IReadOnlyList<LspLocation>>>? HostImplementationProvider { get; init; }

    /// <summary>Optional host-side semantic type-definition provider used when LSP has no result.</summary>
    public Func<string, string, int, int, CancellationToken,
        Task<IReadOnlyList<LspLocation>>>? HostTypeDefinitionProvider { get; init; }

    /// <summary>Optional host-side semantic declaration provider used when LSP has no result.</summary>
    public Func<string, string, int, int, CancellationToken,
        Task<IReadOnlyList<LspLocation>>>? HostDeclarationProvider { get; init; }

    /// <summary>Optional host-side semantic hover provider used when LSP has no result.</summary>
    public Func<string, string, int, int, CancellationToken, Task<string?>>? HostHoverProvider { get; init; }

    /// <summary>Optional host-side document-highlight provider used when LSP has no result.</summary>
    public Func<string, string, int, int, CancellationToken,
        Task<IReadOnlyList<DocumentHighlight>>>? HostDocumentHighlightProvider { get; init; }

    /// <summary>
    /// Optional host-side signature-help provider. It is used when the current LSP
    /// server is unavailable or returns no signatures, so a host can supply
    /// language-specific semantic help without teaching the generic editor about
    /// that language.
    /// </summary>
    public Func<string, string, int, int, CancellationToken, Task<LspSignatureHelp?>>? HostSignatureHelpProvider { get; init; }

    /// <summary>
    /// Optional host-side inlay-hint provider. It is used when the current LSP
    /// server is unavailable or returns no hints, allowing language-specific
    /// semantic hints to be supplied by the host.
    /// </summary>
    public Func<string, string, int, int, CancellationToken, Task<IReadOnlyList<InlayHint>>>? HostInlayHintProvider { get; init; }

    /// <summary>
    /// ホバーに出ている診断へ、ホストが<b>補記</b>を添える口（「なぜそう言われたのか」）。
    /// 引数はファイルパス・本文・その診断で、返す1行がメッセージの下に薄く並ぶ。何も言えなければ null。
    ///
    /// <para>言語サーバーの文面は結論だけのことがある——「Using ディレクティブは必要ありません」は
    /// 正しいが、<c>GlobalUsings.cs</c> の <c>global using</c> と重複しているからだ、とはどのサーバーも
    /// 言わない。理由を知っているのは、その言語のプロジェクトを抱えているホストの側なので、
    /// エディタは場所だけ用意して中身は持たない。</para>
    /// </summary>
    public Func<string, string, LspDiagnostic, CancellationToken, Task<string?>>?
        HostDiagnosticExplanationProvider { get; init; }

    /// <summary>
    /// マウスを本文の識別子に乗せたまま止めたとき、型と説明のポップアップを出すか（既定は true）。
    /// 中身は <see cref="HostHoverProvider"/> を含む通常の hover 経路から取るので、LSP もホスト提供も
    /// 無いエディタでは何も出ない（＝出せるときだけ出る）。実行時は
    /// <see cref="VimEditorControl.HoverInfoEnabled"/> で切り替えられる。
    /// </summary>
    public bool HoverInfoEnabled { get; init; } = true;

    /// <summary>ホバーの説明を出すまでの待ち時間（ms、既定 250）。マウスを掃くように動かしている間は
    /// 問い合わせない。
    ///
    /// <para>既定は<b>実測で決めてある</b>。温まった言語サーバーの hover 応答は 3〜55ms、
    /// ポップアップの組み立ては 4〜83ms しかかからないので、以前の 400 では「出るまで」の
    /// 77〜98% がこの待ちだった——遅さの正体は問い合わせではなく待ちの方。
    /// 冷えている初回だけは別物（Roslyn がソリューションを読む 5.5 秒がまるごと乗る）で、
    /// そちらは待ちを削っても効かないので、長引くときは「取得しています」の板を先に出している。</para></summary>
    public int HoverInfoDelayMs { get; init; } = 250;

    public SyntaxLanguageRegistry? SyntaxLanguages { get; init; }
    public EditorCommandRegistry? Commands { get; init; }
    public IServiceProvider? CommandServices { get; init; }
    public VimEngineServices? EngineServices { get; init; }

    /// <summary>
    /// Rules for saving a pasted clipboard image and the Markdown link written in its place
    /// (relative directory + file-name templates). When null the control uses defaults
    /// (<c>images/{filename}-{datetime}.png</c>); the effective instance is exposed and
    /// mutable via <see cref="VimEditorControl.ImagePasteOptions"/>.
    /// </summary>
    public ImagePasteOptions? ImagePasteOptions { get; init; }

    /// <summary>
    /// テスト用の差し替え口。指定すると <see cref="LspWorkspace"/> から作る既定のビューの代わりに
    /// この <see cref="Lsp.IEditorLspView"/> を使う。製品コードのホストは指定しない
    /// （ポップアップ状態の所有はエディタ側にあり、ホストが再実装する場所ではない）。
    /// </summary>
    internal Func<Lsp.IEditorLspView>? LspViewFactory { get; init; }
}
