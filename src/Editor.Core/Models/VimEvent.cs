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
}

public enum ViewportAlign
{
    Top,
    Center,
    Bottom
}

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

    public static VimEvent SplitRequested(bool vertical) =>
        new SplitRequestedEvent(vertical);

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
public record SplitRequestedEvent(bool Vertical) : VimEvent(VimEventType.SplitRequested);
public record NextTabRequestedEvent() : VimEvent(VimEventType.NextTabRequested);
public record PrevTabRequestedEvent() : VimEvent(VimEventType.PrevTabRequested);
public record CloseTabRequestedEvent(bool Force) : VimEvent(VimEventType.CloseTabRequested);
public record ViewportAlignRequestedEvent(ViewportAlign Align) : VimEvent(VimEventType.ViewportAlignRequested);
