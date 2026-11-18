// Maroom-Setup – installs, updates and uninstalls Maroom.
// © 2026 Timo Dörsam
//
// - per-user installation without administrator rights into %LOCALAPPDATA%\Maroom
// - Maroom.exe is embedded in this file as the resource "Maroom.exe"
// - entry under Settings > Apps > Installed apps (HKCU\...\Uninstall\Maroom)
// - optional: autostart (HKCU\...\Run\Maroom) and Start menu shortcut
//
// Usage:
//   Maroom-Setup.exe                 dialog: install or update
//   Maroom-Setup.exe /quiet          install/update without dialog (options are kept)
//   Maroom-Setup.exe /uninstall      uninstall (with confirmation)
//   Maroom-Setup.exe /uninstall /quiet
//
// Exit code 3: started from a redirected environment (a terminal inside a Store app, see Container.cs);
// the setup has started itself again via Explorer and continues there.
//
// Uninstall: Maroom first puts desktop 1 back into the desktop folder. If that
// fails, the uninstall stops so that no file is left behind in the wrong folder.
// Files in "Desktop 2" are NEVER deleted.
//
// Build: see build.ps1 (csc with /resource:Maroom.exe,Maroom.exe and SetupVersion.cs)

using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

static class Setup
{
    static readonly string InstallDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Maroom");
    static readonly string Exe = Path.Combine(InstallDir, "Maroom.exe");
    static readonly string Backup = Path.Combine(InstallDir, "Maroom.bak.exe");
    static readonly string SetupCopy = Path.Combine(InstallDir, "Maroom-Setup.exe");
    static readonly string LogFile = Path.Combine(InstallDir, "setup.log");
    static readonly string ProfileDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    static readonly string StartLnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Maroom.lnk");
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Maroom";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Title = "Maroom Setup";
    static bool quiet;
    static bool logToTemp;

    [STAThread]
    static int Main(string[] args)
    {
        quiet = HasArg(args, "/quiet");
        L10n.Init(InstallDir);
        Application.EnableVisualStyles();
        // Started from a redirected environment (e.g. a terminal inside a Store app)? Then installing here would
        // land in that app's private copy of %LOCALAPPDATA%: run the setup again via Explorer instead.
        string container = Container.RedirectedTo();
        if (container != null) return RestartOutsideContainer(container, args);
        try
        {
            if (HasArg(args, "/uninstall")) return Uninstall(HasArg(args, "/temp"));
            return Install();
        }
        catch (Exception ex)
        {
            Log("Error: " + ex);
            Notify(L10n.T("Es ist ein Fehler aufgetreten:\n\n", "An error occurred:\n\n") + ex.Message, MessageBoxIcon.Error);
            return 1;
        }
    }

    // Exit code 3: the setup continues in a new process outside the container (its result is in setup.log).
    static int RestartOutsideContainer(string container, string[] args)
    {
        logToTemp = true;   // %LOCALAPPDATA% writes would land in the container copy
        string self = Container.RealPath(Assembly.GetExecutingAssembly().Location, container);
        string error = Container.StartViaExplorer(self, Container.JoinArgs(args));
        Log("Started inside the container of " + Path.GetFileName(container) + "; "
            + (error == null ? "continued outside of it via Explorer" : "starting via Explorer failed: " + error));
        if (error == null) return 3;
        Notify(L10n.T("Das Setup wurde aus einer abgeschotteten App gestartet (", "The setup was started from a sandboxed app (")
            + Path.GetFileName(container)
            + L10n.T("). Dort installiert würde Maroom in einem privaten Ordner dieser App landen.\n\nBitte starte Maroom-Setup.exe per Doppelklick im Explorer.",
                     "). Installed from there, Maroom would end up in a private folder of that app.\n\nPlease start Maroom-Setup.exe by double-clicking it in Explorer."),
            MessageBoxIcon.Warning);
        return 1;
    }

