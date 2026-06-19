using System.Runtime.InteropServices;

namespace Editor.Controls.Ime;

// ─────────────────────────────────────────────────────────────────────────────
// COM interop for a custom TSF (Text Services Framework) text store.
//
// These declarations let the editor install its own ITextStoreACP so the IME
// writes the composition string + display attributes (clause boundaries / focused
// clause) directly into our store, instead of relying on WPF's default text store.
//
// vtable order is load-bearing: every method must appear in the exact order and
// with the exact signature of the native interface (textstor.h / msctf.h). Unused
// trailing slots may be omitted; unused leading/middle slots are kept as no-arg
// `_NN()` placeholders purely to preserve slot offsets.
// ─────────────────────────────────────────────────────────────────────────────

internal static class TsfHr
{
    public const int S_OK = 0;
    public const int S_FALSE = 1;
    public const int E_FAIL = unchecked((int)0x80004005);
    public const int E_NOTIMPL = unchecked((int)0x80004001);
    public const int E_INVALIDARG = unchecked((int)0x80070057);

    // textstor.h error/success codes
    public const int TS_E_INVALIDPOS = unchecked((int)0x80040200);
    public const int TS_E_NOLOCK = unchecked((int)0x80040201);
    public const int TS_E_NOOBJECT = unchecked((int)0x80040202);
    public const int TS_E_NOSELECTION = unchecked((int)0x80040205);
    public const int TS_E_NOLAYOUT = unchecked((int)0x80040206);
    public const int TS_E_SYNCHRONOUS = unchecked((int)0x80040207);
    public const int TS_E_READONLY = unchecked((int)0x80040208);
    public const int TS_S_ASYNC = 0x00040300;
}

internal static class TsfConst
{
    // RequestLock dwLockFlags
    public const uint TS_LF_SYNC = 0x1;
    public const uint TS_LF_READ = 0x2;
    public const uint TS_LF_WRITE = 0x4;
    public const uint TS_LF_READWRITE = 0x6;

    // GetStatus static flags
    public const uint TS_SS_REGIONS = 0x2;
    public const uint TS_SS_TRANSITORY = 0x4;
    public const uint TS_SS_NOHIDDENTEXT = 0x8;

    // AdviseSink masks (ITextStoreACPSink notifications we ask to receive)
    public const uint TS_AS_TEXT_CHANGE = 0x1;
    public const uint TS_AS_SEL_CHANGE = 0x2;
    public const uint TS_AS_LAYOUT_CHANGE = 0x4;
    public const uint TS_AS_ATTR_CHANGE = 0x8;
    public const uint TS_AS_STATUS_CHANGE = 0x10;
    public const uint TS_AS_ALL_SINKS =
        TS_AS_TEXT_CHANGE | TS_AS_SEL_CHANGE | TS_AS_LAYOUT_CHANGE | TS_AS_ATTR_CHANGE | TS_AS_STATUS_CHANGE;

    // Run types (GetText)
    public const int TS_RT_PLAIN = 0;

    // Active selection end (TfActiveSelEnd)
    public const int TS_AE_NONE = 0;
    public const int TS_AE_END = 2;

    public const uint TS_DEFAULT_SELECTION = 0xFFFFFFFF;

    // GetTextExt / view
    public const uint TEXT_STORE_VIEW = 1;

    // OnLayoutChange codes (TsLayoutCode)
    public const int TS_LC_CHANGE = 0;

    public const uint TF_INVALID_COOKIE = 0xFFFFFFFF;

    // IIDs / GUIDs
    public static readonly Guid IID_ITextStoreACPSink = new("22D44C94-A419-4542-A272-AE26093ECECF");
    public static readonly Guid IID_ITfContextOwnerCompositionSink = new("5F20AA40-B57A-4F34-96AB-3576F377CC79");
    public static readonly Guid IID_ITfSource = new("4EA48A35-60AE-446F-8FD6-E6A8D82459F7");
}

[StructLayout(LayoutKind.Sequential)]
public struct TS_POINT { public int x, y; }

[StructLayout(LayoutKind.Sequential)]
public struct TS_RECT { public int left, top, right, bottom; }

[StructLayout(LayoutKind.Sequential)]
public struct TS_STATUS
{
    public uint dwDynamicFlags;
    public uint dwStaticFlags;
}

