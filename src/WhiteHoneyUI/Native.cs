using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace WhiteHoney.UI;

/// <summary>The few bits of Windows the page can't do itself: a message box when things go wrong, and a folder picker.</summary>
static class Native
{
    const uint MB_ICONERROR = 0x10, MB_TOPMOST = 0x40000, MB_SETFOREGROUND = 0x10000;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    /// <summary>A plain Windows message box, so a double-click never fails silently. Never throws.</summary>
    public static void ShowError(string text, string caption)
    {
        try { MessageBoxW(IntPtr.Zero, text, caption, MB_ICONERROR | MB_TOPMOST | MB_SETFOREGROUND); } catch { }
    }

    // ---------------------------------------------------------------- folder picker

    [StructLayout(LayoutKind.Sequential)]
    struct BROWSEINFOW
    {
        public IntPtr hwndOwner, pidlRoot, pszDisplayName, lpszTitle;
        public uint ulFlags;
        public IntPtr lpfn, lParam;
        public int iImage;
    }

    delegate int BrowseCallbackProc(IntPtr hwnd, uint msg, IntPtr lParam, IntPtr data);

    const uint BIF_RETURNONLYFSDIRS = 0x1, BIF_EDITBOX = 0x10, BIF_NEWDIALOGSTYLE = 0x40;
    const uint BFFM_INITIALIZED = 1, BFFM_SETSELECTIONW = 0x400 + 103;
    const int PathChars = 1024;
    static readonly IntPtr HWND_TOPMOST = new(-1), HWND_NOTOPMOST = new(-2);
    const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_SHOWWINDOW = 0x40;

    [DllImport("ole32.dll")] static extern int OleInitialize(IntPtr reserved);
    [DllImport("ole32.dll")] static extern void OleUninitialize();
    [DllImport("ole32.dll")] static extern void CoTaskMemFree(IntPtr p);
    [DllImport("shell32.dll")] static extern IntPtr SHBrowseForFolderW(ref BROWSEINFOW bi);
    [DllImport("shell32.dll")] static extern int SHGetPathFromIDListW(IntPtr pidl, IntPtr path);
    [DllImport("user32.dll")] static extern IntPtr SendMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int SetWindowTextW(IntPtr hWnd, string text);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr processId);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern int AttachThreadInput(uint idAttach, uint idAttachTo, int attach);
    [DllImport("user32.dll")] static extern int SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern int BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] static extern int SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    static int _picking;

    /// <summary>
    /// Shows the Windows folder picker on its own STA thread and returns the picked folder, or null when the player
    /// cancelled. Null at once when a picker is already open.
    /// </summary>
    public static Task<string?> PickFolder(string title, string? startIn)
    {
        if (Interlocked.Exchange(ref _picking, 1) == 1) return Task.FromResult<string?>(null);
        var done = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { done.TrySetResult(PickHere(title, startIn)); }
            catch (Exception ex) { done.TrySetException(ex); }
            finally { Volatile.Write(ref _picking, 0); }
        }) { IsBackground = true, Name = "Folder picker" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }

    static string? PickHere(string title, string? startIn)
    {
        OleInitialize(IntPtr.Zero);
        IntPtr titlePtr = Marshal.StringToHGlobalUni(title);
        IntPtr startPtr = startIn != null && Directory.Exists(startIn) ? Marshal.StringToHGlobalUni(startIn) : IntPtr.Zero;
        IntPtr display = Marshal.AllocHGlobal(PathChars * 2);
        IntPtr pathBuf = Marshal.AllocHGlobal(PathChars * 2);
        BrowseCallbackProc callback = (hwnd, msg, _, _) =>
        {
            if (msg == BFFM_INITIALIZED)
            {
                SetWindowTextW(hwnd, "White Honey - VRAM Optimizer");
                if (startPtr != IntPtr.Zero) SendMessageW(hwnd, BFFM_SETSELECTIONW, new IntPtr(1), startPtr);
                BringToFront(hwnd);
            }
            return 0;
        };
        try
        {
            var bi = new BROWSEINFOW
            {
                pszDisplayName = display,
                lpszTitle = titlePtr,
                ulFlags = BIF_RETURNONLYFSDIRS | BIF_NEWDIALOGSTYLE | BIF_EDITBOX, // the box takes a pasted path
                lpfn = Marshal.GetFunctionPointerForDelegate(callback),
            };
            var pidl = SHBrowseForFolderW(ref bi);
            if (pidl == IntPtr.Zero) return null;
            try { return SHGetPathFromIDListW(pidl, pathBuf) != 0 ? Marshal.PtrToStringUni(pathBuf) : null; }
            finally { CoTaskMemFree(pidl); }
        }
        finally
        {
            GC.KeepAlive(callback);
            Marshal.FreeHGlobal(titlePtr);
            if (startPtr != IntPtr.Zero) Marshal.FreeHGlobal(startPtr);
            Marshal.FreeHGlobal(display);
            Marshal.FreeHGlobal(pathBuf);
            OleUninitialize();
        }
    }

    /// <summary>
    /// The browser window has the focus, and Windows doesn't let a background process take it; sharing the focused
    /// window's input queue for a moment does.
    /// </summary>
    static void BringToFront(IntPtr hwnd)
    {
        try
        {
            uint me = GetCurrentThreadId();
            uint other = GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero);
            bool attached = other != 0 && other != me && AttachThreadInput(me, other, 1) != 0;
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
            SetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
            if (attached) AttachThreadInput(me, other, 0);
        }
        catch { }
    }
}

/// <summary>Opens the page in an app-style window: Edge, else Chrome, else the default browser.</summary>
static class Browser
{
    public static void Open(string url)
    {
        foreach (var (exe, folder) in new[] { ("msedge.exe", @"Microsoft\Edge\Application"), ("chrome.exe", @"Google\Chrome\Application") })
        {
            var path = Find(exe, folder);
            if (path == null) continue;
            try
            {
                var psi = new ProcessStartInfo(path) { UseShellExecute = false };
                psi.ArgumentList.Add("--app=" + url);
                psi.ArgumentList.Add("--window-size=1180,900");
                Process.Start(psi)?.Dispose();
                return;
            }
            catch { }
        }
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
    }

    static string? Find(string exe, string folder)
    {
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            try
            {
                using var key = hive.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{exe}");
                if (key?.GetValue(null) is string p && p.Trim().Trim('"') is { Length: > 0 } path && File.Exists(path)) return path;
            }
            catch { }
        }
        var roots = new[]
        {
            Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
            Environment.GetEnvironmentVariable("ProgramFiles"),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        };
        foreach (var root in roots)
        {
            if (string.IsNullOrEmpty(root)) continue;
            var path = Path.Combine(root, folder, exe);
            if (File.Exists(path)) return path;
        }
        return null;
    }
}
