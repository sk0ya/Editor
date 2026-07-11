using System.Runtime.InteropServices;
using Editor.Controls.Ime;

namespace Editor.Controls.Tests;

public sealed class EditorTextStoreTests
{
    [Fact]
    public void LoadedControlHost_ComposesCommitsCancelsAndReportsCaretGeometry()
    {
        WpfTestHost.Run(() => WpfTestHost.WithLoadedControl<VimEditorControl>((editor, window) =>
        {
            var insert = new System.Windows.Input.TextComposition(
                System.Windows.Input.InputManager.Current, editor, "i");
            editor.RaiseEvent(new System.Windows.Input.TextCompositionEventArgs(
                System.Windows.Input.Keyboard.PrimaryDevice, insert)
            {
                RoutedEvent = System.Windows.Input.TextCompositionManager.TextInputEvent,
                Source = editor
            });

            var host = (IEditorTextStoreHost)editor;
            Assert.True(host.IsCompositionAllowed);
            Assert.NotEqual(IntPtr.Zero, host.WindowHandle);
            Assert.True(host.TryGetCaretScreenRect(out int left, out int top, out int right, out int bottom));
            Assert.True(right > left);
            Assert.True(bottom > top);

            var store = new EditorTextStore(host, action => action());
            var sink = new FakeSink();
            Advise(store, sink);
            Assert.Equal(TsfHr.S_OK, store.OnStartComposition(IntPtr.Zero, out bool accepted));
            Assert.True(accepted);
            sink.OnGrant = _ => SetText(store, "変換");
            store.RequestLock(TsfConst.TS_LF_READWRITE, out _);
            store.OnEndComposition(IntPtr.Zero);
            Assert.Equal("変換", editor.Text);

            var canceled = new EditorTextStore(host, action => action());
            Assert.Equal(TsfHr.S_OK, canceled.OnStartComposition(IntPtr.Zero, out accepted));
            Assert.True(accepted);
            canceled.OnEndComposition(IntPtr.Zero);
            Assert.Equal("変換", editor.Text);
        }));
    }

    [Fact]
    public void CompositionLifecycle_UpdatesOverlayAndCommitsText()
    {
        var host = new FakeHost();
        var store = new EditorTextStore(host, action => action());
        var sink = new FakeSink();
        Advise(store, sink);

        Assert.Equal(TsfHr.S_OK, store.OnStartComposition(IntPtr.Zero, out bool accepted));
        Assert.True(accepted);

        sink.OnGrant = _ => SetText(store, "かんじ");
        Assert.Equal(TsfHr.S_OK, store.RequestLock(TsfConst.TS_LF_READWRITE, out int session));
        Assert.Equal(TsfHr.S_OK, session);
        Assert.Equal(("かんじ", 3), host.LastUpdate);

        store.OnEndComposition(IntPtr.Zero);

        Assert.Equal("かんじ", host.Committed);
        Assert.False(host.Canceled);
    }

    [Fact]
    public void RequestLock_ReentrantAsyncWriteUpgrade_IsGrantedAfterReadLock()
    {
        var store = new EditorTextStore(new FakeHost(), action => action());
        var sink = new FakeSink();
        Advise(store, sink);
        int grants = 0;

        sink.OnGrant = lockType =>
        {
            grants++;
            if (grants == 1)
            {
                Assert.Equal(TsfConst.TS_LF_READ, lockType);
                Assert.Equal(TsfHr.S_OK,
                    store.RequestLock(TsfConst.TS_LF_READWRITE, out int nestedSession));
                Assert.Equal(TsfHr.TS_S_ASYNC, nestedSession);
            }
            else
            {
                Assert.Equal(TsfConst.TS_LF_READWRITE, lockType);
                SetText(store, "変換");
            }
        };

        Assert.Equal(TsfHr.S_OK, store.RequestLock(TsfConst.TS_LF_READ, out int session));

        Assert.Equal(TsfHr.S_OK, session);
        Assert.Equal(2, grants);
    }

    [Fact]
    public void EndComposition_DuringDocumentLock_DefersResetNotification()
    {
        var scheduled = new Queue<Action>();
        var host = new FakeHost();
        var store = new EditorTextStore(host, scheduled.Enqueue);
        var sink = new FakeSink();
        Advise(store, sink);
        store.OnStartComposition(IntPtr.Zero, out _);

        sink.OnGrant = _ =>
        {
            SetText(store, "候補");
            store.OnEndComposition(IntPtr.Zero);
            Assert.Empty(sink.TextChanges);
        };
        store.RequestLock(TsfConst.TS_LF_READWRITE, out _);

        Assert.Single(scheduled);
        scheduled.Dequeue()();
        Assert.Equal(new TS_TEXTCHANGE { acpStart = 0, acpOldEnd = 2, acpNewEnd = 0 },
            sink.TextChanges.Single());
        Assert.Equal(1, sink.SelectionChanges);
    }

    [Fact]
    public void UnicodeText_UsesUtf16AcpOffsetsWithoutSplittingOrLosingSurrogates()
    {
        var host = new FakeHost();
        var store = new EditorTextStore(host, action => action());
        var sink = new FakeSink();
        Advise(store, sink);
        store.OnStartComposition(IntPtr.Zero, out _);

        sink.OnGrant = _ =>
        {
            Assert.Equal(TsfHr.S_OK, SetText(store, "A😀𠮷B"));
            Assert.Equal(TsfHr.S_OK, store.GetEndACP(out int end));
            Assert.Equal(6, end); // ACP is UTF-16 code units, as required by TSF.

            Assert.Equal(TsfHr.S_OK, store.SetSelection(1,
                [new TS_SELECTION_ACP { acpStart = 1, acpEnd = 5 }]));
            Assert.Equal(TsfHr.S_OK, store.GetSelection(0, 1, new TS_SELECTION_ACP[1], out uint fetched));
            Assert.Equal((uint)1, fetched);
            Assert.Equal("😀𠮷", ReadText(store, 1, 5));
        };
        store.RequestLock(TsfConst.TS_LF_READWRITE, out _);
        store.OnEndComposition(IntPtr.Zero);

        Assert.Equal("A😀𠮷B", host.Committed);
        Assert.Equal(("A😀𠮷B", 5), host.LastUpdate);
    }

