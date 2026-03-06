using Editor.Core.Engine;

namespace Editor.Core.Models;

public enum VimEventType
{
    ModeChanged,
    TextChanged,
    CursorMoved,
    SelectionChanged,
    SaveRequested,
    QuitRequested,
    OpenFileRequested,
    NewTabRequested,
    SplitRequested,
    NextTabRequested,
    PrevTabRequested,
    CloseTabRequested,
    ViewportAlignRequested,
    StatusMessage,
    SearchResultChanged,
    CommandLineChanged,
    GoToDefinitionRequested,
    FormatDocumentRequested,
    QuickfixOpenRequested,
    QuickfixCloseRequested,
    QuickfixNextRequested,
    QuickfixPrevRequested,
    QuickfixGotoRequested,
    FoldsChanged,
    OptionsChanged,
    GrepRequested,
    GitBlameRequested,
    GitDiffRequested,
    GitLogRequested,
    WindowNavRequested,
    WindowCloseRequested,
    LspHoverRequested,
    FindReferencesRequested,
    LspRenameRequested,
    CodeActionRequested,
    CommandCompletionChanged,
    OpenUrlRequested,
    MkSessionRequested,
    SourceRequested,
    TerminalRequested,
    ReloadFileRequested,
    SymbolsRequested,
    GitCommitRequested,
    HunkNavigateRequested,
    CallHierarchyRequested,
    TypeHierarchyRequested,
}

public enum ViewportAlign
{
    Top,
    Center,
    Bottom
}

public enum WindowNavDir { Next, Prev, Left, Right, Up, Down }

public record VimEvent(VimEventType Type)
{
    public static VimEvent ModeChanged(VimMode mode) =>
        new ModeChangedEvent(mode);

    public static VimEvent TextChanged() =>
        new(VimEventType.TextChanged);

    public static VimEvent CursorMoved(CursorPosition pos) =>
        new CursorMovedEvent(pos);

    public static VimEvent SelectionChanged(Selection? sel) =>
        new SelectionChangedEvent(sel);

    public static VimEvent SaveRequested(string? path) =>
        new SaveRequestedEvent(path);

    public static VimEvent QuitRequested(bool force) =>
        new QuitRequestedEvent(force);

    public static VimEvent OpenFileRequested(string path) =>
        new OpenFileRequestedEvent(path);

    public static VimEvent StatusMessage(string msg) =>
        new StatusMessageEvent(msg);

    public static VimEvent CommandLineChanged(string text) =>
        new CommandLineChangedEvent(text);

    public static VimEvent SearchChanged(string pattern, int matchCount) =>
        new SearchResultChangedEvent(pattern, matchCount);

    public static VimEvent NewTabRequested(string? path) =>
        new NewTabRequestedEvent(path);

    public static VimEvent SplitRequested(bool vertical, string? filePath = null) =>
        new SplitRequestedEvent(vertical, filePath);

    public static VimEvent WindowNavRequested(WindowNavDir dir) =>
        new WindowNavRequestedEvent(dir);

    public static VimEvent WindowCloseRequested(bool force) =>
        new WindowCloseRequestedEvent(force);

    public static VimEvent NextTabRequested() =>
        new NextTabRequestedEvent();

    public static VimEvent PrevTabRequested() =>
        new PrevTabRequestedEvent();

    public static VimEvent CloseTabRequested(bool force) =>
        new CloseTabRequestedEvent(force);

    public static VimEvent ViewportAlignRequested(ViewportAlign align) =>
        new ViewportAlignRequestedEvent(align);

    public static VimEvent GoToDefinitionRequested() =>
        new(VimEventType.GoToDefinitionRequested);

    public static VimEvent FormatDocumentRequested() =>
        new(VimEventType.FormatDocumentRequested);

    public static VimEvent QuickfixOpen() =>
        new(VimEventType.QuickfixOpenRequested);

    public static VimEvent QuickfixClose() =>
        new(VimEventType.QuickfixCloseRequested);

    public static VimEvent QuickfixNext(int count = 1) =>
        new QuickfixNextEvent(count);

    public static VimEvent QuickfixPrev(int count = 1) =>
        new QuickfixPrevEvent(count);

    public static VimEvent QuickfixGoto(int index) =>
        new QuickfixGotoEvent(index);

    public static VimEvent FoldsChanged() =>
        new(VimEventType.FoldsChanged);

    public static VimEvent OptionsChanged() =>
        new(VimEventType.OptionsChanged);

    public static VimEvent GrepRequested(string pattern, string? fileGlob, bool ignoreCase) =>
        new GrepRequestedEvent(pattern, fileGlob, ignoreCase);

    public static VimEvent GitBlameRequested() =>
        new(VimEventType.GitBlameRequested);

    public static VimEvent GitDiffRequested() =>
        new(VimEventType.GitDiffRequested);

    public static VimEvent GitLogRequested() =>
        new(VimEventType.GitLogRequested);

    public static VimEvent LspHoverRequested() =>
        new(VimEventType.LspHoverRequested);

