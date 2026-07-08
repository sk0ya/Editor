using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Editor.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Editor.App;

/// <summary>
/// Owns the Markdown preview pane: splicing/removing the preview from the pane tree,
/// the WebView2-hosted HTML render, and bidirectional scroll sync between the source
/// editor and the preview. Extracted from MainWindow (Phase 7).
///
/// The pane-tree splicing (Show/Close) shares <see cref="PaneNode"/> mutation with
/// window-split management in MainWindow, so the tree root is reached through
/// get/set callbacks rather than owned outright.
/// </summary>
internal sealed class MarkdownPreviewController
{
    private readonly Func<PaneNode?> _getGlobalRoot;
    private readonly Action<PaneNode?> _setGlobalRoot;
    private readonly Func<VimEditorControl?> _currentEditor;
    private readonly Func<VimEditorControl?> _focusedEditor;
    private readonly Func<string> _getMarkdownPreviewStyle;
    private readonly Border _editorContent;
    private readonly Grid _previewPanel;
    private readonly ToggleButton _previewBtn;
    private readonly GridSplitter _previewSplitter;
    private readonly ColumnDefinition _previewSplitterCol;
    private readonly ColumnDefinition _previewCol;
    private readonly WebView2 _previewBrowser;

    private bool _previewVisible;
    private PreviewPaneNode? _previewPaneNode;
    private VimEditorControl? _previewSourceEditor;
    private DispatcherTimer? _previewDebounceTimer;
    private Task? _webView2InitTask;
    private bool _previewWebViewEventsAttached;
    private bool _syncingPreviewFromEditor;
    private bool _syncingEditorFromPreview;
    private bool _previewScrollSyncQueued;
    private double _pendingPreviewScrollRatio;

    public bool IsVisible => _previewVisible;
    public VimEditorControl? SourceEditor => _previewSourceEditor;

    public MarkdownPreviewController(
        Func<PaneNode?> getGlobalRoot,
        Action<PaneNode?> setGlobalRoot,
        Func<VimEditorControl?> currentEditor,
        Func<VimEditorControl?> focusedEditor,
        Func<string> getMarkdownPreviewStyle,
        Border editorContent,
        Grid previewPanel,
        ToggleButton previewBtn,
        GridSplitter previewSplitter,
        ColumnDefinition previewSplitterCol,
        ColumnDefinition previewCol,
        WebView2 previewBrowser)
    {
        _getGlobalRoot = getGlobalRoot;
        _setGlobalRoot = setGlobalRoot;
        _currentEditor = currentEditor;
        _focusedEditor = focusedEditor;
        _getMarkdownPreviewStyle = getMarkdownPreviewStyle;
        _editorContent = editorContent;
        _previewPanel = previewPanel;
        _previewBtn = previewBtn;
        _previewSplitter = previewSplitter;
        _previewSplitterCol = previewSplitterCol;
        _previewCol = previewCol;
        _previewBrowser = previewBrowser;
    }

    /// <summary>Stop any pending debounced refresh (called on app shutdown).</summary>
    public void StopPendingWork() => _previewDebounceTimer?.Stop();

    public void Toggle()
    {
        if (_previewVisible)
            Close();
        else
            Show();
    }

    /// <summary>Schedule a preview refresh if the given editor is the current preview source.</summary>
    public void NotifyEditorActivated(VimEditorControl? editor)
    {
        if (_previewVisible && editor == _previewSourceEditor)
            ScheduleUpdate();
    }