    static bool HasArg(string[] args, string a)
    {
        foreach (string s in args) if (string.Equals(s, a, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // ================= Install =================

    static int Install()
    {
        byte[] program = EmbeddedProgram();
        string newVersion = VersionOf(program);
        string oldVersion = File.Exists(Exe) ? FileVersionInfo.GetVersionInfo(Exe).FileVersion : null;

        bool autostart = oldVersion == null || ValueExists(RunKey, "Maroom");
        bool startMenuBox = oldVersion == null || File.Exists(StartLnk);
        if (!quiet)
        {
            InstallDialog d = new InstallDialog(newVersion, oldVersion, InstallDir, autostart, startMenuBox);
            if (d.ShowDialog() != DialogResult.OK) return 2;
            autostart = d.Autostart;
            startMenuBox = d.StartMenu;
        }

        Directory.CreateDirectory(InstallDir);
        Log("Install " + newVersion + (oldVersion != null ? " (previously " + oldVersion + ")" : ""));

        // Halt the running version without swapping back: the new one takes over the state from state.txt
        StopMaroom(false);

        bool hadOld = File.Exists(Exe);
        try
        {
            if (hadOld) File.Copy(Exe, Backup, true);
            string tmp = Exe + ".neu";
            File.WriteAllBytes(tmp, program);
            // replace with retries (virus scanners often scan new EXE files briefly)
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    if (File.Exists(Exe)) File.Delete(Exe);
                    File.Move(tmp, Exe);
                    break;
                }
                catch (IOException) { if (attempt >= 10) throw; Thread.Sleep(500); }
                catch (UnauthorizedAccessException) { if (attempt >= 10) throw; Thread.Sleep(500); }
            }

            string self = Assembly.GetExecutingAssembly().Location;
            if (!string.Equals(Path.GetFullPath(self), Path.GetFullPath(SetupCopy), StringComparison.OrdinalIgnoreCase))
                File.Copy(self, SetupCopy, true);

            using (RegistryKey run = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (autostart) run.SetValue("Maroom", "\"" + Exe + "\"");
                else run.DeleteValue("Maroom", false);
            }
            if (startMenuBox) CreateShortcut(StartLnk, Exe); else if (File.Exists(StartLnk)) File.Delete(StartLnk);
        }
        catch (Exception ex)
        {
            // Maroom is already stopped: restore the old version and start it again so nothing is left stopped
            Log("Installation failed, rolling back: " + ex);
            try
            {
                if (hadOld && File.Exists(Backup) && (!File.Exists(Exe) || new FileInfo(Exe).Length != program.Length)) { File.Copy(Backup, Exe, true); PrepareForOldVersion(); }
                if (File.Exists(Exe) && Process.GetProcessesByName("Maroom").Length == 0) Process.Start(Exe);
            }
            catch (Exception ex2) { Log("Rollback failed: " + ex2.Message); }
            throw;
        }

        using (RegistryKey u = Registry.CurrentUser.CreateSubKey(UninstallKey))
        {
            u.SetValue("DisplayName", "Maroom");
            u.SetValue("DisplayVersion", newVersion);
            u.SetValue("Publisher", "Timo Dörsam");
            u.SetValue("Comments", L10n.T("Eigene Räume auf virtuellen Desktops", "Your own rooms on virtual desktops"));
            u.SetValue("DisplayIcon", Exe);
            u.SetValue("InstallLocation", InstallDir);
            u.SetValue("UninstallString", "\"" + SetupCopy + "\" /uninstall");
            u.SetValue("QuietUninstallString", "\"" + SetupCopy + "\" /uninstall /quiet");
            u.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
            u.SetValue("EstimatedSize", (int)((program.Length * 2 + 1023) / 1024), RegistryValueKind.DWord);
            u.SetValue("NoModify", 1, RegistryValueKind.DWord);
            u.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        }

        Process.Start(Exe);
        if (!WaitUntil(delegate { return Process.GetProcessesByName("Maroom").Length > 0; }, 5000))
        {
            Log("New version does not start, restoring the old one");
            if (File.Exists(Backup)) { File.Copy(Backup, Exe, true); PrepareForOldVersion(); Process.Start(Exe); }
            Notify(L10n.T("Die neue Version ließ sich nicht starten. Die vorherige Version wurde wiederhergestellt.\n\nProtokoll: ", "The new version could not be started. The previous version has been restored.\n\nLog: ") + Path.Combine(InstallDir, "Maroom.log"), MessageBoxIcon.Error);
            return 1;
        }

        Log("Install done");
        Notify(oldVersion == null
            ? "Maroom " + newVersion + L10n.T(" ist installiert und läuft.\n\nDas Symbol mit der Desktop-Nummer findest du im Infobereich der Taskleiste.", " is installed and running.\n\nYou will find the icon with the desktop number in the notification area of the taskbar.")
            : L10n.T("Maroom wurde auf Version ", "Maroom has been updated to version ") + newVersion + L10n.T(" aktualisiert und läuft wieder.", " and is running again."), MessageBoxIcon.Information);
        return 0;
    }

    static byte[] EmbeddedProgram()
    {
        using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("Maroom.exe"))
        {
            if (s == null) throw new InvalidOperationException(L10n.T("Maroom.exe ist nicht im Setup enthalten.", "Maroom.exe is missing from the setup."));
            using (MemoryStream m = new MemoryStream()) { s.CopyTo(m); return m.ToArray(); }
        }
    }

