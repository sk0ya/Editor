using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Automation.Text;
using Editor.Core.Models;

namespace Editor.Controls.Rendering;

/// <summary>UI Automation text provider for the custom-drawn editor surface.</summary>
internal sealed class EditorCanvasAutomationPeer(EditorCanvas owner)
    : FrameworkElementAutomationPeer(owner), ITextProvider
{
    private EditorCanvas Canvas => (EditorCanvas)Owner;

    protected override string GetClassNameCore() => nameof(EditorCanvas);
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Document;
    protected override string GetNameCore() => string.IsNullOrWhiteSpace(base.GetNameCore()) ? "Text editor" : base.GetNameCore();
    protected override bool IsContentElementCore() => true;
    protected override bool IsControlElementCore() => true;

    public ITextRangeProvider DocumentRange => Range(0, Canvas.AutomationText.Length);
    public SupportedTextSelection SupportedTextSelection => SupportedTextSelection.Single;

    public ITextRangeProvider[] GetSelection()
    {
        var (start, end) = Canvas.AutomationSelectionOffsets;
        return [Range(start, end)];
    }

    public ITextRangeProvider[] GetVisibleRanges()
    {
        var (start, end) = Canvas.AutomationVisibleOffsets;
        return [Range(start, end)];
    }

    public ITextRangeProvider? RangeFromChild(IRawElementProviderSimple childElement) => null;

    public ITextRangeProvider RangeFromPoint(System.Windows.Point screenLocation)
    {
        Point local = Canvas.PointFromScreen(screenLocation);
        return Range(Canvas.AutomationOffsetFromPoint(local), Canvas.AutomationOffsetFromPoint(local));
    }

    private EditorTextRangeProvider Range(int start, int end) => new(this, start, end);
    internal IRawElementProviderSimple RawProvider => ProviderFromPeer(this);

    internal static void NotifyTextChanged(EditorCanvas canvas)
    {
        if (FromElement(canvas) is EditorCanvasAutomationPeer peer && ListenerExists(AutomationEvents.TextPatternOnTextChanged))
            peer.RaiseAutomationEvent(AutomationEvents.TextPatternOnTextChanged);
    }

    internal static void NotifySelectionChanged(EditorCanvas canvas)
    {
        if (FromElement(canvas) is EditorCanvasAutomationPeer peer && ListenerExists(AutomationEvents.TextPatternOnTextSelectionChanged))
            peer.RaiseAutomationEvent(AutomationEvents.TextPatternOnTextSelectionChanged);
    }
}

internal sealed class EditorTextRangeProvider(EditorCanvasAutomationPeer peer, int start, int end) : ITextRangeProvider
{
    private EditorCanvas Canvas => (EditorCanvas)peer.Owner;
    private int Start { get; set; } = start;
    private int End { get; set; } = end;
    private int Length => Canvas.AutomationText.Length;
    private void Clamp() { Start = Math.Clamp(Start, 0, Length); End = Math.Clamp(End, Start, Length); }