    public void Show()
    {
        var globalRoot = _getGlobalRoot();
        var currentEditor = _currentEditor();
        if (globalRoot == null || currentEditor == null) return;

        Close(focusEditor: false);
        globalRoot = _getGlobalRoot();

        var sourcePane = PaneTreeHelpers.FindEditorPane(globalRoot!, currentEditor);
        if (sourcePane == null) return;

        _previewVisible = true;
        _previewSourceEditor = currentEditor;
        _previewSourceEditor.ViewportScrolled += PreviewSourceEditor_ViewportScrolled;
        _previewPanel.Visibility = Visibility.Visible;
        _previewBtn.IsChecked = true;

        _previewSplitter.Visibility = Visibility.Collapsed;
        _previewSplitterCol.Width = new GridLength(0);
        _previewCol.Width = new GridLength(0);

        var sourceElement = (UIElement)sourcePane.Editor;
        var parentSplit = PaneTreeHelpers.FindParentSplit(globalRoot!, sourcePane);
        int gridPos = -1;
        if (parentSplit != null)
        {
            gridPos = parentSplit.Vertical
                ? Grid.GetColumn(sourceElement)
                : Grid.GetRow(sourceElement);
            parentSplit.Container.Children.Remove(sourceElement);
        }
        else
        {
            _editorContent.Child = null;
        }

        DetachFromParent(_previewPanel);
        var previewNode = new PreviewPaneNode { Panel = _previewPanel };
        var newGrid = SplitPaneNode.BuildGrid(vertical: true, sourceElement, _previewPanel);
        var splitNode = new SplitPaneNode
        {
            Vertical = true,
            First = sourcePane,
            Second = previewNode,
            Container = newGrid
        };

        if (parentSplit == null)
        {
            _setGlobalRoot(splitNode);
            _editorContent.Child = newGrid;
        }
        else
        {
            if (parentSplit.Vertical) Grid.SetColumn(newGrid, gridPos);
            else                      Grid.SetRow(newGrid, gridPos);
            parentSplit.Container.Children.Add(newGrid);

            if (parentSplit.First == sourcePane) parentSplit.First = splitNode;
            else parentSplit.Second = splitNode;
        }

        _previewPaneNode = previewNode;
        Refresh();
        currentEditor.Focus();
    }

    public void Close(bool focusEditor = true)
    {
        _previewDebounceTimer?.Stop();

        var globalRoot = _getGlobalRoot();
        if (globalRoot != null && _previewPaneNode != null)
        {
            var parentSplit = PaneTreeHelpers.FindParentSplit(globalRoot, _previewPaneNode);
            if (parentSplit != null)
            {
                var sibling = parentSplit.First == _previewPaneNode
                    ? parentSplit.Second
                    : parentSplit.First;
                var siblingElement = PaneTreeHelpers.PaneToElement(sibling);

                parentSplit.Container.Children.Remove(siblingElement);
                parentSplit.Container.Children.Remove(_previewPanel);

                var grandParent = PaneTreeHelpers.FindParentSplit(globalRoot, parentSplit);
                if (grandParent == null)
                {
                    _setGlobalRoot(sibling);
                    _editorContent.Child = siblingElement;
                }
                else
                {
                    grandParent.ReplaceChild(parentSplit.Container, siblingElement);
                    if (grandParent.First == parentSplit) grandParent.First = sibling;
                    else grandParent.Second = sibling;
                }
            }
        }

        _previewVisible = false;
        _previewPaneNode = null;
        if (_previewSourceEditor != null)
            _previewSourceEditor.ViewportScrolled -= PreviewSourceEditor_ViewportScrolled;
        _previewSourceEditor = null;
        _previewBtn.IsChecked = false;
        _previewPanel.Visibility = Visibility.Collapsed;
        _previewSplitter.Visibility = Visibility.Collapsed;
        _previewSplitterCol.Width = new GridLength(0);
        _previewCol.Width = new GridLength(0);
        DetachFromParent(_previewPanel);

        if (focusEditor)
            _focusedEditor()?.Focus();
    }

    private static void DetachFromParent(UIElement element)
    {
        switch (VisualTreeHelper.GetParent(element))
        {
            case Panel panel:
                panel.Children.Remove(element);
                break;
            case Decorator decorator when decorator.Child == element:
                decorator.Child = null;
                break;
            case ContentControl contentControl when contentControl.Content == element:
                contentControl.Content = null;
                break;
        }
    }

    public void ScheduleUpdate()
    {
        if (_previewDebounceTimer == null)
        {
            _previewDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            // Use sender to avoid closing over the nullable field
            _previewDebounceTimer.Tick += (s, _) =>
            {
                ((DispatcherTimer)s!).Stop();
                Refresh();
            };
        }
        _previewDebounceTimer.Stop();
        _previewDebounceTimer.Start();
    }

    private void AttachPreviewWebViewEvents()
    {
        if (_previewWebViewEventsAttached || _previewBrowser.CoreWebView2 == null) return;

        _previewBrowser.CoreWebView2.WebMessageReceived += PreviewBrowser_WebMessageReceived;
        _previewBrowser.NavigationCompleted += PreviewBrowser_NavigationCompleted;
        _previewWebViewEventsAttached = true;
    }

