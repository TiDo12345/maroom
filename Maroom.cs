// Maroom – your own rooms on virtual desktops ("my room", "more room", Marea + Room).
// © 2026 Timo Dörsam
// Replacement for Dexpot (until version 1.7 the tool was called "DesktopWechsler").
// - Two Windows virtual desktops
// - Mouse side buttons (back/forward) switch the desktop when the desktop itself
//   (empty area) is active; otherwise they keep their normal function
// - Alt+1 / Alt+2 jump directly to desktop 1 / 2
// - Every desktop has its own desktop icons with their own positions.
//   Explorer does not reload a redirected desktop folder, so
//   %USERPROFILE%\Desktop stays fixed and its contents are swapped on every switch:
//     desktop 1 is stored in %USERPROFILE%\Desktop 1 while desktop 2 is active
//     desktop 2 is stored in %USERPROFILE%\Desktop 2 while desktop 1 is active
//
// Hardening:
// - Swap with journal (state.txt): phase 1 desktop -> storage, phase 2 storage -> desktop.
//   If the program dies halfway, the next start resumes exactly there.
// - Locked files: several attempts; whatever stays behind is recorded with its
//   owner desktop in pending.txt and sorted correctly on the next swap.
// - Mouse hook in its own thread (Windows removes low-level hooks that respond too slowly).
// - Positions are merged instead of overwritten; all files are written atomically.
// - Unhandled errors are logged (log limited to 1 MB).
//
// Build (only .NET Framework 4, ships with Windows; save the file as UTF-8 with BOM):
//   C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /target:winexe /platform:x64 /out:Maroom.exe Maroom.cs Container.cs
// Note: this compiler only supports C# 5, so do not use newer syntax.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("Maroom")]
[assembly: System.Reflection.AssemblyProduct("Maroom")]
[assembly: System.Reflection.AssemblyDescription("Your own rooms on virtual desktops")]
[assembly: System.Reflection.AssemblyCopyright("© 2026 Timo Dörsam")]
[assembly: System.Reflection.AssemblyVersion("2.13.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("2.13.0.0")]

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // Started from a redirected environment (e.g. a terminal inside a Store app)? Then this process would
        // read and write that app's private copy of the data folder and disagree with the real state.txt
        // about which desktop's files are in the desktop folder. Start again via Explorer instead.
        // Nothing may be written before this check (not even the log).
        string container = Container.RedirectedTo();
        if (container != null)
        {
            string error = Container.StartViaExplorer(Container.RealPath(Application.ExecutablePath, container), Container.JoinArgs(args));
            if (error != null)
                MessageBox.Show("Maroom was started from a sandboxed app (" + Path.GetFileName(container) + ") and could not start itself outside of it: "
                    + error + "\n\nPlease start Maroom from the Start menu.", "Maroom", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        LegacyMigration.Run();   // before anything reads the data files
        if (!LegacyMigration.StateIsSafe())
        {
            MaroomApp.Log("zustand.txt could not be migrated, not starting to protect the icon sets");
            MessageBox.Show("Maroom: zustand.txt could not be renamed to state.txt. Maroom does not start, to protect your desktop icons.\n\n%LOCALAPPDATA%\\Maroom", "Maroom", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        L10n.Init();
        // Maroom.exe --log : only open the log window
        if (args.Length > 0 && args[0] == "--log")
        {
            Application.EnableVisualStyles();
            Application.Run(LogWindow.CreateNew(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Maroom", "Maroom.log")));
            return;
        }
        bool fresh;
        Mutex m = new Mutex(true, "Maroom_SingleInstance", out fresh);
        if (!fresh && args.Length > 0 && args[0] == "--restart")
        {
            // After a crash: wait until the old instance is gone
            try { fresh = m.WaitOne(15000); } catch (AbandonedMutexException) { fresh = true; }
        }
        if (!fresh) return;
        // Start and every regular end are logged, so a Maroom that is suddenly gone without a log entry
        // was killed from outside (e.g. together with the process tree it was started from).
        MaroomApp.Log("Start " + Application.ProductVersion + (args.Length > 0 ? " " + string.Join(" ", args) : ""));
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += delegate (object s, ThreadExceptionEventArgs e) { MaroomApp.Log("UI error: " + e.Exception); };
        // No Windows error dialog ("Maroom has stopped working") that keeps the process alive
        Native.SetErrorMode(0x0002 /* SEM_NOGPFAULTERRORBOX */ | 0x0001 /* SEM_FAILCRITICALERRORS */);
        AppDomain.CurrentDomain.UnhandledException += delegate (object s, UnhandledExceptionEventArgs e) {
            MaroomApp.Log("Crash: " + e.ExceptionObject);
            if (!e.IsTerminating) return;
            RestartAfterCrash();
            // Terminate immediately: otherwise error reporting keeps the process (and the single-instance mutex) alive for seconds
            try { Process.GetCurrentProcess().Kill(); } catch { }
        };
        Application.EnableVisualStyles();
        // Only for testing the restart: MAROOM_CRASHTEST=1 makes a background thread crash after 3 s
        if (Environment.GetEnvironmentVariable("MAROOM_CRASHTEST") == "1")
            new Thread(delegate () { Thread.Sleep(3000); throw new InvalidOperationException("Crash test"); }).Start();
        Application.Run(new MaroomApp());
        MaroomApp.Log("End");
        GC.KeepAlive(m);
    }

    // Restart after a crash; at most 3 times in 10 minutes (otherwise endless loop)
    static void RestartAfterCrash()
    {
        try
        {
            string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Maroom");
            string file = Path.Combine(data, "restarts.txt");
            List<string> times = new List<string>();
            if (File.Exists(file))
                foreach (string z in File.ReadAllLines(file))
                {
                    DateTime t;
                    if (DateTime.TryParse(z, null, System.Globalization.DateTimeStyles.RoundtripKind, out t) && DateTime.UtcNow - t < TimeSpan.FromMinutes(10)) times.Add(z);
                }
            if (times.Count >= 3) { MaroomApp.Log("No automatic restart: already 3 crashes within 10 minutes"); return; }
            times.Add(DateTime.UtcNow.ToString("o"));
            File.WriteAllLines(file, times.ToArray());
            MaroomApp.Log("Automatic restart after crash");
            Process.Start(Application.ExecutablePath, "--restart");
        }
        catch { }
    }
}

class MaroomApp : ApplicationContext
{
    // ---------- Configuration ----------
    static readonly string ProfileDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    // The real desktop folder (may be redirected, e.g. by OneDrive folder backup). The storage folders
    // live next to it so that swapping stays a fast rename on the same drive.
    static readonly string DesktopDir = ResolveDesktopDir();
    static readonly string[] StorageDirs = { Path.Combine(StorageParent(), "Desktop 1"), Path.Combine(StorageParent(), "Desktop 2") };

    // Parent of the desktop folder; the profile folder if the desktop is a drive root (e.g. "D:\").
    static string StorageParent()
    {
        string parent = Path.GetDirectoryName(DesktopDir);
        return string.IsNullOrEmpty(parent) ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : parent;
    }
    const int DesktopCount = 2;
    static readonly string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Maroom");
    static readonly string StateFile = Path.Combine(DataDir, "state.txt");
    static readonly string PendingFile = Path.Combine(DataDir, "pending.txt");
    static readonly string LogFile = Path.Combine(DataDir, "Maroom.log");
    static readonly string[] NeverMove = { "desktop.ini", "thumbs.db" };

    // ---------- State ----------
    NotifyIcon tray;
    MessageWindow window;
    MouseHookThread mouse;
    IntPtr currentIconHandle = IntPtr.Zero;
    Thread worker;
    volatile bool stopping;
    volatile int preempted = -1;   // target of a switch triggered by Maroom itself
    DateTime graceUntil = DateTime.MinValue;
    int lastVd = -1;
    volatile bool overlayEnabled = true;
    static readonly string SettingsFile = Path.Combine(DataDir, "settings.txt");
    public const string StopSignalName = "Maroom_Stop";
    EventWaitHandle stopSignal;
    public const string ResetSignalName = "Maroom_Reset";
    EventWaitHandle resetSignal;
    RegisteredWaitHandle stopWait;
    readonly AutoResetEvent wakeSignal = new AutoResetEvent(false);
    readonly object swapLock = new object();
    int loadedSet;          // which set is currently in %USERPROFILE%\Desktop
    int shownIndex = -1;   // number shown in the tray icon

    public MaroomApp()
    {
        Directory.CreateDirectory(DataDir);
        foreach (string o in StorageDirs) Directory.CreateDirectory(o);
        RepairDesktopFolder();
        loadedSet = Journal.Read().Done;

        window = new MessageWindow(this);

        tray = new NotifyIcon();
        ContextMenuStrip menu = new ContextMenuStrip();
        for (int i = 0; i < DesktopCount; i++)
        {
            int target = i;
            menu.Items.Add("Desktop " + (i + 1), null, delegate { SwitchTo(target); });
        }
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(L10n.T("Desktop-Ordner öffnen", "Open desktop folder"), null, delegate { Process.Start("explorer.exe", "\"" + DesktopDir + "\""); });
        menu.Items.Add(L10n.T("Positionen jetzt speichern", "Save icon positions now"), null, delegate { lock (swapLock) SavePositions(loadedSet, true); });
        // Own window instead of Notepad: the Store Notepad cannot find the path and reopens old tabs
        menu.Items.Add(L10n.T("Protokoll anzeigen", "Show log"), null, delegate { LogWindow.Display(LogFile); });
        overlayEnabled = ReadSetting("overlay", "1") == "1";
        ToolStripMenuItem osd = new ToolStripMenuItem(L10n.T("Einblendung beim Wechsel", "Show overlay when switching"));
        osd.CheckOnClick = true;
        osd.Checked = overlayEnabled;
        osd.CheckedChanged += delegate { overlayEnabled = osd.Checked; WriteSetting("overlay", overlayEnabled ? "1" : "0"); };
        menu.Items.Add(osd);
        ToolStripMenuItem fm = new ToolStripMenuItem(L10n.T("Eintrag im Fenstermenü", "Entry in window menu"));
        fm.CheckOnClick = true;
        fm.Checked = ReadSetting("windowmenu", "1") == "1";
        fm.CheckedChanged += delegate {
            WriteSetting("windowmenu", fm.Checked ? "1" : "0");
            if (fm.Checked) StartWindowMenu(); else StopWindowMenu();
        };
        menu.Items.Add(fm);
        // Autostart (HKCU\...\Run\Maroom), the same entry the setup uses
        ToolStripMenuItem autostart = new ToolStripMenuItem(L10n.T("Mit Windows starten", "Start with Windows"));
        autostart.Click += delegate {
            try
            {
                using (RegistryKey run = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                {
                    if (IsAutostartEnabled()) run.DeleteValue("Maroom", false);
                    else run.SetValue("Maroom", "\"" + Application.ExecutablePath + "\"");
                }
                Log(IsAutostartEnabled() ? "Autostart enabled" : "Autostart disabled");
            }
            catch (Exception ex) { Log("Changing autostart failed: " + ex.Message); }
        };
        menu.Opening += delegate { autostart.Checked = IsAutostartEnabled(); };
        menu.Items.Add(autostart);
        menu.Items.Add(LanguageMenu());
        menu.Items.Add(L10n.T("Info", "About"), null, delegate { ShowInfo(); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(L10n.T("Beenden", "Exit"), null, delegate { Log("Exit chosen in the menu"); Shutdown(); });
        tray.ContextMenuStrip = menu;
        tray.MouseClick += delegate (object s, MouseEventArgs e) {
            if (e.Button == MouseButtons.Left) Next(1);
        };
        SetTrayIcon(loadedSet);
        tray.Visible = true;

        EnsureDesktops();

        // Alt+1/2 switch, Alt+Shift+1/2 move, Alt+Shift+0 pin
        VdApi.Init();
        if (ReadSetting("windowmenu", "1") == "1") StartWindowMenu();
        SetHotkeys(true);

        mouse = new MouseHookThread(delegate (int direction) { RunOnUi(delegate { Next(direction); }); });

        SystemEvents.SessionEnding += delegate { Log("Session ending (sign-out or shutdown)"); Halt(); };

        // Clean stop for updates: halt, but do NOT swap back to desktop 1
        // (the new version takes over the state directly from the journal).
        stopSignal = new EventWaitHandle(false, EventResetMode.AutoReset, StopSignalName);
        stopWait = ThreadPool.RegisterWaitForSingleObject(stopSignal, delegate {
            RunOnUi(delegate {
                Log("Stop for update requested");
                Halt();
                tray.Visible = false;
                ExitThread();
            });
        }, null, -1, true);

        // For uninstalling: like "Exit" in the menu, i.e. put desktop 1 back into the desktop folder
        resetSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ResetSignalName);
        ThreadPool.RegisterWaitForSingleObject(resetSignal, delegate {
            RunOnUi(delegate {
                Log("Stop with swap back requested (uninstall)");
                Shutdown();
            });
        }, null, -1, true);

        worker = new Thread(WorkerLoop);
        worker.IsBackground = true;
        worker.Name = "Tausch";
        worker.Start();
    }

    // ---------- Virtual desktops (read from registry) ----------
    const string VdKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops";

    static List<byte[]> DesktopIds()
    {
        List<byte[]> l = new List<byte[]>();
        using (RegistryKey k = Registry.CurrentUser.OpenSubKey(VdKey))
        {
            byte[] ids = k == null ? null : k.GetValue("VirtualDesktopIDs") as byte[];
            if (ids == null) return l;
            for (int i = 0; i + 16 <= ids.Length; i += 16)
            {
                byte[] g = new byte[16];
                Array.Copy(ids, i, g, 0, 16);
                l.Add(g);
            }
        }
        return l;
    }

    static int CurrentDesktop()
    {
        List<byte[]> ids = DesktopIds();
        if (ids.Count <= 1) return 0;
        using (RegistryKey k = Registry.CurrentUser.OpenSubKey(VdKey))
        {
            byte[] cur = k == null ? null : k.GetValue("CurrentVirtualDesktop") as byte[];
            if (cur == null || cur.Length != 16) return -1;
            for (int i = 0; i < ids.Count; i++)
            {
                bool equal = true;
                for (int j = 0; j < 16; j++) if (ids[i][j] != cur[j]) { equal = false; break; }
                if (equal) return i;
            }
        }
        return -1;
    }

    void EnsureDesktops()
    {
        int n = Math.Max(1, DesktopIds().Count);
        if (n >= DesktopCount) return;
        int start = CurrentDesktop();
        for (int i = n; i < DesktopCount; i++) KeyInput.WinCtrl(KeyInput.VK_D);
        Thread.Sleep(300);
        SwitchTo(start < 0 ? 0 : start);
    }

    // ---------- Switching ----------
    public void SwitchTo(int target)
    {
        int cur = CurrentDesktop();
        if (cur < 0 || target == cur) return;
        // Start the swap immediately, not only when Windows reports the switch (~600 ms later).
        // If the switch fails, the worker loop reconciles with the real desktop afterwards.
        preempted = target;
        wakeSignal.Set();
        // With the taskbar focused, it takes the arrow keys of Win+Ctrl+Arrow for its own button navigation
        // and nothing switches. So first move the focus to the desktop and switch with keys as usual (with the
        // slide animation that hides the icon swap). Only if Windows refuses that, switch through the
        // virtual desktop interface (instant, without animation).
        if (IsTaskbar(Native.GetForegroundWindow()) && !FocusDesktop() && VdApi.Available)
        {
            try { VdApi.SwitchTo(target); return; }
            catch (Exception ex) { Log("Direct switch failed, using keys: " + ex.Message); }
        }
        KeyInput.ReleaseAlt();
        int delta = target - cur;
        ushort arrow = delta > 0 ? KeyInput.VK_RIGHT : KeyInput.VK_LEFT;
        for (int i = 0; i < Math.Abs(delta); i++) KeyInput.WinCtrl(arrow);
    }

    // Gives the focus to the desktop window. Windows only lets the process that sent the last input
    // take the foreground, so send a harmless key event first.
    static bool FocusDesktop()
    {
        IntPtr desktop = Native.FindWindow("Progman", null);
        if (desktop == IntPtr.Zero) return false;
        KeyInput.Nudge();
        Native.SetForegroundWindow(desktop);
        for (int i = 0; i < 10; i++)
        {
            if (!IsTaskbar(Native.GetForegroundWindow())) return true;
            Thread.Sleep(10);
        }
        Log("Could not move the focus from the taskbar to the desktop");
        return false;
    }

    static bool IsTaskbar(IntPtr h)
    {
        if (h == IntPtr.Zero) return false;
        IntPtr root = Native.GetAncestor(h, 2 /* GA_ROOT */);
        StringBuilder sb = new StringBuilder(64);
        Native.GetClassName(root != IntPtr.Zero ? root : h, sb, sb.Capacity);
        string c = sb.ToString();
        return c == "Shell_TrayWnd" || c == "Shell_SecondaryTrayWnd";
    }

    void Next(int direction)
    {
        int cur = CurrentDesktop();
        if (cur < 0) return;
        int n = Math.Min(DesktopCount, Math.Max(1, DesktopIds().Count));
        SwitchTo(((cur + direction) % n + n) % n);
    }

    // Submenu "Sprache / Language": changing it saves the setting and restarts Maroom
    ToolStripMenuItem LanguageMenu()
    {
        ToolStripMenuItem m = new ToolStripMenuItem("Sprache / Language");
        string[][] options = {
            new string[] { "auto", L10n.T("Automatisch (wie Windows)", "Automatic (like Windows)") },
            new string[] { "de", "Deutsch" },
            new string[] { "en", "English" } };
        foreach (string[] o in options)
        {
            string code = o[0];
            ToolStripMenuItem mi = new ToolStripMenuItem(o[1]);
            mi.Checked = L10n.Setting == code;
            mi.Click += delegate {
                if (code == L10n.Setting) return;
                WriteSetting("language", code);
                Log("Language set to '" + code + "', restarting");
                Process.Start(Application.ExecutablePath, "--restart");
                Halt();          // without swapping back, the new instance takes over the state
                tray.Visible = false;
                ExitThread();
            };
            m.DropDownItems.Add(mi);
        }
        return m;
    }

    static bool IsAutostartEnabled()
    {
        using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
            return k != null && k.GetValue("Maroom") != null;
    }

    // Register or unregister hotkeys. While a program runs full screen (game, video), they are
    // unregistered so that e.g. Alt+1 reaches the game (like Dexpot's "FullscreenDetection").
    bool hotkeysActive;
    bool lastFullscreen;   // only used in the worker thread
    void SetHotkeys(bool on)
    {
        if (on == hotkeysActive) return;
        hotkeysActive = on;
        for (int i = 0; i < DesktopCount && i < 9; i++)
        {
            RegisterOne(on, 100 + i, Native.MOD_ALT, (uint)('1' + i), "Alt+" + (i + 1));
            RegisterOne(on, 200 + i, Native.MOD_ALT | Native.MOD_SHIFT, (uint)('1' + i), "Alt+Shift+" + (i + 1));
        }
        RegisterOne(on, 300, Native.MOD_ALT | Native.MOD_SHIFT, (uint)'0', "Alt+Shift+0");
    }

    void RegisterOne(bool on, int id, uint mods, uint vk, string name)
    {
        if (!on) { Native.UnregisterHotKey(window.Handle, id); return; }
        if (!Native.RegisterHotKey(window.Handle, id, mods | Native.MOD_NOREPEAT, vk))
            Log("Hotkey " + name + " could not be registered (in use?)");
    }

    public void Hotkey(int id)
    {
        if (id >= 100 && id < 100 + DesktopCount) SwitchTo(id - 100);
        else if (id >= 200 && id < 200 + DesktopCount) MoveWindow(ActiveWindow.Get(), id - 200);
        else if (id == 300) PinWindow(ActiveWindow.Get());
    }

    void MoveWindow(IntPtr h, int target)
    {
        if (h == IntPtr.Zero) { Overlay.Display(L10n.T("Kein Fenster aktiv", "No active window"), -1, DesktopCount); return; }
        try
        {
            if (VdApi.IsPinned(h)) { Overlay.Display(L10n.T("Fenster ist angeheftet", "Window is pinned"), -1, DesktopCount); return; }
            VdApi.MoveToDesktop(h, target);
            Overlay.Display(L10n.T("Fenster → Desktop ", "Window → Desktop ") + (target + 1), target, DesktopCount);
        }
        catch (Exception ex) { ReportFailure(L10n.T("Verschieben", "Move"), ex); }
    }

    void PinWindow(IntPtr h)
    {
        if (h == IntPtr.Zero) { Overlay.Display(L10n.T("Kein Fenster aktiv", "No active window"), -1, DesktopCount); return; }
        try
        {
            bool on = VdApi.TogglePin(h);
            Overlay.Display(on ? L10n.T("Auf allen Desktops", "On all desktops") : L10n.T("Nicht mehr angeheftet", "No longer pinned"), -1, DesktopCount);
        }
        catch (Exception ex) { ReportFailure(L10n.T("Anheften", "Pin"), ex); }
    }

    void ReportFailure(string what, Exception ex)
    {
        Log(what + " failed: " + ex.Message);
        Overlay.Display(what + L10n.T(" nicht möglich", " not possible"), -1, DesktopCount);
    }

    // ---------- Swapping icons (background thread) ----------
    void WorkerLoop()
    {
        // After sign-in, wait until Explorer shows the desktop (max. 60 s)
        for (int i = 0; i < 120 && !stopping && !DesktopIcons.IsReady(); i++) Thread.Sleep(500);

        // Resume an interrupted swap
        try
        {
            Journal.Entry j = Journal.Read();
            if (j.Phase != 0)
            {
                Log("Interrupted swap " + (j.From + 1) + "->" + (j.To + 1) + " in phase " + j.Phase + " is being resumed");
                lock (swapLock) Swap(j.From, j.To, j.Phase);
            }
        }
        catch (Exception ex) { Log("Resume failed: " + ex); }

        while (!stopping)
        {
            try
            {
                // Show the overlay only once Windows reports the new desktop: a window created earlier
                // would still belong to the old desktop and be invisible after the switch.
                ActiveWindow.Remember();
                bool fullscreen = Fullscreen.IsActive();
                if (fullscreen != lastFullscreen)
                {
                    lastFullscreen = fullscreen;
                    bool on = !fullscreen;
                    Log(fullscreen ? "Full screen detected: hotkeys paused" : "Full screen ended: hotkeys active again");
                    RunOnUi(delegate { SetHotkeys(on); });
                }
                int reg = CurrentDesktop();
                if (reg >= 0 && reg != lastVd)
                {
                    if (lastVd >= 0 && overlayEnabled) { int showIndex = reg; RunOnUi(delegate { Overlay.Display("Desktop " + (showIndex + 1), showIndex, DesktopCount); }); }
                    lastVd = reg;
                }

                int vd = preempted;
                if (vd >= 0)
                {
                    preempted = -1;
                    graceUntil = DateTime.UtcNow.AddMilliseconds(2000);
                }
                else
                {
                    vd = reg;
                    // Windows reports a switch triggered by Maroom with a delay: do not swap back for a moment
                    if (vd == loadedSet) graceUntil = DateTime.MinValue;
                    else if (DateTime.UtcNow < graceUntil) vd = -1;
                }
                if (vd >= 0 && vd < DesktopCount && vd != loadedSet)
                    lock (swapLock) { if (!stopping) Swap(loadedSet, vd, 1); }
            }
            catch (Exception ex) { Log(ex.ToString()); Thread.Sleep(2000); }
            wakeSignal.WaitOne(100);
        }
    }

    // call only while holding swapLock
    void Swap(int old, int target, int abPhase)
    {
        if (old == target) { Journal.Done(target); loadedSet = target; return; }
        List<string> failures = new List<string>();

        if (abPhase <= 1)
        {
            if (abPhase == 1 && Journal.Read().Phase == 0)
            {
                // Right after a swap Explorer may still be placing icons that appeared late on default slots;
                // saving now (e.g. when switching back and forth quickly) would store those slots as the layout.
                if (DateTime.UtcNow < settledAt) Log("Positions of desktop " + (old + 1) + " not saved: switched away right after the swap");
                else SavePositions(old);
            }
            Journal.Write(1, old, target);
            MoveOut(old, target, failures);
        }
        Journal.Write(2, old, target);
        MoveIn(target, failures);
        DesktopIcons.Refresh();   // otherwise Explorer collects the changes for ~1.7 s
        Journal.Done(target);
        loadedSet = target;
        RunOnUi(delegate { SetTrayIcon(target); });

        if (failures.Count > 0)
        {
            string text = L10n.T("Nicht verschoben (geöffnet oder gesperrt): ", "Not moved (open or locked): ") + string.Join(", ", failures.ToArray());
            Log("Not moved (open or locked): " + string.Join(", ", failures.ToArray()));   // log is always English
            RunOnUi(delegate { tray.ShowBalloonTip(5000, "Maroom", text.Length > 250 ? text.Substring(0, 250) + "…" : text, ToolTipIcon.Warning); });
        }

        try
        {
            Dictionary<string, Native.POINT> pos = LoadPositions(target);
            if (pos.Count == 0) return;
            WaitUntilStable();
            // Icons whose image loads slowly (e.g. .exe files) appear late and first get a default slot.
            // Re-apply until two passes in a row have nothing left to correct, at most about 5 s.
            int calm = 0;
            for (int i = 0; i < 20 && calm < 2; i++)
            {
                if (DesktopIcons.Apply(pos) == 0) calm++; else calm = 0;
                Thread.Sleep(250);
            }
        }
        finally { settledAt = DateTime.UtcNow.AddMilliseconds(SettleMs); }
    }

    // Positions are only saved once a desktop has been shown for this long (see Swap)
    const int SettleMs = 3000;
    DateTime settledAt = DateTime.MinValue;

    // Phase 1: move everything from the desktop into its owner's storage
    static void MoveOut(int old, int target, List<string> failures)
    {
        Dictionary<string, int> pending = ReadPending();
        Dictionary<string, int> newPending = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (string source in ListEntries(DesktopDir))
        {
            string name = Path.GetFileName(source);
            int owner;
            if (!pending.TryGetValue(name, out owner)) owner = old;
            if (owner == target) continue;   // belongs to the target desktop, stays
            if (!MoveWithRetry(source, StorageDirs[owner]))
            {
                newPending[name] = owner;
                failures.Add(name);
            }
        }
        WritePending(newPending);
    }

    // Phase 2: move the target's storage onto the desktop
    static void MoveIn(int target, List<string> failures)
    {
        foreach (string source in ListEntries(StorageDirs[target]))
        {
            if (!MoveWithRetry(source, DesktopDir))
                failures.Add(Path.GetFileName(source) + " (still in " + StorageDirs[target] + ")");
        }
    }

    static List<string> ListEntries(string dir)
    {
        List<string> l = new List<string>();
        if (!Directory.Exists(dir)) { Directory.CreateDirectory(dir); return l; }
        foreach (string e in Directory.GetFileSystemEntries(dir))
        {
            if (Array.IndexOf(NeverMove, Path.GetFileName(e).ToLowerInvariant()) >= 0) continue;
            l.Add(e);
        }
        return l;
    }

    static bool MoveWithRetry(string source, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        for (int attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                if (!File.Exists(source) && !Directory.Exists(source)) return true; // gone in the meantime
                string target = FreeName(targetDir, Path.GetFileName(source));
                if (Directory.Exists(source)) Directory.Move(source, target);
                else File.Move(source, target);
                return true;
            }
            catch (Exception ex)
            {
                if (attempt == 3) { Log("Move failed: " + source + ": " + ex.Message); return false; }
                Thread.Sleep(250);
            }
        }
        return false;
    }

    static string FreeName(string dir, string name)
    {
        string target = Path.Combine(dir, name);
        if (!File.Exists(target) && !Directory.Exists(target)) return target;
        string baseName = Path.GetFileNameWithoutExtension(name), ext = Path.GetExtension(name);
        for (int i = 2; ; i++)
        {
            target = Path.Combine(dir, baseName + " (" + i + ")" + ext);
            if (!File.Exists(target) && !Directory.Exists(target)) return target;
        }
    }

    static void WaitUntilStable()
    {
        // After the refresh Explorer re-reads the folder; wait until the count is stable.
        int last = -1, stable = 0;
        for (int i = 0; i < 70 && stable < 3; i++)
        {
            Thread.Sleep(30);
            int n = DesktopIcons.Count();
            if (n == last && n > 0) stable++; else { stable = 0; last = n; }
        }
    }

    // ---------- Pending (locked files) ----------
    static Dictionary<string, int> ReadPending()
    {
        Dictionary<string, int> d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(PendingFile)) return d;
            foreach (string z in File.ReadAllLines(PendingFile, Encoding.UTF8))
            {
                string[] t = z.Split(new char[] { '\t' }, 2);
                int b;
                if (t.Length == 2 && int.TryParse(t[0], out b) && b >= 0 && b < DesktopCount) d[t[1]] = b;
            }
        }
        catch (Exception ex) { Log("pending.txt unreadable: " + ex.Message); }
        return d;
    }

    static void WritePending(Dictionary<string, int> d)
    {
        if (d.Count == 0) { if (File.Exists(PendingFile)) File.Delete(PendingFile); return; }
        StringBuilder sb = new StringBuilder();
        foreach (KeyValuePair<string, int> e in d) sb.Append(e.Value).Append('\t').Append(e.Key).Append("\r\n");
        FileUtil.WriteAtomic(PendingFile, sb.ToString());
    }

    // ---------- Positions ----------
    static string PositionsFile(int i) { return Path.Combine(DataDir, "positions-" + (i + 1) + ".txt"); }

    // Merges the currently visible positions with the saved ones.
    // That way an incomplete list (Explorer still loading) does not destroy data.
    static void SavePositions(int setIndex) { SavePositions(setIndex, false); }

    // force = true: menu item "Save icon positions now", without the plausibility check
    static void SavePositions(int setIndex, bool force)
    {
        List<KeyValuePair<string, Native.POINT>> l = DesktopIcons.Read();
        if (l.Count == 0) return;
        string file = PositionsFile(setIndex);
        if (!force && File.Exists(file) && LooksLikeDefaultLayout(l))
        {
            // Explorer lost the layout (e.g. restart, or files moved from outside).
            // Do not save this default layout as intended, otherwise the real one is lost.
            Log("Positions of desktop " + (setIndex + 1) + " not saved: looks like a default layout (" + l.Count + " icons)");
            return;
        }
        Dictionary<string, Native.POINT> d = LoadPositions(setIndex);
        foreach (KeyValuePair<string, Native.POINT> e in l) d[e.Key] = e.Value;
        StringBuilder sb = new StringBuilder();
        foreach (KeyValuePair<string, Native.POINT> e in d)
            sb.Append(e.Value.x).Append('\t').Append(e.Value.y).Append('\t').Append(e.Key).Append("\r\n");
        string fresh = sb.ToString();
        if (File.Exists(file))
        {
            string old = File.ReadAllText(file, Encoding.UTF8);
            if (old == fresh) return;
            BackupPositions(setIndex, old);
        }
        FileUtil.WriteAtomic(file, fresh);
    }

    // Keep the last 20 states per desktop in DataDir\backup
    static void BackupPositions(int setIndex, string inhalt)
    {
        try
        {
            string dir = Path.Combine(DataDir, "backup");
            Directory.CreateDirectory(dir);
            string pattern = "positions-" + (setIndex + 1) + "-";
            File.WriteAllText(Path.Combine(dir, pattern + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".txt"), inhalt, new UTF8Encoding(false));
            string[] total = Directory.GetFiles(dir, pattern + "*.txt");
            Array.Sort(total, StringComparer.Ordinal);
            for (int i = 0; i < total.Length - 20; i++) File.Delete(total[i]);
        }
        catch (Exception ex) { Log("Backup failed: " + ex.Message); }
    }

    // Default layout = almost all icons packed column by column from the top left without gaps.
    // A hand-made layout has gaps and spreads across the screen.
    static bool LooksLikeDefaultLayout(List<KeyValuePair<string, Native.POINT>> l)
    {
        if (l.Count < 6) return false;
        int x0 = int.MaxValue, y0 = int.MaxValue;
        foreach (KeyValuePair<string, Native.POINT> e in l) { x0 = Math.Min(x0, e.Value.x); y0 = Math.Min(y0, e.Value.y); }
        // grid: smallest distance in x and y
        int dx = int.MaxValue, dy = int.MaxValue;
        foreach (KeyValuePair<string, Native.POINT> a in l)
        {
            int ax = a.Value.x - x0, ay = a.Value.y - y0;
            if (ax > 0) dx = Math.Min(dx, ax);
            if (ay > 0) dy = Math.Min(dy, ay);
        }
        if (dy == int.MaxValue) return false;
        int lines = 0;
        foreach (KeyValuePair<string, Native.POINT> e in l) if (e.Value.x == x0) lines++;
        if (lines == 0) return false;
        int n = l.Count, packed = 0;
        HashSet<int> used = new HashSet<int>();
        foreach (KeyValuePair<string, Native.POINT> e in l)
        {
            int rx = e.Value.x - x0, ry = e.Value.y - y0;
            if (ry % dy != 0) continue;
            int column = 0;
            if (rx != 0) { if (dx == int.MaxValue || rx % dx != 0) continue; column = rx / dx; }
            int line = ry / dy;
            if (line >= lines) continue;
            int slot = column * lines + line;
            if (slot < n && used.Add(slot)) packed++;
        }
        return packed * 10 >= n * 9;   // at least 90 % packed without gaps
    }

    static Dictionary<string, Native.POINT> LoadPositions(int setIndex)
    {
        Dictionary<string, Native.POINT> d = new Dictionary<string, Native.POINT>();
        try
        {
            string f = PositionsFile(setIndex);
            if (!File.Exists(f)) return d;
            foreach (string line in File.ReadAllLines(f, Encoding.UTF8))
            {
                string[] t = line.Split(new char[] { '\t' }, 3);
                if (t.Length < 3) continue;
                Native.POINT p;
                if (!int.TryParse(t[0], out p.x) || !int.TryParse(t[1], out p.y)) continue;
                d[t[2]] = p;
            }
        }
        catch (Exception ex) { Log("Positions " + (setIndex + 1) + " unreadable: " + ex.Message); }
        return d;
    }

    static readonly Guid FOLDERID_Desktop = new Guid("B4BFCC3A-DB2C-424C-B029-7FE99A87C641");

    static string KnownDesktopFolder()
    {
        IntPtr p;
        // Guid created here, not taken from the static field: KnownDesktopFolder() runs during static
        // initialization of DesktopDir, before FOLDERID_Desktop (declared further down) is set.
        Guid g = new Guid("B4BFCC3A-DB2C-424C-B029-7FE99A87C641");
        if (Native.SHGetKnownFolderPath(ref g, 0, IntPtr.Zero, out p) != 0) return null;
        string path = Marshal.PtrToStringUni(p);
        Marshal.FreeCoTaskMem(p);
        return path == null ? null : path.TrimEnd('\\');
    }

    static bool IsStorageDir(string path)
    {
        string name = Path.GetFileName(path ?? "");
        return name.Equals("Desktop 1", StringComparison.OrdinalIgnoreCase) || name.Equals("Desktop 2", StringComparison.OrdinalIgnoreCase);
    }

    // Uses the real desktop folder, unless it points to one of our storage folders (left over from version 1).
    static string ResolveDesktopDir()
    {
        string known = KnownDesktopFolder();
        if (string.IsNullOrEmpty(known) || IsStorageDir(known)) return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Desktop");
        return known;
    }

    // Version 1 redirected the desktop folder to a storage folder; undo only that. Any other redirection
    // (e.g. OneDrive folder backup) is the user's choice and stays untouched.
    static void RepairDesktopFolder()
    {
        string actual = KnownDesktopFolder();
        if (actual == null || !IsStorageDir(actual)) return;
        Guid g = FOLDERID_Desktop;
        Native.SHSetKnownFolderPath(ref g, 0, IntPtr.Zero, DesktopDir);
        Log("Desktop folder was redirected to " + actual + " (version 1), reset to " + DesktopDir);
    }

    // ---------- UI ----------
    void RunOnUi(MethodInvoker m) { try { window.BeginInvoke(m); } catch { } }

    void SetTrayIcon(int i)
    {
        if (i == shownIndex) return;
        shownIndex = i;
        using (Bitmap bmp = new Bitmap(32, 32))
        using (Graphics gr = Graphics.FromImage(bmp))
        using (Font f = new Font("Segoe UI", 18, FontStyle.Bold, GraphicsUnit.Pixel))
        using (Brush hg = new SolidBrush(Color.FromArgb(0, 120, 212)))
        {
            gr.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            gr.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            gr.FillRectangle(hg, 2, 2, 28, 28);
            StringFormat sf = new StringFormat();
            sf.Alignment = StringAlignment.Center;
            sf.LineAlignment = StringAlignment.Center;
            gr.DrawString((i + 1).ToString(), f, Brushes.White, new RectangleF(0, 1, 32, 32), sf);
            IntPtr h = bmp.GetHicon();
            tray.Icon = Icon.FromHandle(h);
            if (currentIconHandle != IntPtr.Zero) Native.DestroyIcon(currentIconHandle);
            currentIconHandle = h;
        }
        tray.Text = "Desktop " + (i + 1);
    }

    const string Version = "2.13 (2026-11-20)";

    // ---------- Settings (settings.txt, lines "name=value") ----------
    static string ReadSetting(string name, string fallback)
    {
        try
        {
            if (File.Exists(SettingsFile))
                foreach (string z in File.ReadAllLines(SettingsFile, Encoding.UTF8))
                    if (z.StartsWith(name + "=")) return z.Substring(name.Length + 1).Trim();
        }
        catch { }
        return fallback;
    }

    static void WriteSetting(string name, string val)
    {
        try
        {
            List<string> lines = new List<string>();
            if (File.Exists(SettingsFile))
                foreach (string z in File.ReadAllLines(SettingsFile, Encoding.UTF8))
                    if (!z.StartsWith(name + "=")) lines.Add(z);
            lines.Add(name + "=" + val);
            FileUtil.WriteAtomic(SettingsFile, string.Join("\r\n", lines.ToArray()) + "\r\n");
        }
        catch (Exception ex) { Log("Setting not saved: " + ex.Message); }
    }

    static int CountEntries(string dir)
    {
        try { return Directory.Exists(dir) ? ListEntries(dir).Count : 0; } catch { return -1; }
    }

    void ShowInfo()
    {
        int vd = CurrentDesktop();
        Journal.Entry j = Journal.Read();
        Dictionary<string, int> pending = ReadPending();
        bool autostart = IsAutostartEnabled();

        StringBuilder sb = new StringBuilder();
        sb.AppendLine("Maroom " + Version);
        sb.AppendLine(L10n.T("Eigene Räume auf virtuellen Desktops – my room, more room.", "Your own rooms on virtual desktops – my room, more room."));
        sb.AppendLine("© 2026 Timo Dörsam");
        sb.AppendLine();
        sb.AppendLine(L10n.T("Aktiver Desktop: ", "Active desktop: ") + (vd < 0 ? L10n.T("unbekannt", "unknown") : (vd + 1).ToString()) + L10n.T(" von ", " of ") + Math.Max(1, DesktopIds().Count));
        sb.AppendLine(L10n.T("Im Desktop-Ordner liegt: Satz ", "Desktop folder holds: set ") + (loadedSet + 1) + (j.Phase != 0 ? L10n.T("  (Tausch läuft, Phase ", "  (swap in progress, phase ") + j.Phase + ")" : ""));
        sb.AppendLine();
        sb.AppendLine(L10n.T("Ordner:", "Folders:"));
        sb.AppendLine("  Desktop:    " + DesktopDir + "  (" + CountEntries(DesktopDir) + L10n.T(" Einträge)", " items)"));
        for (int i = 0; i < StorageDirs.Length; i++)
            sb.AppendLine(L10n.T("  Ablage ", "  Storage ") + (i + 1) + ":  " + StorageDirs[i] + "  (" + CountEntries(StorageDirs[i]) + L10n.T(" Einträge)", " items)"));
        sb.AppendLine();
        sb.Append(L10n.T("Gemerkte Positionen:", "Saved positions:"));
        for (int i = 0; i < DesktopCount; i++) sb.Append("  Desktop " + (i + 1) + ": " + LoadPositions(i).Count);
        sb.AppendLine();
        sb.AppendLine(L10n.T("Gesperrte Dateien vorgemerkt: ", "Locked files pending: ") + (pending.Count == 0 ? L10n.T("keine", "none") : string.Join(", ", new List<string>(pending.Keys).ToArray())));
        sb.AppendLine();
        sb.AppendLine(L10n.T("Bedienung:", "Controls:"));
        sb.AppendLine(L10n.T("  Seitentasten Zurück/Vor (Mauszeiger über dem Desktop)", "  Mouse back/forward buttons (pointer over the desktop)"));
        sb.AppendLine(L10n.T("  Alt+1 / Alt+2, Linksklick auf dieses Symbol", "  Alt+1 / Alt+2, left-click this icon"));
        sb.AppendLine(L10n.T("  Aktives Fenster verschieben: Alt+Shift+1 / Alt+Shift+2 oder Fenstermenü", "  Move active window: Alt+Shift+1 / Alt+Shift+2 or window menu"));
        sb.AppendLine(L10n.T("  Fenster auf allen Desktops anzeigen (an/aus): Alt+Shift+0", "  Show window on all desktops (on/off): Alt+Shift+0"));
        sb.AppendLine(L10n.T("  Fenster-Funktionen: ", "  Window features: ") + (VdApi.Available ? L10n.T("verfügbar", "available") : L10n.T("NICHT verfügbar – ", "NOT available – ") + VdApi.LastError));
        sb.AppendLine();
        sb.AppendLine(L10n.T("Autostart: ", "Start with Windows: ") + (autostart ? L10n.T("ja", "yes") : L10n.T("nein", "no")));
        sb.AppendLine(L10n.T("Sprache: ", "Language: ") + L10n.Describe());
        sb.AppendLine(L10n.T("Programm: ", "Program: ") + Application.ExecutablePath);
        sb.AppendLine(L10n.T("Daten und Protokoll: ", "Data and log: ") + DataDir);
        sb.AppendLine(L10n.T("Quelle: ", "Source: ") + "https://github.com/TiDo12345/maroom");
        MessageBox.Show(sb.ToString(), "Maroom – Info", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    // Sign-out/shutdown: only halt cleanly, the state stays in the journal
    WindowMenu windowMenu;

    void StartWindowMenu()
    {
        if (windowMenu != null) return;
        windowMenu = new WindowMenu(DesktopCount,
            delegate (IntPtr h, int target) { RunOnUi(delegate { MoveWindow(h, target); }); },
            delegate (IntPtr h) { RunOnUi(delegate { PinWindow(h); }); },
            CurrentDesktop);
    }

    void StopWindowMenu()
    {
        if (windowMenu == null) return;
        windowMenu.RemoveAll();
        windowMenu = null;
    }

    void Halt()
    {
        // Remove entries from other programs' window menus (only on the UI thread, not on sign-out)
        if (window != null && Thread.CurrentThread.GetApartmentState() == ApartmentState.STA) { try { StopWindowMenu(); } catch { } }
        stopping = true;
        if (worker != null) worker.Join(8000);
        if (mouse != null) mouse.Stop();
    }

    void Shutdown()
    {
        Halt();
        // Put desktop 1 back into the desktop folder so everything is normal without the tool
        lock (swapLock)
        {
            if (loadedSet != 0) { try { Swap(loadedSet, 0, 1); } catch (Exception ex) { Log(ex.ToString()); } }
        }
        tray.Visible = false;
        ExitThread();
    }

    static readonly object logSperre = new object();
    public static void Log(string s)
    {
        lock (logSperre)
        {
            try
            {
                Directory.CreateDirectory(DataDir);
                FileInfo fi = new FileInfo(LogFile);
                if (fi.Exists && fi.Length > 1024 * 1024)
                {
                    string old = LogFile + ".1";
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(LogFile, old);
                }
                File.AppendAllText(LogFile, DateTime.Now.ToString("s") + " " + s + Environment.NewLine);
            }
            catch { }
        }
    }

    // ---------- Journal ----------
    // state.txt: "N" = set N is in the desktop folder (done)
    //              "P A Z" = swap from A to Z is in phase P (1 or 2)
    public static class Journal
    {
        public struct Entry { public int Phase, From, To, Done; }

        public static Entry Read()
        {
            Entry e = new Entry();
            try
            {
                if (!File.Exists(StateFile)) return e;
                string[] t = File.ReadAllText(StateFile).Trim().Split(' ');
                int a, b, c;
                if (t.Length == 1 && int.TryParse(t[0], out a) && a >= 0 && a < DesktopCount) { e.Done = a; return e; }
                if (t.Length == 3 && int.TryParse(t[0], out a) && int.TryParse(t[1], out b) && int.TryParse(t[2], out c)
                    && (a == 1 || a == 2) && b >= 0 && b < DesktopCount && c >= 0 && c < DesktopCount)
                {
                    e.Phase = a; e.From = b; e.To = c; e.Done = b;
                    return e;
                }
                Log("state.txt unknown: " + string.Join(" ", t));
            }
            catch (Exception ex) { Log("state.txt unreadable: " + ex.Message); }
            return e;
        }

        public static void Write(int phase, int old, int target) { FileUtil.WriteAtomic(StateFile, phase + " " + old + " " + target); }
        public static void Done(int setIndex) { FileUtil.WriteAtomic(StateFile, setIndex.ToString()); }
    }
}

static class FileUtil
{
    // Writes to a temporary file first and then replaces; never a half-written file.
    public static void WriteAtomic(string filePath, string inhalt)
    {
        string tmp = filePath + ".tmp";
        File.WriteAllText(tmp, inhalt, new UTF8Encoding(false));
        if (File.Exists(filePath)) File.Replace(tmp, filePath, null);
        else File.Move(tmp, filePath);
    }
}

// Low-level mouse hook in its own thread with its own message loop.
class MouseHookThread
{
    readonly Action<int> onSwitch;
    readonly Thread thread;
    uint threadId;
    IntPtr hook = IntPtr.Zero;
    Native.LowLevelMouseProc proc;
    bool x1Swallowed, x2Swallowed;

    public MouseHookThread(Action<int> onSwitch)
    {
        this.onSwitch = onSwitch;
        thread = new Thread(Run);
        thread.IsBackground = true;
        thread.Name = "Maus";
        thread.Start();
    }

    void Run()
    {
        threadId = Native.GetCurrentThreadId();
        proc = Hook;
        hook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, proc, Native.GetModuleHandle(null), 0);
        if (hook == IntPtr.Zero) { MaroomApp.Log("Mouse hook could not be installed: " + Marshal.GetLastWin32Error()); return; }
        // Windows silently removes low-level hooks that respond too slowly under load.
        // So reinstall it every 10 minutes (thread timer, arrives as WM_TIMER without a window).
        Native.SetTimer(IntPtr.Zero, IntPtr.Zero, 10 * 60 * 1000, IntPtr.Zero);
        Native.MSG m;
        while (Native.GetMessage(out m, IntPtr.Zero, 0, 0) > 0)
        {
            if (m.message == 0x0113 /* WM_TIMER */ && m.hwnd == IntPtr.Zero)
            {
                IntPtr fresh = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, proc, Native.GetModuleHandle(null), 0);
                if (fresh != IntPtr.Zero) { Native.UnhookWindowsHookEx(hook); hook = fresh; }
                else MaroomApp.Log("Renewing mouse hook failed: " + Marshal.GetLastWin32Error());
                continue;
            }
            Native.TranslateMessage(ref m);
            Native.DispatchMessage(ref m);
        }
        Native.UnhookWindowsHookEx(hook);
        hook = IntPtr.Zero;
    }

    public void Stop()
    {
        if (threadId != 0) Native.PostThreadMessage(threadId, 0x0012 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero);
        thread.Join(2000);
    }

    IntPtr Hook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0)
            {
                int msg = wParam.ToInt32();
                if (msg == Native.WM_XBUTTONDOWN || msg == Native.WM_XBUTTONUP)
                {
                    Native.MSLLHOOKSTRUCT info = (Native.MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.MSLLHOOKSTRUCT));
                    int button = (int)((info.mouseData >> 16) & 0xFFFF); // 1 = back, 2 = forward
                    if (msg == Native.WM_XBUTTONDOWN)
                    {
                        if (IsDesktopActive() || IsDesktopAt(info.pt))
                        {
                            if (button == 1) x1Swallowed = true; else x2Swallowed = true;
                            onSwitch(button == 1 ? -1 : 1);
                            return (IntPtr)1;
                        }
                    }
                    else
                    {
                        if (button == 1 && x1Swallowed) { x1Swallowed = false; return (IntPtr)1; }
                        if (button == 2 && x2Swallowed) { x2Swallowed = false; return (IntPtr)1; }
                    }
                }
            }
        }
        catch (Exception ex) { MaroomApp.Log("Mouse hook: " + ex.Message); }
        return Native.CallNextHookEx(hook, nCode, wParam, lParam);
    }

    static bool IsDesktopActive()
    {
        IntPtr fg = Native.GetForegroundWindow();
        return fg == IntPtr.Zero || IsDesktopWindow(fg);
    }

    // The side button is pressed over a free spot of the desktop (or a desktop icon), even if another
    // window is still active: then switch right away instead of needing a click on the desktop first.
    static bool IsDesktopAt(Native.POINT pt)
    {
        IntPtr h = Native.WindowFromPoint(pt);
        if (h == IntPtr.Zero) return false;
        IntPtr root = Native.GetAncestor(h, 2 /* GA_ROOT */);
        return IsDesktopWindow(root != IntPtr.Zero ? root : h);
    }

    static bool IsDesktopWindow(IntPtr h)
    {
        StringBuilder sb = new StringBuilder(64);
        Native.GetClassName(h, sb, sb.Capacity);
        string c = sb.ToString();
        return c == "Progman" || c == "WorkerW";
    }
}

// Reads and sets icon positions in the desktop list view (process explorer.exe).
static class DesktopIcons
{
    const int LVM_GETITEMCOUNT = 0x1004, LVM_GETITEMPOSITION = 0x1010, LVM_GETITEMTEXTW = 0x1073, LVM_SETITEMPOSITION32 = 0x1031;
    const uint SMTO_ABORTIFHUNG = 2;

    static IntPtr FindListView()
    {
        IntPtr d = Native.FindWindowEx(Native.FindWindow("Progman", null), IntPtr.Zero, "SHELLDLL_DefView", null);
        IntPtr w = IntPtr.Zero;
        while (d == IntPtr.Zero && (w = Native.FindWindowEx(IntPtr.Zero, w, "WorkerW", null)) != IntPtr.Zero)
            d = Native.FindWindowEx(w, IntPtr.Zero, "SHELLDLL_DefView", null);
        return d == IntPtr.Zero ? IntPtr.Zero : Native.FindWindowEx(d, IntPtr.Zero, "SysListView32", null);
    }

    // If Explorer hangs, do not hang with it: 1 s timeout per message
    static long Send(IntPtr h, int msg, IntPtr w, IntPtr l)
    {
        UIntPtr r;
        if (Native.SendMessageTimeout(h, msg, w, l, SMTO_ABORTIFHUNG, 1000, out r) == IntPtr.Zero) return -1;
        return (long)r.ToUInt64();
    }

    public static bool IsReady() { return FindListView() != IntPtr.Zero; }

    // Same as F5 on the desktop: Explorer re-reads the folder immediately.
    public static void Refresh()
    {
        IntPtr lv = FindListView();
        if (lv == IntPtr.Zero) return;
        Native.PostMessage(Native.GetParent(lv), 0x0111 /* WM_COMMAND */, (IntPtr)0x7103, IntPtr.Zero);
    }

    public static int Count()
    {
        IntPtr lv = FindListView();
        return lv == IntPtr.Zero ? 0 : (int)Math.Max(0, Send(lv, LVM_GETITEMCOUNT, IntPtr.Zero, IntPtr.Zero));
    }

    // remote memory in explorer.exe: [0..88) LVITEMW, [512..1032) text, [2048..2056) POINT
    delegate void RemoteMemoryAction(IntPtr lv, IntPtr processHandle, IntPtr mem);

    static void WithRemoteMemory(RemoteMemoryAction f)
    {
        IntPtr lv = FindListView();
        if (lv == IntPtr.Zero) return;
        uint pid;
        Native.GetWindowThreadProcessId(lv, out pid);
        IntPtr hp = Native.OpenProcess(0x0038, false, pid);
        if (hp == IntPtr.Zero) return;
        IntPtr mem = Native.VirtualAllocEx(hp, IntPtr.Zero, (IntPtr)4096, 0x3000, 4);
        try { if (mem != IntPtr.Zero) f(lv, hp, mem); }
        finally
        {
            if (mem != IntPtr.Zero) Native.VirtualFreeEx(hp, mem, IntPtr.Zero, 0x8000);
            Native.CloseHandle(hp);
        }
    }

    static string Text(IntPtr lv, IntPtr hp, IntPtr mem, int i)
    {
        IntPtr x;
        byte[] it = new byte[88];
        BitConverter.GetBytes(1).CopyTo(it, 0);                       // mask = LVIF_TEXT
        BitConverter.GetBytes(i).CopyTo(it, 4);                       // iItem
        BitConverter.GetBytes((long)mem + 512).CopyTo(it, 24);        // pszText
        BitConverter.GetBytes(260).CopyTo(it, 32);                    // cchTextMax
        byte[] empty = new byte[520];
        Native.WriteProcessMemory(hp, (IntPtr)((long)mem + 512), empty, (IntPtr)empty.Length, out x);
        Native.WriteProcessMemory(hp, mem, it, (IntPtr)it.Length, out x);
        if (Send(lv, LVM_GETITEMTEXTW, (IntPtr)i, mem) < 0) return null;
        byte[] t = new byte[520];
        if (!Native.ReadProcessMemory(hp, (IntPtr)((long)mem + 512), t, (IntPtr)t.Length, out x)) return null;
        string s = Encoding.Unicode.GetString(t);
        int nul = s.IndexOf('\0');
        if (nul >= 0) s = s.Substring(0, nul);
        return s.Length == 0 ? null : s;
    }

    public static List<KeyValuePair<string, Native.POINT>> Read()
    {
        List<KeyValuePair<string, Native.POINT>> r = new List<KeyValuePair<string, Native.POINT>>();
        WithRemoteMemory(delegate (IntPtr lv, IntPtr hp, IntPtr mem) {
            long n = Send(lv, LVM_GETITEMCOUNT, IntPtr.Zero, IntPtr.Zero);
            IntPtr x;
            for (int i = 0; i < n; i++)
            {
                string s = Text(lv, hp, mem, i);
                if (s == null) continue;
                if (Send(lv, LVM_GETITEMPOSITION, (IntPtr)i, (IntPtr)((long)mem + 2048)) <= 0) continue;
                byte[] pt = new byte[8];
                if (!Native.ReadProcessMemory(hp, (IntPtr)((long)mem + 2048), pt, (IntPtr)8, out x)) continue;
                Native.POINT p;
                p.x = BitConverter.ToInt32(pt, 0);
                p.y = BitConverter.ToInt32(pt, 4);
                r.Add(new KeyValuePair<string, Native.POINT>(s, p));
            }
        });
        return r;
    }

    // Virtual screen in physical pixels, relative to the primary monitor (= desktop list view origin),
    // minus one icon cell at the right and bottom edge. Empty if it cannot be determined.
    static Rectangle PhysicalVirtualScreen()
    {
        IntPtr previous = IntPtr.Zero;
        try
        {
            previous = Native.SetThreadDpiAwarenessContext((IntPtr)(-4) /* PER_MONITOR_AWARE_V2 */);
            if (previous == IntPtr.Zero) return Rectangle.Empty;
            int x = Native.GetSystemMetrics(76), y = Native.GetSystemMetrics(77);       // SM_X/YVIRTUALSCREEN
            int w = Native.GetSystemMetrics(78), h = Native.GetSystemMetrics(79);       // SM_CX/CYVIRTUALSCREEN
            if (w <= 64 || h <= 64) return Rectangle.Empty;
            return new Rectangle(x, y, w - 32, h - 32);
        }
        catch { return Rectangle.Empty; }
        finally { if (previous != IntPtr.Zero) Native.SetThreadDpiAwarenessContext(previous); }
    }

    // Returns how many icons had to be moved.
    public static int Apply(Dictionary<string, Native.POINT> wanted)
    {
        // Only positions that are visible with the current monitor layout; others (e.g. after unplugging
        // a monitor or changing the resolution) are left to Explorer, otherwise the icon would be invisible.
        // Icon positions are physical pixels, but this process is not DPI aware and would get scaled
        // screen sizes (e.g. 4096x1152 instead of 5120x1440 at 125 %). So measure with a per-monitor
        // DPI aware thread context. If that is not possible, do not filter at all.
        Rectangle visible = PhysicalVirtualScreen();
        bool check = visible.Width > 0 && visible.Height > 0;
        int skipped = 0, moved = 0;
        WithRemoteMemory(delegate (IntPtr lv, IntPtr hp, IntPtr mem) {
            long n = Send(lv, LVM_GETITEMCOUNT, IntPtr.Zero, IntPtr.Zero);
            IntPtr x;
            for (int i = 0; i < n; i++)
            {
                string s = Text(lv, hp, mem, i);
                Native.POINT p;
                if (s == null || !wanted.TryGetValue(s, out p)) continue;
                if (check && !visible.Contains(p.x, p.y)) { skipped++; continue; }
                // already in place? (LVM_GETITEMPOSITION)
                byte[] cur = new byte[8];
                if (Send(lv, LVM_GETITEMPOSITION, (IntPtr)i, (IntPtr)((long)mem + 2048)) > 0
                    && Native.ReadProcessMemory(hp, (IntPtr)((long)mem + 2048), cur, (IntPtr)8, out x)
                    && BitConverter.ToInt32(cur, 0) == p.x && BitConverter.ToInt32(cur, 4) == p.y) continue;
                moved++;
                byte[] pt = new byte[8];
                BitConverter.GetBytes(p.x).CopyTo(pt, 0);
                BitConverter.GetBytes(p.y).CopyTo(pt, 4);
                Native.WriteProcessMemory(hp, (IntPtr)((long)mem + 2048), pt, (IntPtr)8, out x);
                Send(lv, LVM_SETITEMPOSITION32, (IntPtr)i, (IntPtr)((long)mem + 2048));
            }
        });
        if (skipped > 0) MaroomApp.Log(skipped + " icon position(s) outside the current screen area, left to Explorer");
        return moved;
    }
}

// ---------- Moving and pinning windows ----------
// Windows has no official API for this for other programs' windows. Maroom uses the
// internal COM interfaces of Explorer (as of Windows 11 24H2, source of the IDs:
// github.com/MScholtes/VirtualDesktop, VirtualDesktop11-24H2.cs). Microsoft changes them
// occasionally in major updates; then Init() fails and only this feature is off.
// Only the leading interface methods that are actually needed are declared.
static class VdApi
{
    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIInspectable), Guid("372E1D3B-38D3-42E4-A15B-8AB2B178F513")]
    public interface IApplicationView
    {
        [PreserveSig] int SetFocus();
        [PreserveSig] int SwitchTo();
        [PreserveSig] int TryInvokeBack(IntPtr callback);
        [PreserveSig] int GetThumbnailWindow(out IntPtr hwnd);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("1841C6D7-4F9D-42C0-AF41-8747538F10E5")]
    interface IApplicationViewCollection
    {
        int GetViews(out IObjectArray array);
        [PreserveSig] int GetViewsByZOrder(out IObjectArray array);
        int GetViewsByAppUserModelId(string id, out IObjectArray array);
        [PreserveSig] int GetViewForHwnd(IntPtr hwnd, out IApplicationView view);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("3F07F4BE-B107-441A-AF0F-39D82529072C")]
    interface IVirtualDesktop { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("53F5CA0B-158F-4124-900C-057158060B27")]
    interface IVirtualDesktopManagerInternal
    {
        int GetCount();
        void MoveViewToDesktop(IApplicationView view, IVirtualDesktop desktop);
        bool CanViewMoveDesktops(IApplicationView view);
        IVirtualDesktop GetCurrentDesktop();
        void GetDesktops(out IObjectArray desktops);
        [PreserveSig] int GetAdjacentDesktop(IVirtualDesktop from, int direction, out IVirtualDesktop desktop);
        void SwitchDesktop(IVirtualDesktop desktop);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("4CE81583-1E4C-4632-A621-07A53543148F")]
    interface IVirtualDesktopPinnedApps
    {
        bool IsAppIdPinned(string appId);
        void PinAppID(string appId);
        void UnpinAppID(string appId);
        bool IsViewPinned(IApplicationView view);
        void PinView(IApplicationView view);
        void UnpinView(IApplicationView view);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("92CA9DCD-5622-4BBA-A805-5E9F541BD8C9")]
    interface IObjectArray
    {
        void GetCount(out int count);
        void GetAt(int index, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object obj);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
    interface IServiceProvider10
    {
        [return: MarshalAs(UnmanagedType.IUnknown)]
        object QueryService(ref Guid service, ref Guid riid);
    }

    static readonly Guid CLSID_ImmersiveShell = new Guid("C2F03A33-21F5-47FA-B4BB-156362A2F239");
    static readonly Guid CLSID_VirtualDesktopManagerInternal = new Guid("C5E0CDCA-7B6E-41B2-9FC4-D93975CC467B");
    static readonly Guid CLSID_VirtualDesktopPinnedApps = new Guid("B5A399E7-1C87-46B8-88E9-FC5747B171BD");

    static IVirtualDesktopManagerInternal mgr;
    static IApplicationViewCollection views;
    static IVirtualDesktopPinnedApps pins;

    public static bool Available { get; private set; }
    public static string LastError { get; private set; }

    // Call only on the UI thread (STA).
    public static void Init()
    {
        Available = false;
        try
        {
            IServiceProvider10 shell = (IServiceProvider10)Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_ImmersiveShell));
            Guid s = CLSID_VirtualDesktopManagerInternal, i = typeof(IVirtualDesktopManagerInternal).GUID;
            mgr = (IVirtualDesktopManagerInternal)shell.QueryService(ref s, ref i);
            s = typeof(IApplicationViewCollection).GUID; i = typeof(IApplicationViewCollection).GUID;
            views = (IApplicationViewCollection)shell.QueryService(ref s, ref i);
            s = CLSID_VirtualDesktopPinnedApps; i = typeof(IVirtualDesktopPinnedApps).GUID;
            pins = (IVirtualDesktopPinnedApps)shell.QueryService(ref s, ref i);
            // Probe: do the IDs and method order match this Windows build?
            int n = mgr.GetCount();
            if (n < 1 || n > 50) throw new Exception("implausible desktop count " + n);
            Desktop(0);
            Available = true;
            LastError = null;
        }
        catch (Exception ex)
        {
            LastError = ex.GetType().Name + ": " + ex.Message;
            MaroomApp.Log("Moving/pinning windows not available: " + LastError);
        }
    }

    static IVirtualDesktop Desktop(int index)
    {
        IObjectArray a;
        mgr.GetDesktops(out a);
        try
        {
            object o;
            Guid g = typeof(IVirtualDesktop).GUID;
            a.GetAt(index, ref g, out o);
            return (IVirtualDesktop)o;
        }
        finally { Marshal.ReleaseComObject(a); }
    }

    // Finds the Windows "view" for a window. Some programs (e.g. Edge with its self-drawn
    // window menu) report an internal window; then keep searching via owner and process.
    static IApplicationView View(IntPtr hwnd)
    {
        IApplicationView v;
        List<IntPtr> candidates = new List<IntPtr>();
        candidates.Add(hwnd);
        candidates.Add(Native.GetAncestor(hwnd, 2 /* GA_ROOT */));
        candidates.Add(Native.GetAncestor(hwnd, 3 /* GA_ROOTOWNER */));
        IntPtr o = hwnd;
        for (int i = 0; i < 5 && (o = Native.GetWindow(o, 4 /* GW_OWNER */)) != IntPtr.Zero; i++) candidates.Add(o);
        int hr = 0;
        foreach (IntPtr k in candidates)
        {
            if (k == IntPtr.Zero) continue;
            hr = views.GetViewForHwnd(k, out v);
            if (hr == 0 && v != null) return v;
        }
        // Last resort: topmost visible view of the same process
        uint pid;
        Native.GetWindowThreadProcessId(hwnd, out pid);
        IObjectArray a;
        if (pid != 0 && views.GetViewsByZOrder(out a) == 0 && a != null)
        {
            try
            {
                int n;
                a.GetCount(out n);
                Guid iid = typeof(IApplicationView).GUID;
                for (int i = 0; i < n; i++)
                {
                    object obj;
                    a.GetAt(i, ref iid, out obj);
                    IApplicationView kv = obj as IApplicationView;
                    IntPtr w;
                    if (kv == null || kv.GetThumbnailWindow(out w) != 0 || w == IntPtr.Zero || !Native.IsWindowVisible(w)) continue;
                    uint wp;
                    Native.GetWindowThreadProcessId(w, out wp);
                    if (wp == pid) return kv;
                }
            }
            finally { Marshal.ReleaseComObject(a); }
        }
        StringBuilder sb = new StringBuilder(80);
        Native.GetClassName(hwnd, sb, sb.Capacity);
        throw new InvalidOperationException("Window is not managed by Windows (0x" + hr.ToString("X8") + ", class " + sb + ")");
    }

    // Runs the call; after an Explorer restart the COM objects are dead: reconnect once and retry.
    static T WithReconnect<T>(Func<T> f)
    {
        if (!Available) throw new InvalidOperationException("not available: " + LastError);
        try { return f(); }
        catch (COMException)
        {
            Init();
            if (!Available) throw;
            return f();
        }
    }

    public static int Count() { return WithReconnect<int>(delegate { return mgr.GetCount(); }); }

    // Switches without simulated keys (no slide animation).
    public static void SwitchTo(int index)
    {
        WithReconnect<int>(delegate { mgr.SwitchDesktop(Desktop(index)); return 0; });
    }

    public static void MoveToDesktop(IntPtr hwnd, int index)
    {
        WithReconnect<int>(delegate {
            IApplicationView v = View(hwnd);
            if (!mgr.CanViewMoveDesktops(v)) throw new InvalidOperationException("This window cannot be moved");
            mgr.MoveViewToDesktop(v, Desktop(index));
            return 0;
        });
    }

    public static bool IsPinned(IntPtr hwnd) { return WithReconnect<bool>(delegate { return pins.IsViewPinned(View(hwnd)); }); }

    // Returns the new state
    public static bool TogglePin(IntPtr hwnd)
    {
        return WithReconnect<bool>(delegate {
            IApplicationView v = View(hwnd);
            if (pins.IsViewPinned(v)) { pins.UnpinView(v); return false; }
            pins.PinView(v);
            return true;
        });
    }
}