    [Fact]
    public void ReplaceSelection_NotifiesOverlayWithCollapsedCaret()
    {
        var host = new FakeHost();
        var store = new EditorTextStore(host, action => action());
        var sink = new FakeSink();
        Advise(store, sink);
        store.OnStartComposition(IntPtr.Zero, out _);
        sink.OnGrant = _ =>
        {
            SetText(store, "かな文字");
            store.SetSelection(1, [new TS_SELECTION_ACP { acpStart = 2, acpEnd = 4 }]);
            using var text = NativeText.From("漢字😀");
            Assert.Equal(TsfHr.S_OK, store.InsertTextAtSelection(0, text.Pointer, (uint)text.Value.Length,
                out int start, out int end, out TS_TEXTCHANGE change));
            Assert.Equal((2, 6), (start, end));
            Assert.Equal(new TS_TEXTCHANGE { acpStart = 2, acpOldEnd = 4, acpNewEnd = 6 }, change);
            Assert.Equal("かな漢字😀", ReadText(store, 0, -1));
        };
        store.RequestLock(TsfConst.TS_LF_READWRITE, out _);
        Assert.Equal(("かな漢字😀", 6), host.LastUpdate);
    }

    [Fact]
    public void TextAndSelectionOperations_RequireTheAppropriateDocumentLock()
    {
        var store = new EditorTextStore(new FakeHost(), action => action());
        Assert.Equal(TsfHr.TS_E_NOLOCK, store.GetEndACP(out _));
        Assert.Equal(TsfHr.TS_E_NOLOCK, store.GetSelection(0, 1, new TS_SELECTION_ACP[1], out _));
        using var text = NativeText.From("x");
        Assert.Equal(TsfHr.TS_E_NOLOCK, store.SetText(0, 0, 0, text.Pointer, 1, out _));
    }

    private static void Advise(EditorTextStore store, FakeSink sink)
    {
        Guid iid = TsfConst.IID_ITextStoreACPSink;
        Assert.Equal(TsfHr.S_OK, store.AdviseSink(ref iid, sink, TsfConst.TS_AS_ALL_SINKS));
    }

    private static int SetText(EditorTextStore store, string text)
    {
        IntPtr chars = Marshal.StringToCoTaskMemUni(text);
        try
        {
            return store.SetText(0, 0, -1, chars, (uint)text.Length, out _);
        }
        finally
        {
            Marshal.FreeCoTaskMem(chars);
        }
    }

    private static string ReadText(EditorTextStore store, int start, int end)
    {
        IntPtr chars = Marshal.AllocCoTaskMem(128 * sizeof(char));
        try
        {
            Assert.Equal(TsfHr.S_OK, store.GetText(start, end, chars, 128, out uint count,
                IntPtr.Zero, 0, out _, out _));
            return Marshal.PtrToStringUni(chars, (int)count)!;
        }
        finally { Marshal.FreeCoTaskMem(chars); }
    }

    private sealed class NativeText : IDisposable
    {
        private NativeText(string value) { Value = value; Pointer = Marshal.StringToCoTaskMemUni(value); }
        public string Value { get; }
        public IntPtr Pointer { get; }
        public static NativeText From(string value) => new(value);
        public void Dispose() => Marshal.FreeCoTaskMem(Pointer);
    }

    private sealed class FakeHost : IEditorTextStoreHost
    {
        public bool IsCompositionAllowed => true;
        public IntPtr WindowHandle => IntPtr.Zero;
        public (string Text, int Caret)? LastUpdate { get; private set; }
        public string? Committed { get; private set; }
        public bool Canceled { get; private set; }
        public void OnCompositionUpdated(string text, int caret) => LastUpdate = (text, caret);
        public void OnCompositionCommitted(string text) => Committed = text;
        public void OnCompositionCanceled() => Canceled = true;
        public bool TryGetCaretScreenRect(out int left, out int top, out int right, out int bottom)
        { left = top = 0; right = bottom = 1; return true; }
        public bool TryGetClientScreenRect(out int left, out int top, out int right, out int bottom)
        { left = top = 0; right = bottom = 100; return true; }
    }

    private sealed class FakeSink : ITextStoreACPSink
    {
        public Action<uint>? OnGrant { get; set; }
        public List<TS_TEXTCHANGE> TextChanges { get; } = [];
        public int SelectionChanges { get; private set; }
        public int OnTextChange(uint dwFlags, ref TS_TEXTCHANGE pChange)
        { TextChanges.Add(pChange); return TsfHr.S_OK; }
        public int OnSelectionChange() { SelectionChanges++; return TsfHr.S_OK; }
        public int OnLayoutChange(int lcode, uint vcView) => TsfHr.S_OK;
        public int OnStatusChange(uint dwFlags) => TsfHr.S_OK;
        public int OnAttrsChange(int acpStart, int acpEnd, uint cAttrs, IntPtr paAttrs) => TsfHr.S_OK;
        public int OnLockGranted(uint dwLockFlags)
        { OnGrant?.Invoke(dwLockFlags); return TsfHr.S_OK; }
        public int OnStartEditTransaction() => TsfHr.S_OK;
        public int OnEndEditTransaction() => TsfHr.S_OK;
    }
}
