using System.Runtime.InteropServices;
using System.Text;

namespace Editor.Controls.Ime;

/// <summary>
/// A minimal but real TSF text store (<see cref="ITextStoreACP"/>) scoped to the
/// in-flight IME composition. Making the editor a genuine TSF application means the
/// IME writes the composition string and its display attributes (clause boundaries /
/// focused clause) into <em>our</em> store, which we read back to render composition
/// in-editor and to commit the result through the Vim engine.
///
/// The store deliberately exposes only the composition text (not the whole document):
/// ACP offsets stay tiny, no per-keystroke document sync is needed, and committed text
/// is handed to the editor via <see cref="IEditorTextStoreHost.OnCompositionCommitted"/>.
/// </summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
internal sealed class EditorTextStore : ITextStoreACP, ITfContextOwnerCompositionSink
{
    private const int E_NOINTERFACE = unchecked((int)0x80004002);
    private const uint TS_IAS_QUERYONLY = 0x1;

    private readonly IEditorTextStoreHost _host;

    private readonly StringBuilder _text = new();
    private int _selStart;
    private int _selEnd;

    private ITextStoreACPSink? _sink;
    private uint _lockType;          // 0 | TS_LF_READ | TS_LF_READWRITE bits
    private uint _pendingLockType;   // one coalesced asynchronous request; write wins
    private bool _composing;

    // Last caret rect GetTextExt reported successfully (screen coords). Reused when a
    // live query transiently fails so the IME keeps a valid anchor for its candidate
    // window instead of hiding it; NotifyLayoutChange re-queries once layout is back.
    private TS_RECT _lastCaretRect;
    private bool _haveCaretRect;
    private int _pendingResetOldLength;
    private bool _resetNotificationScheduled;
    private readonly Action<Action> _schedule;

    public EditorTextStore(IEditorTextStoreHost host, Action<Action>? schedule = null)
    {
        _host = host;
        _schedule = schedule ?? (action =>
            System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(action));
    }