    private async void PreviewSourceEditor_ViewportScrolled(object? sender, EventArgs e)
    {
        if (_syncingEditorFromPreview || sender is not VimEditorControl editor) return;
        await QueuePreviewScrollSyncAsync(editor.VerticalScrollRatio);
    }

    private async void PreviewBrowser_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!_previewVisible || _previewSourceEditor == null) return;
        await QueuePreviewScrollSyncAsync(_previewSourceEditor.VerticalScrollRatio);
    }

    private void PreviewBrowser_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (_syncingPreviewFromEditor || !_previewVisible || _previewSourceEditor == null) return;

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var type)
                || type.GetString() != "markdownPreviewScroll"
                || !root.TryGetProperty("ratio", out var ratioElement)
                || !ratioElement.TryGetDouble(out double ratio))
                return;

            _syncingEditorFromPreview = true;
            _previewSourceEditor.ScrollToVerticalRatio(ratio);
        }
        catch
        {
            // Ignore malformed messages from preview content.
        }
        finally
        {
            _syncingEditorFromPreview = false;
        }
    }

    private async Task QueuePreviewScrollSyncAsync(double ratio)
    {
        _pendingPreviewScrollRatio = Math.Clamp(ratio, 0.0, 1.0);
        if (_previewScrollSyncQueued) return;

        _previewScrollSyncQueued = true;
        try
        {
            while (_previewVisible)
            {
                var nextRatio = _pendingPreviewScrollRatio;
                await ScrollPreviewToRatioAsync(nextRatio);

                if (Math.Abs(nextRatio - _pendingPreviewScrollRatio) < 0.0001)
                    break;
            }
        }
        finally
        {
            _previewScrollSyncQueued = false;
        }
    }

    private async Task ScrollPreviewToRatioAsync(double ratio)
    {
        if (!_previewVisible || _previewBrowser.CoreWebView2 == null) return;

        _syncingPreviewFromEditor = true;
        try
        {
            var script = FormattableString.Invariant(
                $"window.setMarkdownPreviewScrollRatio && window.setMarkdownPreviewScrollRatio({Math.Clamp(ratio, 0.0, 1.0):R});");
            await _previewBrowser.ExecuteScriptAsync(script);
        }
        catch
        {
            // Best effort: WebView can be navigating while the editor scrolls.
        }
        finally
        {
            _syncingPreviewFromEditor = false;
        }
    }

    public async void Refresh()
    {
        var editor = _previewSourceEditor ?? _currentEditor();
        if (!_previewVisible || editor == null) return;

        // All concurrent callers await the same Task — prevents parallel EnsureCoreWebView2Async calls.
        _webView2InitTask ??= _previewBrowser.EnsureCoreWebView2Async();
        try
        {
            await _webView2InitTask;
        }
        catch
        {
            _webView2InitTask = null; // allow retry on next open
            if (!_previewVisible) return;
            _previewBrowser.CoreWebView2?.NavigateToString(
                "<html><body style='background:#282A36;color:#FF5555;"
                + "font-family:Segoe UI,sans-serif;font-size:13px;padding:20px'>"
                + "WebView2 の初期化に失敗しました。Edge WebView2 ランタイムがインストールされているか確認してください。"
                + "</body></html>");
            return;
        }

        // Re-check state after the async suspension (user may have closed preview or switched editor)
        editor = _previewSourceEditor ?? _currentEditor();
        if (!_previewVisible || editor == null) return;
        if (_previewBrowser.CoreWebView2 == null) return;
        AttachPreviewWebViewEvents();

        var filePath = editor.Engine.CurrentBuffer.FilePath;
        var ext = filePath != null ? Path.GetExtension(filePath).ToLowerInvariant() : string.Empty;

        if (ext != ".md" && ext != ".markdown")
        {
            _previewBrowser.CoreWebView2.NavigateToString(
                "<html><body style='background:#282A36;color:#6272A4;"
                + "font-family:Segoe UI,sans-serif;font-size:13px;padding:20px'>"
                + "Not a Markdown file</body></html>");
            return;
        }

        var text = editor.Engine.CurrentBuffer.Text.GetText();
        var title = filePath != null ? Path.GetFileName(filePath) : "Preview";
        var html = MarkdownRenderer.RenderToHtml(text, title, _getMarkdownPreviewStyle());
        _previewBrowser.CoreWebView2.NavigateToString(html);
    }
}