// Submenu "Maroom ▸ To desktop N / Show on all desktops" in other programs' window menus
// (right-click on the title bar, Alt+Space). Like Dexpot, but WITHOUT DLL injection:
// - the entry is added from outside via GetSystemMenu/AppendMenu as soon as a window becomes active
// - the selection is detected via WinEvents (EVENT_OBJECT_INVOKED, idChild = command ID),
//   the window via EVENT_SYSTEM_MENUSTART. The window itself receives WM_SYSCOMMAND
//   with an unknown ID, which DefWindowProc ignores.
// - All entries are removed again on exit.
// Edge and Chrome show the native window menu in their own menu window;
// the target window is then resolved via the last active window.
class WindowMenu
{
    const uint ID_DESKTOP = 0xD7A0;   // + 0x10 per desktop (the system uses the lower 4 bits)
    const uint ID_ANHEFTEN = 0xD7F0;
    const string MenuLabel = "Maroom";
    const uint EVENT_SYSTEM_FOREGROUND = 3, EVENT_SYSTEM_MENUSTART = 4, EVENT_SYSTEM_MENUEND = 5, EVENT_OBJECT_INVOKED = 0x8013;
    const int OBJID_SYSMENU = -1, OBJID_MENU = -3;
    const uint MF_BYPOSITION = 0x400, MF_SEPARATOR = 0x800, MF_POPUP = 0x10, MF_GRAYED = 1, MF_CHECKED = 8;