    private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);

    private void RaiseOverlayUpdate()
    {
        try { _host.OnCompositionUpdated(_text.ToString(), Clamp(_selEnd, 0, _text.Length)); }
        catch { /* overlay refresh failures must not propagate into TSF */ }
    }

    // Clears the store and tells TSF the document is now empty (ACP back to 0) so the
    // next composition starts cleanly. The change notifications must only be raised when
    // we are not inside a granted document lock (TSF forbids reentrant notification).
    private void ResetStore()
    {
        int old = _text.Length;
        _text.Clear();
        _selStart = _selEnd = 0;
        if (old == 0) return;

        // OnEndComposition can run from inside OnLockGranted. TSF explicitly forbids
        // change notifications while it owns the document lock, so report the reset
        // after the current input callback has unwound. Coalesce resets because TSF
        // needs only the largest old ACP extent before the now-empty document.
        _pendingResetOldLength = Math.Max(_pendingResetOldLength, old);
        if (_resetNotificationScheduled) return;
        _resetNotificationScheduled = true;
        _schedule(FlushResetNotification);
    }

    private void FlushResetNotification()
    {
        _resetNotificationScheduled = false;
        int old = _pendingResetOldLength;
        _pendingResetOldLength = 0;
        if (old == 0 || _sink == null) return;

        var tc = new TS_TEXTCHANGE { acpStart = 0, acpOldEnd = old, acpNewEnd = 0 };
        try { _sink.OnTextChange(0, ref tc); } catch { }
        try { _sink.OnSelectionChange(); } catch { }
    }

    // ─────────────── ITextStoreACP ───────────────

    public int AdviseSink(ref Guid riid, object punk, uint dwMask)
    {
        if (riid != TsfConst.IID_ITextStoreACPSink) return E_NOINTERFACE;
        if (punk is not ITextStoreACPSink sink) return E_NOINTERFACE;
        _sink = sink;
        return TsfHr.S_OK;
    }

    public int UnadviseSink(object punk)
    {
        _sink = null;
        return TsfHr.S_OK;
    }

    public int RequestLock(uint dwLockFlags, out int phrSession)
    {
        phrSession = TsfHr.E_FAIL;
        if (_sink == null) return TsfHr.E_FAIL;

        uint requestedType = dwLockFlags & TsfConst.TS_LF_READWRITE;

        // A read lock may be upgraded by a reentrant asynchronous write request.
        // Rejecting that request loses the IME edit which opened/updated conversion UI.
        if (_lockType != 0)
        {
            if ((dwLockFlags & TsfConst.TS_LF_SYNC) != 0)
            {
                phrSession = TsfHr.TS_E_SYNCHRONOUS;
                return TsfHr.S_OK;
            }

            if (requestedType == TsfConst.TS_LF_READWRITE
                || _pendingLockType == 0)
                _pendingLockType = requestedType;
            phrSession = TsfHr.TS_S_ASYNC;
            return TsfHr.S_OK;
        }

        _lockType = requestedType;
        try { phrSession = _sink.OnLockGranted(requestedType); }
        finally { _lockType = 0; }

        // TSF needs only one callback for queued requests. If any request asked for
        // write access, the coalesced callback is promoted to read/write.
        while (_pendingLockType != 0 && _sink != null)
        {
            uint pending = _pendingLockType;
            _pendingLockType = 0;
            _lockType = pending;
            try { _ = _sink.OnLockGranted(pending); }
            finally { _lockType = 0; }
        }
        return TsfHr.S_OK;
    }

    public int GetStatus(out TS_STATUS pdcs)
    {
        pdcs = new TS_STATUS { dwDynamicFlags = 0, dwStaticFlags = TsfConst.TS_SS_NOHIDDENTEXT };
        return TsfHr.S_OK;
    }

    public int QueryInsert(int acpTestStart, int acpTestEnd, uint cch, out int pacpResultStart, out int pacpResultEnd)
    {
        int len = _text.Length;
        pacpResultStart = Clamp(acpTestStart, 0, len);
        pacpResultEnd = Clamp(acpTestEnd, 0, len);
        return TsfHr.S_OK;
    }

    public int GetSelection(uint ulIndex, uint ulCount, TS_SELECTION_ACP[] pSelection, out uint pcFetched)
    {
        pcFetched = 0;
        if (_lockType == 0) return TsfHr.TS_E_NOLOCK;
        if (ulCount == 0 || pSelection == null || pSelection.Length < 1) return TsfHr.S_OK;

        pSelection[0] = new TS_SELECTION_ACP
        {
            acpStart = _selStart,
            acpEnd = _selEnd,
            style = new TS_SELECTIONSTYLE { ase = TsfConst.TS_AE_END, fInterimChar = false }
        };
        pcFetched = 1;
        return TsfHr.S_OK;
    }

    public int SetSelection(uint ulCount, TS_SELECTION_ACP[] pSelection)
    {
        if (_lockType == 0) return TsfHr.TS_E_NOLOCK;
        if (ulCount >= 1 && pSelection != null && pSelection.Length >= 1)
        {
            int len = _text.Length;
            _selStart = Clamp(pSelection[0].acpStart, 0, len);
            _selEnd = Clamp(pSelection[0].acpEnd, 0, len);
            if (_composing) RaiseOverlayUpdate();
        }
        return TsfHr.S_OK;
    }

    public int GetText(int acpStart, int acpEnd,
        IntPtr pchPlain, uint cchPlainReq, out uint pcchPlainOut,
        IntPtr prgRunInfo, uint cRunInfoReq, out uint pcRunInfoOut, out int pacpNext)
    {
        pcchPlainOut = 0;
        pcRunInfoOut = 0;
        pacpNext = acpStart;
        if (_lockType == 0) return TsfHr.TS_E_NOLOCK;

        int len = _text.Length;
        int start = Clamp(acpStart < 0 ? 0 : acpStart, 0, len);
        int end = acpEnd < 0 ? len : Clamp(acpEnd, 0, len);
        if (end < start) end = start;

        int avail = end - start;
        int toCopy = Math.Min(avail, (int)cchPlainReq);
        if (pchPlain != IntPtr.Zero && toCopy > 0)
        {
            var chars = new char[toCopy];
            _text.CopyTo(start, chars, 0, toCopy);
            Marshal.Copy(chars, 0, pchPlain, toCopy);
        }
        pcchPlainOut = (uint)toCopy;

        if (prgRunInfo != IntPtr.Zero && cRunInfoReq > 0 && toCopy > 0)
        {
            Marshal.StructureToPtr(new TS_RUNINFO { uCount = (uint)toCopy, type = TsfConst.TS_RT_PLAIN }, prgRunInfo, false);
            pcRunInfoOut = 1;
        }

        pacpNext = start + toCopy;
        return TsfHr.S_OK;
    }

    public int SetText(uint dwFlags, int acpStart, int acpEnd, IntPtr pchText, uint cch, out TS_TEXTCHANGE pChange)
    {
        pChange = default;
        if ((_lockType & TsfConst.TS_LF_WRITE) == 0) return TsfHr.TS_E_NOLOCK;

        int len = _text.Length;
        int s = Clamp(acpStart, 0, len);
        int e = acpEnd < 0 ? len : Clamp(acpEnd, 0, len);
        if (e < s) e = s;

        string ins = (cch > 0 && pchText != IntPtr.Zero) ? (Marshal.PtrToStringUni(pchText, (int)cch) ?? "") : "";
        _text.Remove(s, e - s);
        _text.Insert(s, ins);

        pChange = new TS_TEXTCHANGE { acpStart = s, acpOldEnd = e, acpNewEnd = s + ins.Length };
        _selStart = _selEnd = s + ins.Length;
        if (_composing) RaiseOverlayUpdate();
        return TsfHr.S_OK;
    }

    public int InsertTextAtSelection(uint dwFlags, IntPtr pchText, uint cch,
        out int pacpStart, out int pacpEnd, out TS_TEXTCHANGE pChange)
    {
        pChange = default;
        int len = _text.Length;
        int s = Clamp(Math.Min(_selStart, _selEnd), 0, len);
        int e = Clamp(Math.Max(_selStart, _selEnd), 0, len);

        if ((dwFlags & TS_IAS_QUERYONLY) != 0)
        {
            pacpStart = s;
            pacpEnd = e;
            return TsfHr.S_OK;
        }

        if ((_lockType & TsfConst.TS_LF_WRITE) == 0)
        {
            pacpStart = s;
            pacpEnd = e;
            return TsfHr.TS_E_NOLOCK;
        }

        string ins = (cch > 0 && pchText != IntPtr.Zero) ? (Marshal.PtrToStringUni(pchText, (int)cch) ?? "") : "";
        _text.Remove(s, e - s);
        _text.Insert(s, ins);

        pacpStart = s;
        pacpEnd = s + ins.Length;
        pChange = new TS_TEXTCHANGE { acpStart = s, acpOldEnd = e, acpNewEnd = s + ins.Length };
        _selStart = _selEnd = s + ins.Length;
        if (_composing) RaiseOverlayUpdate();
        return TsfHr.S_OK;
    }

    public int GetEndACP(out int pacp)
    {
        pacp = 0;
        if (_lockType == 0) return TsfHr.TS_E_NOLOCK;
        pacp = _text.Length;
        return TsfHr.S_OK;
    }

    public int GetActiveView(out uint pvcView)
    {
        pvcView = TsfConst.TEXT_STORE_VIEW;
        return TsfHr.S_OK;
    }

    public int GetTextExt(uint vcView, int acpStart, int acpEnd, out TS_RECT prc, out bool pfClipped)
    {
        prc = default;
        pfClipped = false;
        if (_lockType == 0) return TsfHr.TS_E_NOLOCK;

        if (_host.TryGetCaretScreenRect(out int l, out int t, out int r, out int b))
        {
            _lastCaretRect = new TS_RECT { left = l, top = t, right = r, bottom = b };
            _haveCaretRect = true;
        }
        // A transient layout gap (control mid-relayout / not yet arranged) makes the live
        // query fail. Returning TS_E_NOLAYOUT here makes TSF hide the candidate window and
        // wait for OnLayoutChange, so fall back to the last good rect and keep it visible;
        // NotifyLayoutChange drives the eventual re-query with the corrected position.
        else if (!_haveCaretRect) return TsfHr.TS_E_NOLAYOUT;

        prc = _lastCaretRect;
        return TsfHr.S_OK;
    }

    /// <summary>True while an IME composition is in flight (a candidate window may exist).</summary>
    public bool IsComposing => _composing;

    /// <summary>
    /// Tells TSF the on-screen position of the store's text changed for a reason other than an
    /// edit — the editor scrolled, resized, or the caret moved. Without this the IME leaves its
    /// candidate/composition window at a stale position, and — critically — if a prior
    /// <see cref="GetTextExt"/> returned <c>TS_E_NOLAYOUT</c>, keeps it hidden for the rest of the
    /// composition. Sink notifications are forbidden while we service a document lock, so defer
    /// until the lock unwinds in that case.
    /// </summary>
    public void NotifyLayoutChange()
    {
        if (_sink == null) return;
        if (_lockType != 0) { _schedule(NotifyLayoutChange); return; }
        try { _sink.OnLayoutChange(TsfConst.TS_LC_CHANGE, TsfConst.TEXT_STORE_VIEW); }
        catch { /* layout-change notification is best-effort */ }
    }

    public int GetScreenExt(uint vcView, out TS_RECT prc)
    {
        prc = default;
        if (!_host.TryGetClientScreenRect(out int l, out int t, out int r, out int b)) return TsfHr.TS_E_NOLAYOUT;
        prc = new TS_RECT { left = l, top = t, right = r, bottom = b };
        return TsfHr.S_OK;
    }

    public int GetWnd(uint vcView, out IntPtr phwnd)
    {
        phwnd = _host.WindowHandle;
        return TsfHr.S_OK;
    }

    // ── Attribute requests: we expose no custom attributes ──
    public int RequestSupportedAttrs(uint dwFlags, uint cFilterAttrs, IntPtr paFilterAttrs) => TsfHr.S_OK;
    public int RequestAttrsAtPosition(int acpPos, uint cFilterAttrs, IntPtr paFilterAttrs, uint dwFlags) => TsfHr.S_OK;
    public int RequestAttrsTransitioningAtPosition(int acpPos, uint cFilterAttrs, IntPtr paFilterAttrs, uint dwFlags) => TsfHr.S_OK;

    public int FindNextAttrTransition(int acpStart, int acpHalt, uint cFilterAttrs, IntPtr paFilterAttrs,
        uint dwFlags, out int pacpNext, out bool pfFound, out int plFoundOffset)
    {
        pacpNext = acpHalt;
        pfFound = false;
        plFoundOffset = 0;
        return TsfHr.S_OK;
    }

    public int RetrieveRequestedAttrs(uint ulCount, IntPtr paAttrVals, out uint pcFetched)
    {
        pcFetched = 0;
        return TsfHr.S_OK;
    }

    // ── Unsupported: embedded objects, formatted text, point hit-testing ──
    public int GetFormattedText(int acpStart, int acpEnd, out IntPtr ppDataObject)
    { ppDataObject = IntPtr.Zero; return TsfHr.E_NOTIMPL; }

    public int GetEmbedded(int acpPos, ref Guid rguidService, ref Guid riid, out IntPtr ppunk)
    { ppunk = IntPtr.Zero; return TsfHr.E_NOTIMPL; }

    public int QueryInsertEmbedded(ref Guid pguidService, IntPtr pFormatEtc, out bool pfInsertable)
    { pfInsertable = false; return TsfHr.S_OK; }

    public int InsertEmbedded(uint dwFlags, int acpStart, int acpEnd, IntPtr pDataObject, out TS_TEXTCHANGE pChange)
    { pChange = default; return TsfHr.E_NOTIMPL; }

    public int InsertEmbeddedAtSelection(uint dwFlags, IntPtr pDataObject, out int pacpStart, out int pacpEnd, out TS_TEXTCHANGE pChange)
    { pacpStart = 0; pacpEnd = 0; pChange = default; return TsfHr.E_NOTIMPL; }

    public int GetACPFromPoint(uint vcView, ref TS_POINT ptScreen, uint dwFlags, out int pacp)
    { pacp = 0; return TsfHr.E_NOTIMPL; }

    // ─────────────── ITfContextOwnerCompositionSink ───────────────

    public int OnStartComposition(IntPtr pComposition, out bool pfOk)
    {
        pfOk = _host.IsCompositionAllowed;
        if (pfOk) _composing = true;
        return TsfHr.S_OK;
    }

    public int OnUpdateComposition(IntPtr pComposition, IntPtr pRangeNew)
    {
        if (_composing) RaiseOverlayUpdate();
        return TsfHr.S_OK;
    }

    public int OnEndComposition(IntPtr pComposition)
    {
        _composing = false;
        string committed = _text.ToString();
        ResetStore();
        try
        {
            if (committed.Length > 0) _host.OnCompositionCommitted(committed);
            else _host.OnCompositionCanceled();
        }
        catch { /* commit/cancel callback failures must not propagate into TSF */ }
        return TsfHr.S_OK;
    }
}
