using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Threading;
using System.Windows.Forms;

internal static class FerryTray
{
    private static string projectRoot = Directory.GetCurrentDirectory();
    private static int port = 8787;
    private static bool noOpen = false;
    private static string dataDir = "";
    private static string pidFile = "";
    private static string outLog = "";
    private static string errLog = "";
    private static string trayLog = "";
    private static string iconFile = "";
    private static string openUrl = "";
    private static string healthUrl = "";
    private static NotifyIcon notifyIcon;
    private static Mutex mutex;

    [STAThread]
    private static void Main(string[] args)
    {
        ParseArgs(args);
        dataDir = Path.Combine(projectRoot, "data");
        pidFile = Path.Combine(dataDir, "ferry.pid");
        outLog = Path.Combine(dataDir, "ferry.out.log");
        errLog = Path.Combine(dataDir, "ferry.err.log");
        trayLog = Path.Combine(dataDir, "ferry.tray.log");
        iconFile = Path.Combine(dataDir, "ferry.ico");
        openUrl = "http://localhost:" + port;
        healthUrl = "http://127.0.0.1:" + port;

        Directory.CreateDirectory(dataDir);
        Log("FerryTray.exe invoked. ProjectRoot=" + projectRoot + " Port=" + port + " NoOpen=" + noOpen);

        bool createdNew;
        mutex = new Mutex(true, "Local\\FerryTrayNative", out createdNew);
        if (!createdNew)
        {
            Log("Another native tray instance owns the mutex.");
            if (!noOpen) OpenFerry();
            return;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        bool started = StartFerryServer();
        if (started && !noOpen) OpenFerry();

        using (Form form = CreateHiddenForm())
        {
            notifyIcon = new NotifyIcon();
            notifyIcon.Icon = LoadIcon();
            notifyIcon.Text = "Ferry";
            notifyIcon.ContextMenuStrip = BuildMenu(form);
            notifyIcon.Visible = true;
            notifyIcon.MouseUp += delegate(object sender, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) OpenFerry();
            };

            Log("NotifyIcon set visible.");
            notifyIcon.ShowBalloonTip(
                started ? 2000 : 5000,
                "Ferry",
                started ? "Ferry is running at " + openUrl + "." : "Ferry did not start. Check data\\ferry.err.log.",
                started ? ToolTipIcon.Info : ToolTipIcon.Error);

            try
            {
                Log("Entering WinForms message loop.");
                Application.Run(form);
            }
            finally
            {
                Log("Tray executable exiting.");
                notifyIcon.Visible = false;
                notifyIcon.Dispose();
                mutex.ReleaseMutex();
                mutex.Dispose();
            }
        }
    }