    static string VersionOf(byte[] program)
    {
        string tmp = Path.Combine(Path.GetTempPath(), "Maroom-version-" + Guid.NewGuid().ToString("N") + ".exe");
        try { File.WriteAllBytes(tmp, program); return FileVersionInfo.GetVersionInfo(tmp).FileVersion; }
        finally { try { File.Delete(tmp); } catch { } }
    }

    static void CreateShortcut(string lnk, string target)
    {
        Type t = Type.GetTypeFromProgID("WScript.Shell");
        object shell = Activator.CreateInstance(t);
        object l = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
        Type lt = l.GetType();
        lt.InvokeMember("TargetPath", BindingFlags.SetProperty, null, l, new object[] { target });
        lt.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, l, new object[] { Path.GetDirectoryName(target) });
        lt.InvokeMember("Description", BindingFlags.SetProperty, null, l, new object[] { L10n.T("Maroom – eigene Räume auf virtuellen Desktops", "Maroom – your own rooms on virtual desktops") });
        lt.InvokeMember("Save", BindingFlags.InvokeMethod, null, l, null);
    }

    // ================= Uninstall =================

    static int Uninstall(bool fromTemp)
    {
        string self = Assembly.GetExecutingAssembly().Location;

        // Started from the install folder: run a copy from %TEMP%, otherwise the folder cannot be deleted
        if (!fromTemp && self.StartsWith(InstallDir, StringComparison.OrdinalIgnoreCase))
        {
            string copy = Path.Combine(Path.GetTempPath(), "Maroom-Uninstall.exe");
            File.Copy(self, copy, true);
            Process.Start(copy, "/uninstall /temp" + (quiet ? " /quiet" : ""));
            return 0;
        }

        if (!quiet && MessageBox.Show(
                L10n.T("Maroom deinstallieren?\n\n" +
                "• Die Symbole von Desktop 1 kommen zurück in den Desktop-Ordner.\n" +
                "• Autostart, Startmenü-Eintrag und Programmdaten werden entfernt.\n" +
                "• Deine Dateien in „Desktop 2“ bleiben erhalten.",
                "Uninstall Maroom?\n\n" +
                "• The icons of desktop 1 are put back into the desktop folder.\n" +
                "• Autostart, Start menu entry and program data are removed.\n" +
                "• Your files in \"Desktop 2\" are kept."),
                Title, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return 2;

        Log("Uninstall");

        // 1. Put desktop 1 back into the desktop folder
        if (Process.GetProcessesByName("Maroom").Length > 0) StopMaroom(true);
        else if (ReadState() != "0" && File.Exists(Exe))
        {
            // Maroom is not running but desktop 2 is still in the desktop folder: start it briefly and let it reset
            Process.Start(Exe);
            WaitUntil(delegate {
                EventWaitHandle h;
                bool exists = EventWaitHandle.TryOpenExisting("Maroom_Reset", out h) || EventWaitHandle.TryOpenExisting("Maroom_Zuruecksetzen", out h);
                if (exists) h.Dispose();
                return exists;
            }, 15000);
            StopMaroom(true);
        }
        string state = ReadState();
        if (state != "0")
        {
            Log("Aborted: state after reset is '" + state + "'");
            Notify(L10n.T("Die Deinstallation wurde abgebrochen, weil die Symbole von Desktop 1 nicht sicher zurückgelegt werden konnten (Zustand: ", "Uninstall was cancelled because the icons of desktop 1 could not be put back safely (state: ") + state + ").\n\n" +
                    L10n.T("Bitte Maroom starten, auf Desktop 1 wechseln und es erneut versuchen. Es wurde nichts entfernt.", "Please start Maroom, switch to desktop 1 and try again. Nothing was removed."), MessageBoxIcon.Warning);
            return 1;
        }
        string pending = Path.Combine(InstallDir, "pending.txt");
        if (!File.Exists(pending)) pending = Path.Combine(InstallDir, "verbleib.txt");   // name before 2.8
        bool hasPending = File.Exists(pending) && new FileInfo(pending).Length > 0;

        // 2. Remove entries
        using (RegistryKey run = Registry.CurrentUser.OpenSubKey(RunKey, true)) { if (run != null) run.DeleteValue("Maroom", false); }
        if (File.Exists(StartLnk)) File.Delete(StartLnk);
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);

        // 3. Delete program and data
        string restNote = "";
        try { if (Directory.Exists(InstallDir)) Directory.Delete(InstallDir, true); }
        catch (Exception ex) { restNote = L10n.T("\n\nDer Ordner ", "\n\nThe folder ") + InstallDir + L10n.T(" ließ sich nicht ganz löschen (", " could not be deleted completely (") + ex.Message + ")."; }

        // 4. Remove the empty storage of desktop 1, never touch desktop 2
        string storageParent = StorageParent();
        string store1 = Path.Combine(storageParent, "Desktop 1"), store2 = Path.Combine(storageParent, "Desktop 2");
        try { if (Directory.Exists(store1) && Directory.GetFileSystemEntries(store1).Length == 0) Directory.Delete(store1); } catch { }
        int count2 = Directory.Exists(store2) ? Directory.GetFileSystemEntries(store2).Length : 0;

        string text = L10n.T("Maroom wurde deinstalliert.", "Maroom has been uninstalled.");
        if (count2 > 0) text += L10n.T("\n\nDeine ", "\n\nYour ") + count2 + L10n.T(" Einträge von Desktop 2 liegen weiterhin in:\n", " items of desktop 2 are still in:\n") + store2;
        if (Directory.Exists(store1)) text += L10n.T("\n\nIn ", "\n\n") + store1 + L10n.T(" liegen noch Dateien von Desktop 1, bitte von Hand zurück auf den Desktop legen.", " still contains files of desktop 1. Please move them back to the desktop by hand.");
        if (hasPending) text += L10n.T("\n\nHinweis: Einzelne gesperrte Dateien waren vorgemerkt. Bitte Desktop und „Desktop 2“ kurz prüfen.", "\n\nNote: some locked files were pending. Please check the desktop and \"Desktop 2\".");
        text += L10n.T("\n\nDie virtuellen Desktops von Windows bleiben bestehen.", "\n\nThe Windows virtual desktops remain.") + restNote;
        Notify(text, MessageBoxIcon.Information);

        // Remove the temporary copy of itself
        if (fromTemp)
        {
            ProcessStartInfo p = new ProcessStartInfo("cmd.exe", "/c ping 127.0.0.1 -n 3 > nul & del \"" + self + "\"");
            p.CreateNoWindow = true;
            p.WindowStyle = ProcessWindowStyle.Hidden;
            Process.Start(p);
        }
        return 0;
    }

    // Same rule as Maroom: storage folders live next to the real (possibly redirected) desktop folder.
    static string StorageParent()
    {
        string desktop = null;
        try
        {
            IntPtr p;
            Guid g = new Guid("B4BFCC3A-DB2C-424C-B029-7FE99A87C641");   // FOLDERID_Desktop
            if (SHGetKnownFolderPath(ref g, 0, IntPtr.Zero, out p) == 0) { desktop = Marshal.PtrToStringUni(p); Marshal.FreeCoTaskMem(p); }
        }
        catch { }
        string name = Path.GetFileName((desktop ?? "").TrimEnd('\\'));
        if (string.IsNullOrEmpty(desktop) || name == "Desktop 1" || name == "Desktop 2") return ProfileDir;
        string parent = Path.GetDirectoryName(desktop.TrimEnd('\\'));
        return string.IsNullOrEmpty(parent) ? ProfileDir : parent;
    }

    [DllImport("shell32.dll")] static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);

    // A restored version before 2.8 only knows zustand.txt. Without it, it would assume set 0 is in the
    // desktop folder and could mix up the icon sets, so provide the journal under the old name.
    static void PrepareForOldVersion()
    {
        try
        {
            Version v = new Version(FileVersionInfo.GetVersionInfo(Exe).FileVersion);
            if (v >= new Version(2, 8)) return;
            string state = Path.Combine(InstallDir, "state.txt");
            if (File.Exists(state)) File.Copy(state, Path.Combine(InstallDir, "zustand.txt"), true);
        }
        catch (Exception ex) { Log("Preparing old version failed: " + ex.Message); }
    }

    static string ReadState()
    {
        string f = Path.Combine(InstallDir, "state.txt");
        if (!File.Exists(f)) f = Path.Combine(InstallDir, "zustand.txt");   // name before 2.8
        try { return File.Exists(f) ? File.ReadAllText(f).Trim() : "0"; } catch { return "?"; }
    }

    // ================= Helpers =================

    // resetToDesktop1 = true: Maroom puts desktop 1 back and exits (uninstall)
    static void StopMaroom(bool resetToDesktop1)
    {
        Process[] running = Process.GetProcessesByName("Maroom");
        if (running.Length == 0) return;
        EventWaitHandle h;
        if ((EventWaitHandle.TryOpenExisting(resetToDesktop1 ? "Maroom_Reset" : "Maroom_Stop", out h)
            // versions before 2.8 used German signal names
            || EventWaitHandle.TryOpenExisting(resetToDesktop1 ? "Maroom_Zuruecksetzen" : "Maroom_Beenden", out h)))
        {
            h.Set();
            h.Dispose();
        }
        foreach (Process p in running)
        {
            if (!p.WaitForExit(30000))
            {
                Log("Maroom does not respond, killing it");
                try { p.Kill(); p.WaitForExit(5000); } catch { }
            }
        }
        Thread.Sleep(300);
    }

    static bool ValueExists(string keyPath, string name)
    {
        using (RegistryKey k = Registry.CurrentUser.OpenSubKey(keyPath)) return k != null && k.GetValue(name) != null;
    }

    static bool WaitUntil(Func<bool> condition, int ms)
    {
        Stopwatch sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms) { if (condition()) return true; Thread.Sleep(200); }
        return condition();
    }

    static void Notify(string text, MessageBoxIcon icon)
    {
        if (quiet) { Log(text.Replace("\n", " ")); return; }
        MessageBox.Show(text, Title, MessageBoxButtons.OK, icon);
    }

    static void Log(string s)
    {
        try
        {
            string file = Directory.Exists(InstallDir) && !logToTemp ? LogFile : Path.Combine(Path.GetTempPath(), "Maroom-setup.log");
            File.AppendAllText(file, DateTime.Now.ToString("s") + " " + s + Environment.NewLine);
        }
        catch { }
    }
}

