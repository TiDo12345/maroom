// Maroom – detection of a redirected (MSIX container) environment, shared by Maroom.exe and the setup.
// © 2026 Timo Dörsam
//
// Shell commands of some packaged apps (e.g. a terminal inside a Store app) run in the app's container:
// files written to %LOCALAPPDATA% silently land in %LOCALAPPDATA%\Packages\<app>\LocalCache\Local
// instead, and only processes in that container see them. An install or a Maroom started from there
// would work with a data folder (state.txt, positions) that the normally started Maroom never sees,
// so the two would disagree about which desktop's files are in the desktop folder.
// Therefore both programs check this first and start themselves again via Explorer, outside the container.

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

static class Container
{
    static readonly string LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    // The package folder (%LOCALAPPDATA%\Packages\<app>) if writes to %LOCALAPPDATA% are redirected, otherwise null.
    // There is no API for this (such processes have no package identity), so write a probe file and look where it landed.
    public static string RedirectedTo()
    {
        string packages = Path.Combine(LocalAppData, "Packages");
        if (!Directory.Exists(packages)) return null;
        string name = "maroom-probe-" + Guid.NewGuid().ToString("N") + ".tmp";
        string probe = Path.Combine(LocalAppData, name);
        try
        {
            File.WriteAllText(probe, "");
            foreach (string dir in Directory.GetDirectories(packages))
                if (File.Exists(Path.Combine(Path.Combine(dir, "LocalCache"), Path.Combine("Local", name)))) return dir;
            return null;
        }
        catch { return null; }
        finally { try { File.Delete(probe); } catch { } }
    }

    // Path under which a file seen inside the container exists for processes outside of it.
    public static string RealPath(string path, string packageDir)
    {
        string local = LocalAppData.TrimEnd('\\') + "\\";
        if (!path.StartsWith(local, StringComparison.OrdinalIgnoreCase)) return path;
        string rel = path.Substring(local.Length);
        if (rel.StartsWith("Packages\\", StringComparison.OrdinalIgnoreCase)) return path;
        string redirected = Path.Combine(Path.Combine(packageDir, "LocalCache"), Path.Combine("Local", rel));
        return File.Exists(redirected) ? redirected : path;
    }

    public static string JoinArgs(string[] args)
    {
        StringBuilder sb = new StringBuilder();
        foreach (string a in args)
        {
            if (sb.Length > 0) sb.Append(' ');
            if (a.Length > 0 && a.IndexOfAny(new char[] { ' ', '\t', '"' }) < 0) sb.Append(a);
            else sb.Append('"').Append(a.Replace("\"", "\\\"")).Append('"');
        }
        return sb.ToString();
    }

    // Starts the file through the running Explorer (the desktop's shell view), so it runs outside the
    // container like a double-click would. Returns null on success, otherwise the error.
    public static string StartViaExplorer(string file, string args)
    {
        try
        {
            Type t = Type.GetTypeFromCLSID(new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39"));   // ShellWindows
            dynamic windows = Activator.CreateInstance(t);
            object loc = 0, root = Type.Missing;   // CSIDL_DESKTOP
            int hwnd;
            object desktop = windows.FindWindowSW(ref loc, ref root, 8 /* SWC_DESKTOP */, out hwnd, 1 /* SWFO_NEEDDISPATCH */);
            if (desktop == null) return "Explorer desktop not found";
            Guid topLevel = new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837");   // SID_STopLevelBrowser
            Guid iidBrowser = typeof(IShellBrowser).GUID;
            object browser;
            Marshal.ThrowExceptionForHR(((IServiceProviderCom)desktop).QueryService(ref topLevel, ref iidBrowser, out browser));
            object view;
            ((IShellBrowser)browser).QueryActiveShellView(out view);
            Guid iidDispatch = new Guid("00020400-0000-0000-C000-000000000046");
            object background;
            ((IShellView)view).GetItemObject(0 /* SVGIO_BACKGROUND */, ref iidDispatch, out background);
            // IShellFolderViewDual.Application -> IShellDispatch2.ShellExecute, executed by Explorer
            dynamic folderView = background;
            dynamic shell = folderView.Application;
            shell.ShellExecute(file, args, Path.GetDirectoryName(file), "open", 1 /* SW_SHOWNORMAL */);
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IServiceProviderCom
    {
        [PreserveSig] int QueryService(ref Guid service, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object obj);
    }

    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellBrowser
    {
        void GetWindow(out IntPtr h); void ContextSensitiveHelp(bool f);
        void InsertMenusSB(IntPtr a, IntPtr b); void SetMenuSB(IntPtr a, IntPtr b, IntPtr c); void RemoveMenusSB(IntPtr a);
        void SetStatusTextSB(IntPtr a); void EnableModelessSB(bool f); void TranslateAcceleratorSB(IntPtr a, short b);
        void BrowseObject(IntPtr a, uint b); void GetViewStateStream(uint a, out IntPtr b); void GetControlWindow(uint a, out IntPtr b);
        void SendControlMsg(uint a, uint b, IntPtr c, IntPtr d, out IntPtr e);
        void QueryActiveShellView([MarshalAs(UnmanagedType.IUnknown)] out object view);
    }

    [ComImport, Guid("000214E3-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellView
    {
        void GetWindow(out IntPtr h); void ContextSensitiveHelp(bool f); [PreserveSig] int TranslateAccelerator(IntPtr m);
        void EnableModeless(bool f); void UIActivate(uint s); void Refresh();
        void CreateViewWindow(IntPtr a, IntPtr b, IntPtr c, IntPtr d, out IntPtr e); void DestroyViewWindow();
        void GetCurrentInfo(IntPtr a); void AddPropertySheetPages(uint a, IntPtr b, IntPtr c); void SaveViewState();
        void SelectItem(IntPtr a, uint b);
        void GetItemObject(uint item, ref Guid riid, [MarshalAs(UnmanagedType.IDispatch)] out object obj);
    }
}