    readonly int count;
    readonly Action<IntPtr, int> moveAction;
    readonly Action<IntPtr> pinAction;
    readonly Func<int> currentDesktop;
    readonly Native.WinEventProc proc;
    readonly List<IntPtr> hooks = new List<IntPtr>();
    const int TRENNER_DAVOR = 1, TRENNER_DANACH = 2;
    const uint SC_CLOSE = 0xF060;
    readonly Dictionary<IntPtr, int> patched = new Dictionary<IntPtr, int>();   // hwnd -> separator inserted by Maroom
    IntPtr menuWindow = IntPtr.Zero;
    DateTime menuValidUntil = DateTime.MinValue;   // open window menu (+1 s tolerance after MENUEND)

    public WindowMenu(int count, Action<IntPtr, int> moveAction, Action<IntPtr> pinAction, Func<int> currentDesktop)
    {
        this.count = count;
        this.moveAction = moveAction;
        this.pinAction = pinAction;
        this.currentDesktop = currentDesktop;
        proc = OnWinEvent;
        foreach (uint[] ev in new uint[][] { new uint[] { EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND },
                                             new uint[] { EVENT_SYSTEM_MENUSTART, EVENT_SYSTEM_MENUEND },
                                             new uint[] { EVENT_OBJECT_INVOKED, EVENT_OBJECT_INVOKED } })
        {
            IntPtr h = Native.SetWinEventHook(ev[0], ev[1], IntPtr.Zero, proc, 0, 0, 0 /* WINEVENT_OUTOFCONTEXT */);
            if (h == IntPtr.Zero) MaroomApp.Log("WinEvent-Hook 0x" + ev[0].ToString("X") + " failed");
            else hooks.Add(h);
        }
        AddToWindow(Native.GetForegroundWindow());
    }