class InstallDialog : Form
{
    readonly CheckBox autostart = new CheckBox(), startMenuBox = new CheckBox();
    public bool Autostart { get { return autostart.Checked; } }
    public bool StartMenu { get { return startMenuBox.Checked; } }

    public InstallDialog(string fresh, string old, string target, bool withAutostart, bool withStartMenu)
    {
        Text = "Maroom Setup";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(480, 270);
        Font = new Font("Segoe UI", 9.5f);
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { ShowIcon = false; }

        Label header = new Label();
        header.Text = "Maroom " + fresh;
        header.Font = new Font("Segoe UI Semibold", 18f);
        header.AutoSize = true;
        header.Location = new Point(20, 16);

        Label subtitle = new Label();
        subtitle.Text = L10n.T("Eigene Räume auf virtuellen Desktops", "Your own rooms on virtual desktops") + " · © 2026 Timo Dörsam";
        subtitle.ForeColor = SystemColors.GrayText;
        subtitle.AutoSize = true;
        subtitle.Location = new Point(22, 56);

        Label info = new Label();
        info.Text = (old == null ? L10n.T("Wird installiert nach:", "Will be installed to:") : L10n.T("Installiert ist Version ", "Installed version is ") + old + L10n.T(". Wird aktualisiert in:", ". Will be updated in:")) +
                    "\n" + target + L10n.T("\n(nur für dich, ohne Administratorrechte)", "\n(for you only, no administrator rights needed)");
        info.AutoSize = true;
        info.Location = new Point(22, 90);

        autostart.Text = L10n.T("Mit Windows starten", "Start with Windows");
        autostart.Checked = withAutostart;
        autostart.AutoSize = true;
        autostart.Location = new Point(24, 160);

        startMenuBox.Text = L10n.T("Eintrag im Startmenü", "Add to Start menu");
        startMenuBox.Checked = withStartMenu;
        startMenuBox.AutoSize = true;
        startMenuBox.Location = new Point(24, 186);

        Button ok = new Button();
        ok.Text = old == null ? L10n.T("Installieren", "Install") : L10n.T("Aktualisieren", "Update");
        ok.DialogResult = DialogResult.OK;
        ok.Size = new Size(120, 32);
        ok.Location = new Point(ClientSize.Width - 260, ClientSize.Height - 48);

        Button cancelButton = new Button();
        cancelButton.Text = L10n.T("Abbrechen", "Cancel");
        cancelButton.DialogResult = DialogResult.Cancel;
        cancelButton.Size = new Size(120, 32);
        cancelButton.Location = new Point(ClientSize.Width - 132, ClientSize.Height - 48);

        Controls.AddRange(new Control[] { header, subtitle, info, autostart, startMenuBox, ok, cancelButton });
        AcceptButton = ok;
        CancelButton = cancelButton;
    }
}

// Language as in Maroom: setting "language" from settings.txt (auto/de/en), otherwise the Windows language
static class L10n
{
    public static bool En { get; private set; }

    public static void Init(string dataDir)
    {
        string choice = "auto";
        try
        {
            string f = Path.Combine(dataDir, "settings.txt");
            if (!File.Exists(f)) f = Path.Combine(dataDir, "einstellungen.txt");   // name before 2.8
            if (File.Exists(f))
                foreach (string z in File.ReadAllLines(f, System.Text.Encoding.UTF8))
                    if (z.StartsWith("language=")) choice = z.Substring(9).Trim().ToLowerInvariant();
                    else if (z.StartsWith("sprache=")) choice = z.Substring(8).Trim().ToLowerInvariant();
        }
        catch { }
        En = choice == "en" || (choice != "de" && System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName != "de");
    }

    public static string T(string de, string en) { return En ? en : de; }
}