    public ITextRangeProvider Clone() => new EditorTextRangeProvider(peer, Start, End);
    public bool Compare(ITextRangeProvider range) => range is EditorTextRangeProvider r && r.Canvas == Canvas && r.Start == Start && r.End == End;
    public int CompareEndpoints(TextPatternRangeEndpoint endpoint, ITextRangeProvider targetRange, TextPatternRangeEndpoint targetEndpoint)
    {
        var r = RequireRange(targetRange);
        return Endpoint(endpoint).CompareTo(r.Endpoint(targetEndpoint));
    }
    public void ExpandToEnclosingUnit(TextUnit unit)
    {
        Clamp(); string text = Canvas.AutomationText;
        if (unit == TextUnit.Document) { Start = 0; End = text.Length; return; }
        if (unit is TextUnit.Line or TextUnit.Paragraph) { Start = LineStart(text, Start); End = LineEnd(text, Start); return; }
        if (unit == TextUnit.Word) { Start = WordStart(text, Start); End = WordEnd(text, Start); return; }
        if (Start < text.Length) End = Start + 1;
    }
    public ITextRangeProvider? FindAttribute(int attributeId, object value, bool backward) => null;
    public ITextRangeProvider? FindText(string text, bool backward, bool ignoreCase)
    {
        Clamp(); var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string source = Canvas.AutomationText;
        int found = backward ? source.LastIndexOf(text, Math.Max(Start, End - 1), Math.Max(0, End - Start), comparison) : source.IndexOf(text, Start, End - Start, comparison);
        return found < 0 ? null : new EditorTextRangeProvider(peer, found, found + text.Length);
    }
    public object GetAttributeValue(int attributeId) => AutomationElement.NotSupported;
    public double[] GetBoundingRectangles() => Canvas.AutomationBoundingRectangles(Start, End);
    public IRawElementProviderSimple[] GetChildren() => [];
    public IRawElementProviderSimple GetEnclosingElement() => peer.RawProvider;
    public string GetText(int maxLength) { Clamp(); string value = Canvas.AutomationText[Start..End]; return maxLength < 0 ? value : value[..Math.Min(value.Length, maxLength)]; }
    public int Move(TextUnit unit, int count) { int moved = MoveEndpointByUnit(TextPatternRangeEndpoint.Start, unit, count); End = Start; if (moved != 0) ExpandToEnclosingUnit(unit); return moved; }
    public void MoveEndpointByRange(TextPatternRangeEndpoint endpoint, ITextRangeProvider targetRange, TextPatternRangeEndpoint targetEndpoint)
    { int value = RequireRange(targetRange).Endpoint(targetEndpoint); SetEndpoint(endpoint, value); }
    public int MoveEndpointByUnit(TextPatternRangeEndpoint endpoint, TextUnit unit, int count)
    {
        int value = Endpoint(endpoint), moved = 0; string text = Canvas.AutomationText;
        for (int i = 0; i < Math.Abs(count); i++) { int next = MoveOne(text, value, unit, Math.Sign(count)); if (next == value) break; value = next; moved += Math.Sign(count); }
        SetEndpoint(endpoint, value); return moved;
    }
    public void RemoveFromSelection() { var (s, e) = Canvas.AutomationSelectionOffsets; if (s == Start && e == End) Canvas.SetSelection(null); }
    public void ScrollIntoView(bool alignToTop) { Canvas.AutomationScrollToOffset(Start); }
    public void Select() { var (s, e) = (Canvas.AutomationPosition(Start), Canvas.AutomationPosition(End)); Canvas.SetSelection(s == e ? null : new Selection(s, e, SelectionType.Character)); Canvas.SetCursor(e); }
    public void AddToSelection() => throw new InvalidOperationException("The editor supports a single selection.");
    private int Endpoint(TextPatternRangeEndpoint e) => e == TextPatternRangeEndpoint.Start ? Start : End;
    private void SetEndpoint(TextPatternRangeEndpoint e, int value) { value = Math.Clamp(value, 0, Length); if (e == TextPatternRangeEndpoint.Start) { Start = value; if (Start > End) End = Start; } else { End = value; if (End < Start) Start = End; } }
    private EditorTextRangeProvider RequireRange(ITextRangeProvider range) => range is EditorTextRangeProvider r && ReferenceEquals(r.Canvas, Canvas) ? r : throw new ArgumentException("Range belongs to another text provider.", nameof(range));
    private static int LineStart(string s, int p) { p = Math.Min(p, s.Length); while (p > 0 && s[p - 1] != '\n') p--; return p; }
    private static int LineEnd(string s, int p) { int n = s.IndexOf('\n', p); return n < 0 ? s.Length : n + 1; }
    private static int WordStart(string s, int p) { while (p > 0 && char.IsLetterOrDigit(s[p - 1])) p--; return p; }
    private static int WordEnd(string s, int p) { while (p < s.Length && char.IsLetterOrDigit(s[p])) p++; return p; }
    private static int MoveOne(string s, int p, TextUnit unit, int d) => unit switch { TextUnit.Document => d > 0 ? s.Length : 0, TextUnit.Line or TextUnit.Paragraph => d > 0 ? LineEnd(s, p) : LineStart(s, Math.Max(0, p - 1)), TextUnit.Word => d > 0 ? WordEnd(s, Math.Min(s.Length, p + 1)) : WordStart(s, Math.Max(0, p - 1)), _ => Math.Clamp(p + d, 0, s.Length) };
}

public partial class EditorCanvas
{
    internal string AutomationText => string.Join("\n", _lines);
    internal (int Start, int End) AutomationSelectionOffsets { get { if (_selection is not { } s) { int c = AutomationOffset(_cursor); return (c, c); } return (AutomationOffset(s.NormalizedStart), AutomationOffset(s.NormalizedEnd)); } }
    internal (int Start, int End) AutomationVisibleOffsets { get { var (first, last) = GetVisibleBufferLineRange(); return (AutomationOffset(new(first, 0)), AutomationOffset(new(last, _lines[Math.Clamp(last, 0, _lines.Length - 1)].Length))); } }
    internal int AutomationOffset(CursorPosition p) { int line = Math.Clamp(p.Line, 0, _lines.Length - 1), offset = 0; for (int i = 0; i < line; i++) offset += _lines[i].Length + 1; return offset + Math.Clamp(p.Column, 0, _lines[line].Length); }
    internal CursorPosition AutomationPosition(int offset) { offset = Math.Clamp(offset, 0, AutomationText.Length); for (int i = 0; i < _lines.Length; i++) { if (offset <= _lines[i].Length) return new(i, offset); offset -= _lines[i].Length + 1; } return new(_lines.Length - 1, _lines[^1].Length); }
    internal int AutomationOffsetFromPoint(Point point) { var (line, col) = HitTest(point); return AutomationOffset(new(line, col)); }
    internal void AutomationScrollToOffset(int offset) { var p = AutomationPosition(offset); int visual = Array.FindIndex(_visualLines, v => v.BufferLine == p.Line); if (visual >= 0) { _scrollOffsetY = Math.Clamp(visual * _lineHeight, 0, MaxScrollOffsetY); InvalidateVisual(); ScrollChanged?.Invoke(_scrollOffsetX, _scrollOffsetY); } }
    internal double[] AutomationBoundingRectangles(int start, int end)
    {
        var a = AutomationPosition(start); var b = AutomationPosition(end); var result = new List<double>();
        for (int line = a.Line; line <= b.Line; line++)
        {
            int sc = line == a.Line ? a.Column : 0, ec = line == b.Line ? b.Column : _lines[line].Length;
            int visual = Array.FindIndex(_visualLines, v => v.BufferLine == line); if (visual < 0) continue;
            var (_, _, _, gutter) = GetGutterMetrics(); Point local = new(gutter + sc * _charWidth - _scrollOffsetX, visual * _lineHeight - _scrollOffsetY);
            Point screen = PresentationSource.FromVisual(this) is null ? local : PointToScreen(local);
            result.Add(screen.X); result.Add(screen.Y); result.Add(Math.Max(_charWidth, (ec - sc) * _charWidth)); result.Add(_lineHeight);
        }
        return [.. result];
    }
}