    void OnWinEvent(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        try
        {
            if (ev == EVENT_SYSTEM_FOREGROUND) AddToWindow(hwnd);
            else if (ev == EVENT_SYSTEM_MENUEND)
            {
                DateTime until = DateTime.UtcNow.AddSeconds(1);   // some programs report the end before the click
                if (until < menuValidUntil) menuValidUntil = until;
            }
            else if (ev == EVENT_SYSTEM_MENUSTART)
            {
                // menu bars of programs (File, Edit …) are never the window menu
                if (idObject == OBJID_MENU) { menuValidUntil = DateTime.MinValue; return; }
                menuValidUntil = DateTime.UtcNow.AddMinutes(5);
                // Edge & co. show their window menu in a separate menu window: then use the last active window
                menuWindow = ActiveWindow.Normalize(hwnd);
                if (menuWindow == IntPtr.Zero) menuWindow = ActiveWindow.Last();
                if (menuWindow != IntPtr.Zero) UpdateMenuState(menuWindow);
            }
            else if (ev == EVENT_OBJECT_INVOKED)
            {
                // Only clicks in a currently open window menu count. Another program could use the same
                // command ID in its own menu; that must not move any window.
                if (DateTime.UtcNow > menuValidUntil) return;
                // Chromium/Electron windows with their own title bar (e.g. the Claude or Teams app) show the
                // window menu with TrackPopupMenu and report the click as OBJID_MENU. Accept that only from
                // the process whose window menu is open, never from another program's menu bar.
                if (idObject == OBJID_MENU && !SameProcess(hwnd, menuWindow != IntPtr.Zero ? menuWindow : ActiveWindow.Last())) return;
                menuValidUntil = DateTime.MinValue;
                uint id = (uint)idChild;
                if (id != ID_ANHEFTEN && (id < ID_DESKTOP || id >= ID_DESKTOP + 0x10 * (uint)count)) return;
                if (Native.GetSystemMenu(menuWindow != IntPtr.Zero ? menuWindow : ActiveWindow.Last(), false) == IntPtr.Zero) return;
                IntPtr target = menuWindow != IntPtr.Zero ? menuWindow : ActiveWindow.Last();
                if (target == IntPtr.Zero) return;
                if (id == ID_ANHEFTEN) pinAction(target);
                else if (id >= ID_DESKTOP && id < ID_DESKTOP + 0x10 * (uint)count && (id - ID_DESKTOP) % 0x10 == 0)
                    moveAction(target, (int)((id - ID_DESKTOP) / 0x10));
            }
        }
        catch (Exception ex) { MaroomApp.Log("Window menu: " + ex.Message); }
    }