    public static VimEvent FindReferencesRequested() =>
        new(VimEventType.FindReferencesRequested);

    public static VimEvent LspRenameRequested(string? newName = null) =>
        new LspRenameRequestedEvent(newName);

    public static VimEvent CodeActionRequested() =>
        new(VimEventType.CodeActionRequested);

    public static VimEvent CommandCompletionChanged(string[] items, int selectedIndex) =>
        new CommandCompletionChangedEvent(items, selectedIndex);

    public static VimEvent OpenUrlRequested(string url) =>
        new OpenUrlRequestedEvent(url);

    public static VimEvent MkSessionRequested(string path) =>
        new MkSessionRequestedEvent(path);

    public static VimEvent SourceRequested(string path) =>
        new SourceRequestedEvent(path);

    public static VimEvent TerminalRequested(string? shellCmd = null) =>
        new TerminalRequestedEvent(shellCmd);

    public static VimEvent ReloadFileRequested(bool force) =>
        new ReloadFileRequestedEvent(force);

    public static VimEvent SymbolsRequested(string? query = null) =>
        query is null ? new VimEvent(VimEventType.SymbolsRequested) : new SymbolsRequestedEvent(query);

    public static VimEvent GitCommitRequested() =>
        new(VimEventType.GitCommitRequested);

    public static VimEvent HunkNavigateRequested(bool forward) =>
        new HunkNavigateRequestedEvent(forward);

    public static VimEvent CallHierarchyRequested() =>
        new(VimEventType.CallHierarchyRequested);

    public static VimEvent TypeHierarchyRequested() =>
        new(VimEventType.TypeHierarchyRequested);
}

public record ModeChangedEvent(VimMode Mode) : VimEvent(VimEventType.ModeChanged);
public record CursorMovedEvent(CursorPosition Position) : VimEvent(VimEventType.CursorMoved);
public record SelectionChangedEvent(Selection? Selection) : VimEvent(VimEventType.SelectionChanged);
public record SaveRequestedEvent(string? FilePath) : VimEvent(VimEventType.SaveRequested);
public record QuitRequestedEvent(bool Force) : VimEvent(VimEventType.QuitRequested);
public record OpenFileRequestedEvent(string FilePath) : VimEvent(VimEventType.OpenFileRequested);
public record StatusMessageEvent(string Message) : VimEvent(VimEventType.StatusMessage);
public record CommandLineChangedEvent(string Text) : VimEvent(VimEventType.CommandLineChanged);
public record SearchResultChangedEvent(string Pattern, int MatchCount) : VimEvent(VimEventType.SearchResultChanged);
public record NewTabRequestedEvent(string? FilePath) : VimEvent(VimEventType.NewTabRequested);
public record SplitRequestedEvent(bool Vertical, string? FilePath) : VimEvent(VimEventType.SplitRequested);
public record WindowNavRequestedEvent(WindowNavDir Dir) : VimEvent(VimEventType.WindowNavRequested);
public record WindowCloseRequestedEvent(bool Force) : VimEvent(VimEventType.WindowCloseRequested);
public record NextTabRequestedEvent() : VimEvent(VimEventType.NextTabRequested);
public record PrevTabRequestedEvent() : VimEvent(VimEventType.PrevTabRequested);
public record CloseTabRequestedEvent(bool Force) : VimEvent(VimEventType.CloseTabRequested);
public record ViewportAlignRequestedEvent(ViewportAlign Align) : VimEvent(VimEventType.ViewportAlignRequested);
public record QuickfixNextEvent(int Count) : VimEvent(VimEventType.QuickfixNextRequested);
public record QuickfixPrevEvent(int Count) : VimEvent(VimEventType.QuickfixPrevRequested);
public record QuickfixGotoEvent(int Index) : VimEvent(VimEventType.QuickfixGotoRequested);
public record GrepRequestedEvent(string Pattern, string? FileGlob, bool IgnoreCase) : VimEvent(VimEventType.GrepRequested);
public record LspRenameRequestedEvent(string? NewName) : VimEvent(VimEventType.LspRenameRequested);
public record CommandCompletionChangedEvent(string[] Items, int SelectedIndex) : VimEvent(VimEventType.CommandCompletionChanged);
public record OpenUrlRequestedEvent(string Url) : VimEvent(VimEventType.OpenUrlRequested);
public record MkSessionRequestedEvent(string FilePath) : VimEvent(VimEventType.MkSessionRequested);
public record SourceRequestedEvent(string FilePath) : VimEvent(VimEventType.SourceRequested);
public record TerminalRequestedEvent(string? ShellCmd) : VimEvent(VimEventType.TerminalRequested);
public record ReloadFileRequestedEvent(bool Force) : VimEvent(VimEventType.ReloadFileRequested);
public record HunkNavigateRequestedEvent(bool Forward) : VimEvent(VimEventType.HunkNavigateRequested);
public record SymbolsRequestedEvent(string Query) : VimEvent(VimEventType.SymbolsRequested);
