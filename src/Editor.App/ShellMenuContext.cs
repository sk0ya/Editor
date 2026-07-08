using System.Runtime.InteropServices;

namespace Editor.App;

// ─────────── Shell menu item model ───────────────────────────

// ─────────── Shell context menu (COM IContextMenu) ───────────

internal sealed class ShellMenuContext : IDisposable
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(
        string pszName, IntPtr pbc, out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);

    [DllImport("shell32.dll")]
    private static extern int SHBindToParent(
        IntPtr pidl, ref Guid riid, out IntPtr ppv, out IntPtr ppidlLast);

    [DllImport("shell32.dll")]
    private static extern void ILFree(IntPtr pidl);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenuEx(
        IntPtr hmenu, uint fuFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);

    [StructLayout(LayoutKind.Sequential)]
    private struct CMINVOKECOMMANDINFO
    {
        public uint   cbSize;
        public uint   fMask;
        public IntPtr hwnd;
        public IntPtr lpVerb;
        public IntPtr lpParameters;
        public IntPtr lpDirectory;
        public int    nShow;
        public uint   dwHotKey;
        public IntPtr hIcon;
    }

    private const uint CMF_NORMAL      = 0x00000000;
    private const uint CMF_EXPLORE     = 0x00000020;
    private const uint TPM_RETURNCMD   = 0x0100;
    private const uint TPM_RIGHTBUTTON = 0x0002;

    [ComImport, Guid("000214e4-0000-0000-c000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenuCom
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
        [PreserveSig] int InvokeCommand(IntPtr pici);
        [PreserveSig] int GetCommandString(UIntPtr idCmd, uint uType, IntPtr pReserved, IntPtr pszName, uint cchMax);
    }

    [ComImport, Guid("000214E6-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellFolderCom
    {
        void ParseDisplayName(IntPtr hwnd, IntPtr pbc, [MarshalAs(UnmanagedType.LPWStr)] string pszDisplayName, out uint pchEaten, out IntPtr ppidl, ref uint pdwAttributes);
        void EnumObjects(IntPtr hwnd, uint grfFlags, out IntPtr ppenumIDList);
        void BindToObject(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        void BindToStorage(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);
        void CreateViewObject(IntPtr hwnd, ref Guid riid, out IntPtr ppv);
        void GetAttributesOf(uint cidl, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, ref uint rgfInOut);
        [PreserveSig] int GetUIObjectOf(IntPtr hwndOwner, uint cidl, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] IntPtr[] apidl, ref Guid riid, uint rgfReserved, out IntPtr ppv);
        void GetDisplayNameOf(IntPtr pidl, uint uFlags, out IntPtr pName);
        void SetNameOf(IntPtr hwnd, IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string pszName, uint uFlags, out IntPtr ppidlOut);
    }

    private object? _ctxObj;
    private object? _folderObj;
    private IntPtr  _pidlFull;
    private IntPtr  _hMenu;
    private IContextMenuCom? _ctx;

    private ShellMenuContext(object ctxObj, object folderObj, IntPtr pidlFull,
        IntPtr hMenu, IContextMenuCom ctx)
    {
        _ctxObj    = ctxObj;
        _folderObj = folderObj;
        _pidlFull  = pidlFull;
        _hMenu     = hMenu;
        _ctx       = ctx;
    }

    private static ShellMenuContext? Create(IntPtr hwnd, string path)
    {
        IntPtr pidlFull   = IntPtr.Zero;
        IntPtr hMenu      = IntPtr.Zero;
        object? ctxObj    = null;
        object? folderObj = null;

        try
        {
            int hr = SHParseDisplayName(path, IntPtr.Zero, out pidlFull, 0, out _);
            if (hr != 0 || pidlFull == IntPtr.Zero) return null;

            var iidFolder = new Guid("000214E6-0000-0000-C000-000000000046");
            hr = SHBindToParent(pidlFull, ref iidFolder, out IntPtr psfParent, out IntPtr pidlChild);
            if (hr != 0 || psfParent == IntPtr.Zero) return null;

            folderObj = Marshal.GetObjectForIUnknown(psfParent);
            Marshal.Release(psfParent);

            var iidCtx = new Guid("000214e4-0000-0000-c000-000000000046");
            IntPtr[] pidls = [pidlChild];
            hr = ((IShellFolderCom)folderObj).GetUIObjectOf(hwnd, 1, pidls, ref iidCtx, 0, out IntPtr pCtx);
            if (hr != 0 || pCtx == IntPtr.Zero) return null;

            ctxObj = Marshal.GetObjectForIUnknown(pCtx);
            Marshal.Release(pCtx);
            var ctx = (IContextMenuCom)ctxObj;

            hMenu = CreatePopupMenu();
            ctx.QueryContextMenu(hMenu, 0, 1, 0x7FFF, CMF_NORMAL | CMF_EXPLORE);

            return new ShellMenuContext(ctxObj, folderObj, pidlFull, hMenu, ctx);
        }
        catch
        {
            if (hMenu     != IntPtr.Zero) DestroyMenu(hMenu);
            if (pidlFull  != IntPtr.Zero) ILFree(pidlFull);
            if (ctxObj    != null) Marshal.ReleaseComObject(ctxObj);
            if (folderObj != null) Marshal.ReleaseComObject(folderObj);
            return null;
        }
    }

    // Show the Win32 popup and invoke the selected command.
    private void Show(IntPtr hwnd, int x, int y)
    {
        if (_ctx == null || _hMenu == IntPtr.Zero) return;

        uint cmd = TrackPopupMenuEx(_hMenu, TPM_RETURNCMD | TPM_RIGHTBUTTON, x, y, hwnd, IntPtr.Zero);
        if (cmd == 0) return;

        var ici = new CMINVOKECOMMANDINFO
        {
            cbSize       = (uint)Marshal.SizeOf<CMINVOKECOMMANDINFO>(),
            hwnd         = hwnd,
            lpVerb       = new IntPtr((int)(cmd - 1)),
            lpParameters = IntPtr.Zero,
            lpDirectory  = IntPtr.Zero,
            nShow        = 1
        };
        IntPtr pIci = Marshal.AllocHGlobal(Marshal.SizeOf<CMINVOKECOMMANDINFO>());
        try
        {
            Marshal.StructureToPtr(ici, pIci, false);
            _ctx.InvokeCommand(pIci);
        }
        catch { /* best-effort */ }
        finally { Marshal.FreeHGlobal(pIci); }
    }

    public static void ShowDirect(IntPtr hwnd, string path, int x, int y)
    {
        using var ctx = Create(hwnd, path);
        ctx?.Show(hwnd, x, y);
    }

    public void Dispose()
    {
        if (_hMenu    != IntPtr.Zero) { DestroyMenu(_hMenu);   _hMenu    = IntPtr.Zero; }
        if (_pidlFull != IntPtr.Zero) { ILFree(_pidlFull);     _pidlFull = IntPtr.Zero; }
        if (_ctxObj    != null) { Marshal.ReleaseComObject(_ctxObj);    _ctxObj    = null; }
        if (_folderObj != null) { Marshal.ReleaseComObject(_folderObj); _folderObj = null; }
        _ctx = null;
    }
}