    static bool SameProcess(IntPtr a, IntPtr b)
    {
        if (a == IntPtr.Zero || b == IntPtr.Zero) return false;
        uint pa, pb;
        Native.GetWindowThreadProcessId(a, out pa);
        Native.GetWindowThreadProcessId(b, out pb);
        return pa != 0 && pa == pb;
    }

    static int Position(IntPtr sys)
    {
        int n = Native.GetMenuItemCount(sys);
        StringBuilder sb = new StringBuilder(64);
        for (int i = 0; i < n; i++)
        {
            sb.Length = 0;
            Native.GetMenuString(sys, (uint)i, sb, sb.Capacity, MF_BYPOSITION);
            if (sb.ToString() == MenuLabel) return i;
        }
        return -1;
    }

    void AddToWindow(IntPtr hwnd)
    {
        IntPtr h = ActiveWindow.Normalize(hwnd);
        if (h == IntPtr.Zero) return;
        IntPtr sys = Native.GetSystemMenu(h, false);
        if (sys == IntPtr.Zero) return;
        RemoveDexpotLeftovers(sys);
        if (Position(sys) >= 0) return;

        IntPtr popup = Native.CreatePopupMenu();
        for (int i = 0; i < count; i++)
            Native.AppendMenu(popup, 0, (UIntPtr)(ID_DESKTOP + 0x10 * (uint)i), L10n.T("Auf Desktop ", "To desktop ") + (i + 1) + "\tAlt+Shift+" + (i + 1));
        Native.AppendMenu(popup, MF_SEPARATOR, UIntPtr.Zero, null);
        Native.AppendMenu(popup, 0, (UIntPtr)ID_ANHEFTEN, L10n.T("Auf allen Desktops anzeigen", "Show on all desktops") + "\tAlt+Shift+0");

        // Position: directly above "Close", set off by separators ( … | Maroom ▸ | Close )
        int n = Native.GetMenuItemCount(sys), closeIndex = -1;
        for (int i = 0; i < n; i++) if (Native.GetMenuItemID(sys, i) == SC_CLOSE) { closeIndex = i; break; }
        UIntPtr pop = (UIntPtr)(ulong)popup.ToInt64();
        int separator;
        bool ok;
        if (closeIndex > 0 && (Native.GetMenuState(sys, (uint)(closeIndex - 1), MF_BYPOSITION) & MF_SEPARATOR) != 0)
        {
            ok = Native.InsertMenu(sys, (uint)closeIndex, MF_BYPOSITION | MF_POPUP, pop, MenuLabel)
                 && Native.InsertMenu(sys, (uint)closeIndex + 1, MF_BYPOSITION | MF_SEPARATOR, UIntPtr.Zero, null);
            separator = TRENNER_DANACH;
        }
        else
        {
            bool needSep = n > 0 && (Native.GetMenuState(sys, (uint)(n - 1), MF_BYPOSITION) & MF_SEPARATOR) == 0;
            if (needSep) Native.AppendMenu(sys, MF_SEPARATOR, UIntPtr.Zero, null);
            ok = Native.AppendMenu(sys, MF_POPUP, pop, MenuLabel);
            separator = needSep ? TRENNER_DAVOR : 0;
        }
        if (!ok)
        {
            Native.DestroyMenu(popup);
            return;
        }
        patched[h] = separator;
        if (patched.Count > 200) Prune();
    }