[StructLayout(LayoutKind.Sequential)]
public struct TS_SELECTIONSTYLE
{
    public int ase;                                  // TfActiveSelEnd
    [MarshalAs(UnmanagedType.Bool)] public bool fInterimChar;
}

[StructLayout(LayoutKind.Sequential)]
public struct TS_SELECTION_ACP
{
    public int acpStart;
    public int acpEnd;
    public TS_SELECTIONSTYLE style;
}

[StructLayout(LayoutKind.Sequential)]
public struct TS_TEXTCHANGE
{
    public int acpStart;
    public int acpOldEnd;
    public int acpNewEnd;
}

[StructLayout(LayoutKind.Sequential)]
public struct TS_RUNINFO
{
    public uint uCount;
    public int type;                                 // TsRunType
}

// The application's text store. We implement this and pass the managed object to TSF;
// the CLR builds the CCW vtable from the declaration order below, so it must match
// textstor.h exactly. This must be COM-visible but not ComImport: ComImport is for
// interfaces implemented by external COM objects, and prevents TSF from advising our
// managed composition sink through the same object.
[ComVisible(true)]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("28888FE3-C2A0-483A-A3EA-8CB1CE51FF3D")]
public interface ITextStoreACP
{
    [PreserveSig] int AdviseSink(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] object punk, uint dwMask);
    [PreserveSig] int UnadviseSink([MarshalAs(UnmanagedType.IUnknown)] object punk);
    [PreserveSig] int RequestLock(uint dwLockFlags, out int phrSession);
    [PreserveSig] int GetStatus(out TS_STATUS pdcs);
    [PreserveSig] int QueryInsert(int acpTestStart, int acpTestEnd, uint cch, out int pacpResultStart, out int pacpResultEnd);
    [PreserveSig] int GetSelection(uint ulIndex, uint ulCount,
        [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] TS_SELECTION_ACP[] pSelection, out uint pcFetched);
    [PreserveSig] int SetSelection(uint ulCount,
        [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] TS_SELECTION_ACP[] pSelection);
    [PreserveSig] int GetText(int acpStart, int acpEnd,
        IntPtr pchPlain, uint cchPlainReq, out uint pcchPlainOut,
        IntPtr prgRunInfo, uint cRunInfoReq, out uint pcRunInfoOut, out int pacpNext);
    [PreserveSig] int SetText(uint dwFlags, int acpStart, int acpEnd, IntPtr pchText, uint cch, out TS_TEXTCHANGE pChange);
    [PreserveSig] int GetFormattedText(int acpStart, int acpEnd, out IntPtr ppDataObject);
    [PreserveSig] int GetEmbedded(int acpPos, ref Guid rguidService, ref Guid riid, out IntPtr ppunk);
    [PreserveSig] int QueryInsertEmbedded(ref Guid pguidService, IntPtr pFormatEtc, [MarshalAs(UnmanagedType.Bool)] out bool pfInsertable);
    [PreserveSig] int InsertEmbedded(uint dwFlags, int acpStart, int acpEnd, IntPtr pDataObject, out TS_TEXTCHANGE pChange);
    [PreserveSig] int InsertTextAtSelection(uint dwFlags, IntPtr pchText, uint cch,
        out int pacpStart, out int pacpEnd, out TS_TEXTCHANGE pChange);
    [PreserveSig] int InsertEmbeddedAtSelection(uint dwFlags, IntPtr pDataObject,
        out int pacpStart, out int pacpEnd, out TS_TEXTCHANGE pChange);
    [PreserveSig] int RequestSupportedAttrs(uint dwFlags, uint cFilterAttrs, IntPtr paFilterAttrs);
    [PreserveSig] int RequestAttrsAtPosition(int acpPos, uint cFilterAttrs, IntPtr paFilterAttrs, uint dwFlags);
    [PreserveSig] int RequestAttrsTransitioningAtPosition(int acpPos, uint cFilterAttrs, IntPtr paFilterAttrs, uint dwFlags);
    [PreserveSig] int FindNextAttrTransition(int acpStart, int acpHalt, uint cFilterAttrs, IntPtr paFilterAttrs,
        uint dwFlags, out int pacpNext, [MarshalAs(UnmanagedType.Bool)] out bool pfFound, out int plFoundOffset);
    [PreserveSig] int RetrieveRequestedAttrs(uint ulCount, IntPtr paAttrVals, out uint pcFetched);
    [PreserveSig] int GetEndACP(out int pacp);
    [PreserveSig] int GetActiveView(out uint pvcView);
    [PreserveSig] int GetACPFromPoint(uint vcView, ref TS_POINT ptScreen, uint dwFlags, out int pacp);
    [PreserveSig] int GetTextExt(uint vcView, int acpStart, int acpEnd, out TS_RECT prc, [MarshalAs(UnmanagedType.Bool)] out bool pfClipped);
    [PreserveSig] int GetScreenExt(uint vcView, out TS_RECT prc);
    [PreserveSig] int GetWnd(uint vcView, out IntPtr phwnd);
}