    private static void ParseArgs(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a == "--project" && i + 1 < args.Length) projectRoot = args[++i];
            else if (a == "--port" && i + 1 < args.Length) int.TryParse(args[++i], out port);
            else if (a == "--no-open") noOpen = true;
        }
        projectRoot = Path.GetFullPath(projectRoot);
    }

    private static Form CreateHiddenForm()
    {
        Form form = new Form();
        form.Text = "Ferry";
        form.ShowInTaskbar = false;
        form.WindowState = FormWindowState.Minimized;
        form.FormBorderStyle = FormBorderStyle.FixedToolWindow;
        form.Opacity = 0;
        form.Size = new Size(0, 0);
        form.Shown += delegate { form.Hide(); };
        return form;
    }

    private static ContextMenuStrip BuildMenu(Form form)
    {
        ContextMenuStrip menu = new ContextMenuStrip();
        menu.Items.Add("Open Ferry", null, delegate { OpenFerry(); });
        menu.Items.Add("Copy Phone URL", null, delegate
        {
            Clipboard.SetText(GetPhoneUrl());
            notifyIcon.ShowBalloonTip(2000, "Ferry", "Phone URL copied.", ToolTipIcon.Info);
        });
        menu.Items.Add("Restart Ferry", null, delegate
        {
            StopFerryServer();
            bool ok = StartFerryServer();
            notifyIcon.ShowBalloonTip(
                ok ? 2500 : 5000,
                "Ferry",
                ok ? "Ferry restarted." : "Ferry did not start. Check data\\ferry.err.log.",
                ok ? ToolTipIcon.Info : ToolTipIcon.Error);
        });
        menu.Items.Add("Stop Ferry", null, delegate
        {
            StopFerryServer();
            notifyIcon.ShowBalloonTip(2500, "Ferry", "Ferry stopped.", ToolTipIcon.Info);
        });
        menu.Items.Add("Open Project Folder", null, delegate { Process.Start(projectRoot); });
        menu.Items.Add("Open Logs", null, delegate { Process.Start(dataDir); });
        menu.Items.Add("-");
        menu.Items.Add("Quit Ferry", null, delegate
        {
            StopFerryServer();
            form.Close();
        });
        return menu;
    }

    private static Icon LoadIcon()
    {
        try
        {
            if (File.Exists(iconFile)) return new Icon(iconFile);
        }
        catch { }
        return SystemIcons.Application;
    }

    private static bool TestFerryServer()
    {
        try
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(healthUrl + "/api/info");
            req.Timeout = 2000;
            using (HttpWebResponse res = (HttpWebResponse)req.GetResponse())
            {
                return res.StatusCode == HttpStatusCode.OK;
            }
        }
        catch
        {
            return false;
        }
    }

    private static bool StartFerryServer()
    {
        if (TestFerryServer())
        {
            Log("Ferry server already healthy.");
            return true;
        }

        string node = FindNode();
        if (node == null)
        {
            Log("ERROR: node.exe not found on PATH.");
            return false;
        }

        Log("Starting Ferry server with " + node + ".");
        ProcessStartInfo psi = new ProcessStartInfo();
        psi.FileName = node;
        psi.Arguments = "--no-warnings server.js";
        psi.WorkingDirectory = projectRoot;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;

        Process proc = Process.Start(psi);
        File.WriteAllText(pidFile, proc.Id.ToString());
        BeginRedirect(proc.StandardOutput, outLog);
        BeginRedirect(proc.StandardError, errLog);

        for (int i = 0; i < 30; i++)
        {
            Thread.Sleep(400);
            if (TestFerryServer())
            {
                Log("Ferry server became healthy as PID " + proc.Id + ".");
                return true;
            }
        }

        Log("Ferry server did not become healthy after startup wait.");
        return false;
    }

    private static void BeginRedirect(StreamReader reader, string path)
    {
        Thread t = new Thread(delegate()
        {
            try
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    File.AppendAllText(path, line + Environment.NewLine);
                }
            }
            catch { }
        });
        t.IsBackground = true;
        t.Start();
    }

    private static void StopFerryServer()
    {
        try
        {
            if (!File.Exists(pidFile)) return;
            int pid;
            if (!int.TryParse(File.ReadAllText(pidFile).Trim(), out pid)) return;
            Process proc = Process.GetProcessById(pid);
            if (proc.ProcessName.Equals("node", StringComparison.OrdinalIgnoreCase))
            {
                proc.Kill();
                Log("Stopped Ferry server PID " + pid + ".");
            }
            File.Delete(pidFile);
        }
        catch (Exception ex)
        {
            Log("StopFerryServer ignored: " + ex.Message);
        }
    }

    private static string FindNode()
    {
        string path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (string dir in path.Split(Path.PathSeparator))
        {
            try
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                string candidate = Path.Combine(dir.Trim(), "node.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch { }
        }

        string common = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe");
        return File.Exists(common) ? common : null;
    }

    private static void OpenFerry()
    {
        try { Process.Start(openUrl); }
        catch (Exception ex) { Log("OpenFerry failed: " + ex.Message); }
    }

    private static string GetPhoneUrl()
    {
        try
        {
            using (WebClient client = new WebClient())
            {
                string json = client.DownloadString(healthUrl + "/api/info");
                const string key = "\"primary\":\"";
                int start = json.IndexOf(key, StringComparison.Ordinal);
                if (start >= 0)
                {
                    start += key.Length;
                    int end = json.IndexOf("\"", start, StringComparison.Ordinal);
                    if (end > start) return json.Substring(start, end - start).Replace("\\/", "/");
                }
            }
        }
        catch { }
        return openUrl;
    }

    private static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(dataDir);
            File.AppendAllText(
                trayLog,
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + message + Environment.NewLine);
        }
        catch { }
    }
}