    // Dexpot put a "Dexpot" entry into every window menu via DLL injection. After its
    // uninstallation it stays in programs that are still running, without function: remove it,
    // together with a separator that becomes doubled.
    static void RemoveDexpotLeftovers(IntPtr sys)
    {
        StringBuilder sb = new StringBuilder(64);
        for (int i = Native.GetMenuItemCount(sys) - 1; i >= 0; i--)
        {
            sb.Length = 0;
            Native.GetMenuString(sys, (uint)i, sb, sb.Capacity, MF_BYPOSITION);
            if (!sb.ToString().Replace("&", "").Equals("Dexpot", StringComparison.OrdinalIgnoreCase)) continue;
            Native.RemoveMenu(sys, (uint)i, MF_BYPOSITION);
            int n = Native.GetMenuItemCount(sys);
            bool sepBefore = i > 0 && (Native.GetMenuState(sys, (uint)(i - 1), MF_BYPOSITION) & MF_SEPARATOR) != 0;
            bool sepAfter = i < n && (Native.GetMenuState(sys, (uint)i, MF_BYPOSITION) & MF_SEPARATOR) != 0;
            if (sepBefore && sepAfter) Native.DeleteMenu(sys, (uint)i, MF_BYPOSITION);
        }
    }

    void UpdateMenuState(IntPtr h)
    {
        IntPtr sys = Native.GetSystemMenu(h, false);
        int pos = sys == IntPtr.Zero ? -1 : Position(sys);
        if (pos < 0) return;
        IntPtr popup = Native.GetSubMenu(sys, pos);
        bool pinned = false;
        try { pinned = VdApi.Available && VdApi.IsPinned(h); } catch { }
        int vd = currentDesktop();
        for (int i = 0; i < count; i++)
            Native.EnableMenuItem(popup, ID_DESKTOP + 0x10 * (uint)i, (!VdApi.Available || pinned || i == vd) ? MF_GRAYED : 0);
        Native.EnableMenuItem(popup, ID_ANHEFTEN, VdApi.Available ? 0 : MF_GRAYED);
        Native.CheckMenuItem(popup, ID_ANHEFTEN, pinned ? MF_CHECKED : 0);
    }