// TSF implements this and hands it to us via ITextStoreACP.AdviseSink. We hold it
// and call back into TSF (OnLockGranted drives the synchronous edit pump).
[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("22D44C94-A419-4542-A272-AE26093ECECF")]
internal interface ITextStoreACPSink
{
    [PreserveSig] int OnTextChange(uint dwFlags, ref TS_TEXTCHANGE pChange);
    [PreserveSig] int OnSelectionChange();
    [PreserveSig] int OnLayoutChange(int lcode, uint vcView);
    [PreserveSig] int OnStatusChange(uint dwFlags);
    [PreserveSig] int OnAttrsChange(int acpStart, int acpEnd, uint cAttrs, IntPtr paAttrs);
    [PreserveSig] int OnLockGranted(uint dwLockFlags);
    [PreserveSig] int OnStartEditTransaction();
    [PreserveSig] int OnEndEditTransaction();
}

// We implement this to observe the composition lifecycle on our context. pComposition
// / pRangeNew are passed as IntPtr because we never dereference them.
[ComVisible(true)]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("5F20AA40-B57A-4F34-96AB-3576F377CC79")]
public interface ITfContextOwnerCompositionSink
{
    [PreserveSig] int OnStartComposition(IntPtr pComposition, [MarshalAs(UnmanagedType.Bool)] out bool pfOk);
    [PreserveSig] int OnUpdateComposition(IntPtr pComposition, IntPtr pRangeNew);
    [PreserveSig] int OnEndComposition(IntPtr pComposition);
}

[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("4EA48A35-60AE-446F-8FD6-E6A8D82459F7")]
internal interface ITfSourceTs
{
    [PreserveSig] int AdviseSink(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] object punk, out uint pdwCookie);
    [PreserveSig] int UnadviseSink(uint dwCookie);
}

// Declared through the last slot we call (AssociateFocus, slot 7).
[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("AA80E801-2021-11D2-93E0-0060B067B86E")]
internal interface ITfThreadMgrTs
{
    [PreserveSig] int Activate(out uint ptid);
    [PreserveSig] int Deactivate();
    [PreserveSig] int CreateDocumentMgr(out ITfDocumentMgrTs ppdim);
    [PreserveSig] int EnumDocumentMgrs(out IntPtr ppEnum);
    [PreserveSig] int GetFocus(out ITfDocumentMgrTs? ppdimFocus);
    [PreserveSig] int SetFocus(ITfDocumentMgrTs? pdimFocus);
    [PreserveSig] int AssociateFocus(IntPtr hwnd, ITfDocumentMgrTs? pdimNew, out ITfDocumentMgrTs? ppdimPrev);
}

// Declared through GetTop (slot 4).
[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("AA80E7F4-2021-11D2-93E0-0060B067B86E")]
internal interface ITfDocumentMgrTs
{
    [PreserveSig] int CreateContext(uint tidOwner, uint dwFlags,
        [MarshalAs(UnmanagedType.IUnknown)] object? punk, out ITfContextTs? ppic, out uint pecTextStore);
    [PreserveSig] int Push(ITfContextTs? pic);
    [PreserveSig] int Pop(uint dwFlags);
    [PreserveSig] int GetTop(out ITfContextTs? ppic);
}

// We only need this as a typed handle for CreateContext/Push and to QI ITfSource on it.
// No methods are invoked, so the IUnknown slots suffice.
[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("AA80E7FD-2021-11D2-93E0-0060B067B86E")]
internal interface ITfContextTs
{
}