    void Prune()
    {
        List<IntPtr> dead = new List<IntPtr>();
        foreach (IntPtr h in patched.Keys) if (!Native.IsWindow(h)) dead.Add(h);
        foreach (IntPtr h in dead) patched.Remove(h);
    }

    // Remove all entries (on exit)
    public void RemoveAll()
    {
        foreach (IntPtr h in hooks) Native.UnhookWinEvent(h);
        hooks.Clear();
        foreach (KeyValuePair<IntPtr, int> e in patched)
        {
            try
            {
                if (!Native.IsWindow(e.Key)) continue;
                IntPtr sys = Native.GetSystemMenu(e.Key, false);
                int pos = sys == IntPtr.Zero ? -1 : Position(sys);
                if (pos < 0) continue;
                IntPtr popup = Native.GetSubMenu(sys, pos);
                Native.RemoveMenu(sys, (uint)pos, MF_BYPOSITION);
                if (popup != IntPtr.Zero) Native.DestroyMenu(popup);
                // remove our own separator (after the removal the following item moves up to "pos")
                if (e.Value == TRENNER_DANACH && (Native.GetMenuState(sys, (uint)pos, MF_BYPOSITION) & MF_SEPARATOR) != 0)
                    Native.DeleteMenu(sys, (uint)pos, MF_BYPOSITION);
                else if (e.Value == TRENNER_DAVOR && pos > 0 && (Native.GetMenuState(sys, (uint)(pos - 1), MF_BYPOSITION) & MF_SEPARATOR) != 0)
                    Native.DeleteMenu(sys, (uint)(pos - 1), MF_BYPOSITION);
            }
            catch { }
        }
        patched.Clear();
    }
}

// UI language: German or English. Setting "language" in settings.txt:
// auto (default: German if Windows runs in German, otherwise English), de or en.
// Log messages are English (diagnostics).
static class L10n
{
    public static bool En { get; private set; }
    public static string Setting { get; private set; }

    public static void Init()
    {
        Setting = "auto";
        try
        {
            string f = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Maroom", "settings.txt");
            if (File.Exists(f))
                foreach (string z in File.ReadAllLines(f, Encoding.UTF8))
                    if (z.StartsWith("language=")) Setting = z.Substring(9).Trim().ToLowerInvariant();
        }
        catch { }
        if (Setting == "de") En = false;
        else if (Setting == "en") En = true;
        else { Setting = "auto"; En = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName != "de"; }
    }

    public static string T(string de, string en) { return En ? en : de; }

    public static string Describe()
    {
        string language = En ? "English" : "Deutsch";
        return Setting == "auto"
            ? language + T(" (automatisch, Windows: ", " (automatic, Windows: ") + System.Globalization.CultureInfo.CurrentUICulture.Name + ")"
            : language;
    }
}

// Detects a program running full screen (game, video, presentation) in the foreground.
static class Fullscreen
{
    public static bool IsActive()
    {
        IntPtr fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero || ActiveWindow.Normalize(fg) == IntPtr.Zero) return false;   // desktop, taskbar, own windows
        Native.RECT r;
        if (!Native.GetWindowRect(fg, out r)) return false;
        IntPtr mon = Native.MonitorFromWindow(fg, 2 /* MONITOR_DEFAULTTONEAREST */);
        Native.MONITORINFO mi = new Native.MONITORINFO();
        mi.cbSize = Marshal.SizeOf(typeof(Native.MONITORINFO));
        if (!Native.GetMonitorInfo(mon, ref mi)) return false;
        // Full screen = covers the whole monitor including the taskbar area (maximized windows do not)
        return r.left <= mi.rcMonitor.left && r.top <= mi.rcMonitor.top && r.right >= mi.rcMonitor.right && r.bottom >= mi.rcMonitor.bottom;
    }
}

// Remembers the last active normal window (fallback for the window menu).
static class ActiveWindow
{
    static IntPtr lastWindow = IntPtr.Zero;
    static readonly uint OwnPid = (uint)Process.GetCurrentProcess().Id;
    static readonly string[] ExcludedClasses = {
        "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Progman", "WorkerW", "NotifyIconOverflowWindow",
        "TopLevelWindowForOverflowXamlIsland", "XamlExplorerHostIslandWindow", "Windows.UI.Core.CoreWindow",
        "MultitaskingViewFrame", "ForegroundStaging", "#32768"
    };

    public static IntPtr Normalize(IntPtr h)
    {
        if (h == IntPtr.Zero || !Native.IsWindowVisible(h)) return IntPtr.Zero;
        IntPtr root = Native.GetAncestor(h, 3 /* GA_ROOTOWNER */);
        if (root != IntPtr.Zero) h = root;
        uint pid;
        Native.GetWindowThreadProcessId(h, out pid);
        if (pid == OwnPid) return IntPtr.Zero;
        // helper windows (menus, tooltips, popups of Edge & co.) are no targets
        if ((Native.GetWindowLong(h, -20 /* GWL_EXSTYLE */) & (0x80 /* TOOLWINDOW */ | 0x08000000 /* NOACTIVATE */)) != 0) return IntPtr.Zero;
        StringBuilder sb = new StringBuilder(128);
        Native.GetClassName(h, sb, sb.Capacity);
        if (Array.IndexOf(ExcludedClasses, sb.ToString()) >= 0) return IntPtr.Zero;
        return h;
    }

    // call regularly from the worker loop
    public static void Remember()
    {
        IntPtr h = Normalize(Native.GetForegroundWindow());
        if (h != IntPtr.Zero) lastWindow = h;
    }

    // for hotkeys: foreground window, otherwise the last remembered one
    public static IntPtr Get()
    {
        IntPtr h = Normalize(Native.GetForegroundWindow());
        if (h != IntPtr.Zero) return h;
        return Native.IsWindow(lastWindow) ? lastWindow : IntPtr.Zero;
    }

    public static IntPtr Last() { return Native.IsWindow(lastWindow) && Native.IsWindowVisible(lastWindow) ? lastWindow : IntPtr.Zero; }

    public static string Title(IntPtr h)
    {
        StringBuilder sb = new StringBuilder(256);
        Native.GetWindowText(h, sb, sb.Capacity);
        string t = sb.ToString().Trim();
        if (t.Length == 0) t = L10n.T("(ohne Titel)", "(untitled)");
        return t.Length > 45 ? t.Substring(0, 44) + "…" : t;
    }
}

// Shows the log (Maroom.log and the rotated .log.1), newest entries first.
class LogWindow : Form
{
    static LogWindow openInstance;
    readonly string file;
    readonly TextBox text = new TextBox();
    readonly CheckBox errorsOnly = new CheckBox();
    readonly Label status = new Label();
    static readonly string[] ErrorWords = { "failed", "not ", "error", "crash", "blocked", "unreadable", "interrupted", "aborted",
        /* entries written before 2.8 */ "fehlgeschlagen", "nicht ", "fehler", "absturz", "blockiert", "unlesbar", "unterbrochen" };

    public static LogWindow CreateNew(string file) { openInstance = new LogWindow(file); return openInstance; }

    public static void Display(string file)
    {
        if (openInstance != null && !openInstance.IsDisposed) { openInstance.Reload(); openInstance.Activate(); return; }
        openInstance = new LogWindow(file);
        openInstance.Show();
    }

    LogWindow(string file)
    {
        this.file = file;
        Text = L10n.T("Maroom – Protokoll", "Maroom – Log");
        Size = new Size(900, 520);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9f);
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { ShowIcon = false; }

        text.Multiline = true;
        text.ReadOnly = true;
        text.WordWrap = false;
        text.ScrollBars = ScrollBars.Both;
        text.Font = new Font("Consolas", 9.5f);
        text.BackColor = SystemColors.Window;
        text.Dock = DockStyle.Fill;

        FlowLayoutPanel bar = new FlowLayoutPanel();
        bar.Dock = DockStyle.Bottom;
        bar.Height = 40;
        bar.Padding = new Padding(6);
        bar.FlowDirection = FlowDirection.LeftToRight;

        errorsOnly.Text = L10n.T("Nur Fehler und Warnungen", "Errors and warnings only");
        errorsOnly.AutoSize = true;
        errorsOnly.Margin = new Padding(3, 7, 12, 3);
        errorsOnly.CheckedChanged += delegate { Reload(); };
        bar.Controls.Add(errorsOnly);
        bar.Controls.Add(MakeButton(L10n.T("Aktualisieren", "Refresh"), delegate { Reload(); }));
        bar.Controls.Add(MakeButton(L10n.T("Kopieren", "Copy"), delegate { if (text.TextLength > 0) Clipboard.SetText(text.Text); }));
        bar.Controls.Add(MakeButton(L10n.T("Ordner öffnen", "Open folder"), delegate {
            if (File.Exists(file)) Process.Start("explorer.exe", "/select,\"" + file + "\"");
            else Process.Start("explorer.exe", "\"" + Path.GetDirectoryName(file) + "\"");
        }));
        bar.Controls.Add(MakeButton(L10n.T("Leeren", "Clear"), delegate {
            if (MessageBox.Show(this, L10n.T("Protokoll wirklich leeren?", "Really clear the log?"), Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            try { File.WriteAllText(file, ""); if (File.Exists(file + ".1")) File.Delete(file + ".1"); } catch (Exception ex) { status.Text = ex.Message; return; }
            Reload();
        }));
        bar.Controls.Add(MakeButton(L10n.T("Schließen", "Close"), delegate { Close(); }));
        status.AutoSize = true;
        status.Margin = new Padding(12, 7, 3, 3);
        status.ForeColor = SystemColors.GrayText;
        bar.Controls.Add(status);

        Controls.Add(text);
        Controls.Add(bar);
        Reload();
    }

    static Button MakeButton(string label, EventHandler onClick)
    {
        Button b = new Button();
        b.Text = label;
        b.AutoSize = true;
        b.Click += onClick;
        return b;
    }

    static bool IsError(string line)
    {
        string z = line.ToLowerInvariant();
        foreach (string w in ErrorWords) if (z.Contains(w)) return true;
        return false;
    }

    void Reload()
    {
        List<string> lines = new List<string>();
        foreach (string f in new string[] { file + ".1", file })
        {
            try
            {
                if (!File.Exists(f)) continue;
                // read even while Maroom is writing
                using (FileStream fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (StreamReader r = new StreamReader(fs, Encoding.UTF8))
                {
                    string z;
                    while ((z = r.ReadLine()) != null) if (z.Length > 0) lines.Add(z);
                }
            }
            catch (Exception ex) { lines.Add("(" + Path.GetFileName(f) + " not readable: " + ex.Message + ")"); }
        }
        // multi-line entries (exceptions with stack trace) belong to the timestamped line before them
        List<string> entries = new List<string>();
        foreach (string z in lines)
        {
            if (entries.Count > 0 && !(z.Length > 19 && z[4] == '-' && z[10] == 'T')) entries[entries.Count - 1] += "\r\n    " + z;
            else entries.Add(z);
        }
        int total = entries.Count, failures = 0;
        StringBuilder sb = new StringBuilder();
        for (int i = entries.Count - 1; i >= 0; i--)
        {
            bool f = IsError(entries[i]);
            if (f) failures++;
            if (errorsOnly.Checked && !f) continue;
            sb.Append(entries[i]).Append("\r\n");
        }
        text.Text = total == 0 ? L10n.T("Das Protokoll ist leer. Das Tool schreibt nur bei Ereignissen und Problemen hinein.", "The log is empty. Maroom only writes to it on events and problems.") : sb.ToString();
        text.SelectionStart = 0;
        text.ScrollToCaret();
        status.Text = total + L10n.T(" Einträge, davon ", " entries, ") + failures + L10n.T(" Fehler/Warnungen · neueste oben", " errors/warnings · newest first");
    }
}

// Short overlay "Desktop N" in the middle of the screen the mouse is on.
// Click-through, does not steal focus, fades out after ~1 s.
class Overlay : Form
{
    static Overlay current;
    readonly string text;
    readonly int number, count;   // number = highlighted dot (1-based), 0 = none
    readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
    int ticks;

    // highlighted: index of the highlighted desktop, -1 = none
    public static void Display(string text, int highlighted, int count)
    {
        try
        {
            if (Fullscreen.IsActive()) return;   // do not disturb full screen games/videos
            if (current != null && !current.IsDisposed) current.Close();
            current = new Overlay(text, highlighted + 1, count);
            current.Show();
        }
        catch (Exception ex) { MaroomApp.Log("Overlay: " + ex.Message); }
    }

    Overlay(string text, int number, int count)
    {
        this.text = text;
        this.number = number;
        this.count = count;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(32, 32, 32);
        DoubleBuffered = true;
        Size = new Size(340, 120);
        Rectangle b = Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = new Point(b.Left + (b.Width - Width) / 2, b.Top + (b.Height - Height) / 2);
        Opacity = 0.92;
        timer.Interval = 40;
        timer.Tick += delegate {
            ticks++;
            if (ticks > 20) Opacity = Math.Max(0, Opacity - 0.12);   // fade out after 0.8 s
            if (Opacity <= 0.01) Close();
        };
    }

    protected override bool ShowWithoutActivation { get { return true; } }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= 0x20 /* TRANSPARENT */ | 0x80 /* TOOLWINDOW */ | 0x08000000 /* NOACTIVATE */ | 0x08 /* TOPMOST */;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int rund = 2; // DWMWCP_ROUND (Windows 11)
        try { Native.DwmSetWindowAttribute(Handle, 33, ref rund, 4); } catch { }
        timer.Start();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        using (Font f = new Font("Segoe UI Semibold", 26, FontStyle.Regular, GraphicsUnit.Pixel))
        {
            StringFormat sf = new StringFormat();
            sf.Alignment = StringAlignment.Center;
            sf.LineAlignment = StringAlignment.Center;
            g.DrawString(text, f, Brushes.White, new RectangleF(0, 18, Width, 50), sf);
        }
        // dots: active desktop filled
        int d = 12, gap = 22, start = (Width - ((count - 1) * gap + d)) / 2;
        using (Brush filled = new SolidBrush(Color.FromArgb(0, 120, 212)))
        using (Pen border = new Pen(Color.FromArgb(150, 150, 150), 1.5f))
        {
            for (int i = 0; i < count; i++)
            {
                Rectangle r = new Rectangle(start + i * gap, 82, d, d);
                if (i + 1 == number) g.FillEllipse(filled, r); else g.DrawEllipse(border, r);
            }
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        timer.Stop();
        timer.Dispose();
        base.OnFormClosed(e);
    }
}

class MessageWindow : NativeWindow
{
    readonly MaroomApp w;
    readonly Control invoker;
    public MessageWindow(MaroomApp w)
    {
        this.w = w;
        CreateHandle(new CreateParams());
        invoker = new Control();
        invoker.CreateControl();
        IntPtr unused = invoker.Handle;
    }
    public void BeginInvoke(Delegate d) { if (invoker.IsHandleCreated) invoker.BeginInvoke(d); }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_HOTKEY) { try { w.Hotkey(m.WParam.ToInt32()); } catch (Exception ex) { MaroomApp.Log("Hotkey: " + ex.Message); } }
        base.WndProc(ref m);
    }
}

static class KeyInput
{
    public const ushort VK_LEFT = 0x25, VK_RIGHT = 0x27, VK_D = 0x44;
    const ushort VK_LWIN = 0x5B, VK_CONTROL = 0x11, VK_LMENU = 0xA4, VK_RMENU = 0xA5, VK_LEER = 0xE8;

    public static void WinCtrl(ushort taste)
    {
        bool extended = taste == VK_LEFT || taste == VK_RIGHT;
        Send(new Native.INPUT[] {
            Key(VK_LWIN, false, true), Key(VK_CONTROL, false, false), Key(taste, false, extended),
            Key(taste, true, extended), Key(VK_CONTROL, true, false), Key(VK_LWIN, true, true)
        });
        Thread.Sleep(60);
    }

    // An unassigned key: makes Maroom the source of the last input, which Windows requires for SetForegroundWindow.
    public static void Nudge()
    {
        Send(new Native.INPUT[] { Key(VK_LEER, false, false), Key(VK_LEER, true, false) });
    }

    // While Alt+1 is held down, Win+Ctrl+Alt+Arrow would arrive otherwise.
    public static void ReleaseAlt()
    {
        bool l = (Native.GetAsyncKeyState(VK_LMENU) & 0x8000) != 0;
        bool r = (Native.GetAsyncKeyState(VK_RMENU) & 0x8000) != 0;
        if (!l && !r) return;
        List<Native.INPUT> e = new List<Native.INPUT>();
        e.Add(Key(VK_LEER, false, false)); e.Add(Key(VK_LEER, true, false)); // prevents menu activation
        if (l) e.Add(Key(VK_LMENU, true, false));
        if (r) e.Add(Key(VK_RMENU, true, true));
        Send(e.ToArray());
    }

    static Native.INPUT Key(ushort vk, bool up, bool extended)
    {
        Native.KEYBDINPUT k = new Native.KEYBDINPUT();
        k.wVk = vk;
        k.dwFlags = (up ? 2u : 0u) | (extended ? 1u : 0u);
        Native.INPUT i = new Native.INPUT();
        i.type = 1;
        i.u.ki = k;
        return i;
    }

    static void Send(Native.INPUT[] e)
    {
        if (Native.SendInput((uint)e.Length, e, Marshal.SizeOf(typeof(Native.INPUT))) != e.Length)
            MaroomApp.Log("SendInput blocked (e.g. elevated window in the foreground)");
    }
}

static class Native
{
    public const int WH_MOUSE_LL = 14, WM_XBUTTONDOWN = 0x020B, WM_XBUTTONUP = 0x020C, WM_HOTKEY = 0x0312;
    public const uint MOD_ALT = 1, MOD_NOREPEAT = 0x4000;

    public delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData, flags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Explicit)]
    public struct INPUTUNION { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT { public uint type; public INPUTUNION u; }

    [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc fn, IntPtr hMod, uint threadId);
    [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern int GetMessage(out MSG m, IntPtr h, uint min, uint max);
    [DllImport("user32.dll")] public static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32.dll")] public static extern IntPtr DispatchMessage(ref MSG m);
    [DllImport("user32.dll")] public static extern bool PostThreadMessage(uint threadId, int msg, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string name);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] public static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
    [DllImport("user32.dll")] public static extern IntPtr SendMessageTimeout(IntPtr h, int msg, IntPtr w, IntPtr l, uint flags, uint timeout, out UIntPtr result);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    public const uint MOD_SHIFT = 4;
    public delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    [DllImport("user32.dll")] public static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventProc proc, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] public static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] public static extern IntPtr GetSystemMenu(IntPtr h, bool revert);
    [DllImport("user32.dll")] public static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr id, string text);
    [DllImport("user32.dll")] public static extern int GetMenuItemCount(IntPtr menu);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetMenuString(IntPtr menu, uint item, StringBuilder sb, int max, uint flags);
    [DllImport("user32.dll")] public static extern uint GetMenuState(IntPtr menu, uint item, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr GetSubMenu(IntPtr menu, int pos);
    [DllImport("user32.dll")] public static extern int EnableMenuItem(IntPtr menu, uint id, uint flags);
    [DllImport("user32.dll")] public static extern uint CheckMenuItem(IntPtr menu, uint id, uint flags);
    [DllImport("user32.dll")] public static extern bool RemoveMenu(IntPtr menu, uint pos, uint flags);
    [DllImport("user32.dll")] public static extern bool DeleteMenu(IntPtr menu, uint pos, uint flags);
    [DllImport("user32.dll")] public static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] public static extern uint GetMenuItemID(IntPtr menu, int pos);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int left, top, right, bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("user32.dll")] public static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFO mi);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr h, int id);
    [DllImport("user32.dll")] public static extern IntPtr SetTimer(IntPtr h, IntPtr id, uint ms, IntPtr proc);
    [DllImport("kernel32.dll")] public static extern uint SetErrorMode(uint mode);
    [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool InsertMenu(IntPtr menu, uint pos, uint flags, UIntPtr id, string text);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT pt);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
    [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);
    [DllImport("user32.dll")] public static extern IntPtr GetParent(IntPtr h);
    [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] public static extern IntPtr VirtualAllocEx(IntPtr p, IntPtr a, IntPtr size, uint type, uint protect);
    [DllImport("kernel32.dll")] public static extern bool VirtualFreeEx(IntPtr p, IntPtr a, IntPtr size, uint type);
    [DllImport("kernel32.dll")] public static extern bool ReadProcessMemory(IntPtr p, IntPtr a, byte[] buf, IntPtr size, out IntPtr read);
    [DllImport("kernel32.dll")] public static extern bool WriteProcessMemory(IntPtr p, IntPtr a, byte[] buf, IntPtr size, out IntPtr written);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
    [DllImport("shell32.dll")] public static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] public static extern int SHSetKnownFolderPath(ref Guid id, uint flags, IntPtr token, string path);
}

// One-time migration of data files from versions before 2.8, which used German file names.
static class LegacyMigration
{
    public static void Run()
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Maroom");
            if (!Directory.Exists(dir)) return;
            RenameFile(dir, "zustand.txt", "state.txt");
            RenameFile(dir, "verbleib.txt", "pending.txt");
            RenameFile(dir, "neustarts.txt", "restarts.txt");
            foreach (string f in Directory.GetFiles(dir, "positionen-*.txt"))
                RenameFile(dir, Path.GetFileName(f), "positions-" + Path.GetFileName(f).Substring("positionen-".Length));

            string oldSettings = Path.Combine(dir, "einstellungen.txt"), newSettings = Path.Combine(dir, "settings.txt");
            if (File.Exists(oldSettings) && !File.Exists(newSettings))
            {
                List<string> lines = new List<string>();
                foreach (string line in File.ReadAllLines(oldSettings, Encoding.UTF8))
                    lines.Add(line.Replace("einblendung=", "overlay=").Replace("fenstermenue=", "windowmenu=").Replace("sprache=", "language="));
                File.WriteAllLines(newSettings, lines.ToArray(), new UTF8Encoding(false));
                File.Delete(oldSettings);
            }

            string oldBackup = Path.Combine(dir, "sicherung"), newBackup = Path.Combine(dir, "backup");
            if (Directory.Exists(oldBackup))
            {
                Directory.CreateDirectory(newBackup);
                foreach (string f in Directory.GetFiles(oldBackup))
                {
                    string target = Path.Combine(newBackup, Path.GetFileName(f).Replace("positionen-", "positions-"));
                    if (!File.Exists(target)) File.Move(f, target);
                }
                if (Directory.GetFileSystemEntries(oldBackup).Length == 0) Directory.Delete(oldBackup);
            }
        }
        catch (Exception ex) { MaroomApp.Log("Migrating legacy data files failed: " + ex.Message); }
    }

    // If both names exist (e.g. after a rollback to a pre-2.8 version that kept writing the old file),
    // the newer file wins.
    static void RenameFile(string dir, string oldName, string newName)
    {
        string from = Path.Combine(dir, oldName), to = Path.Combine(dir, newName);
        if (!File.Exists(from)) return;
        if (File.Exists(to))
        {
            if (File.GetLastWriteTimeUtc(from) <= File.GetLastWriteTimeUtc(to)) { File.Delete(from); return; }
            File.Delete(to);
        }
        File.Move(from, to);
        MaroomApp.Log("Migrated " + oldName + " -> " + newName);
    }

    // The journal must never be lost: if the old state file is still there, starting would assume
    // set 0 is in the desktop folder and could mix up the icon sets.
    public static bool StateIsSafe()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Maroom");
        return !File.Exists(Path.Combine(dir, "zustand.txt"));
    }
}