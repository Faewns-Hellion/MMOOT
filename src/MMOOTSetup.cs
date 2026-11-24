// MMOOT installer (LoZ Masks of Time).
//
// One window: pick the Majora's Mask ROM and the Ocarina of Time ROM from files you own, put them where the
// game finds them, and check github.com/Faewns-Hellion/MMOOT for a newer release.
//
// No game data is bundled and nothing is downloaded from anywhere but the project's own GitHub releases.
//
// Build: MMOOT/tools/make_installer.ps1  (self-contained Windows build; .NET 8 SDK required to compile)

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Net;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace MmootSetup
{
    internal enum RomKind { MajorasMask, Ocarina }

    internal static class Program
    {
        // Where the game reads its ROMs from: zelda64::get_app_folder_path(), i.e. %LOCALAPPDATA%\Zelda64Recompiled.
        // mmoot_content.cpp opens "<app folder>\mm.n64.us.1.0.z64" for the Termina scene table, so the MM ROM has to
        // live there under that exact name.
        public static readonly string DefaultInstallDir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Zelda64Recompiled");

        public const string MmRomName = "mm.n64.us.1.0.z64";
        public const string OotRomName = "oot.n64.us.1.0.z64";

        public const string RepoOwner = "Faewns-Hellion";
        public const string RepoName = "MMOOT";
        public const string RepoUrl = "https://github.com/" + RepoOwner + "/" + RepoName;
        public const string LatestReleaseApi = "https://api.github.com/repos/" + RepoOwner + "/" + RepoName + "/releases/latest";
        public const string TagsApi = "https://api.github.com/repos/" + RepoOwner + "/" + RepoName + "/tags";
        public const string IssuesUrl = RepoUrl + "/issues/new";
        public const string InstallerVersion = "16z70";

        [STAThread]
        private static int Main(string[] args)
        {
            StartupDiagnostics.Write("Starting " + InstallerVersion + " | " + Environment.Version +
                " | " + (Environment.Is64BitProcess ? "64-bit" : "32-bit"));
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e)
            { StartupDiagnostics.Report(e.Exception); };
            AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
            { StartupDiagnostics.Write(Convert.ToString(e.ExceptionObject)); };
            try { return Run(args); }
            catch (Exception error) { StartupDiagnostics.Report(error); return 1; }
        }

        private static int Run(string[] args)
        {
            // Console modes, so the installer can be checked without clicking anything.
            //   MMOOTSetup.exe --validate mm|oot <rom path>
            //   MMOOTSetup.exe --check-updates
            if (args.Length >= 3 && args[0] == "--validate")
            {
                RomKind kind = args[1] == "mm" ? RomKind.MajorasMask : RomKind.Ocarina;
                Console.WriteLine(RomCheck.Describe(args[2], kind));
                return RomCheck.Check(args[2], kind).Ok ? 0 : 1;
            }
            if (args.Length >= 4 && args[0] == "--install")
            {
                bool ok = Installer.Run(args[1], args[2], args[3], Console.WriteLine);
                if (ok) Console.WriteLine("Installed " + Program.InstallerVersion + " into " + args[3]);
                return ok ? 0 : 1;
            }
            if (args.Length >= 4 && args[0] == "--build")
            {
                // --build <source folder> <mm rom> <oot rom>: configure, recompile, install. What the window does.
                return BuilderFlow.Run(args[1], args[2], args[3], Console.WriteLine) ? 0 : 1;
            }
            if (args.Length >= 2 && args[0] == "--check-build")
            {
                string[] problems = Builder.Preflight(args[1]);
                if (problems.Length == 0) { Console.WriteLine("build prerequisites OK"); return 0; }
                foreach (string problem in problems) Console.WriteLine("missing: " + problem);
                return 1;
            }
            if (args.Length >= 1 && args[0] == "--check-updates")
            {
                UpdateInfo info = GithubCheck.Latest();
                Console.WriteLine(info.Describe(InstalledVersion()));
                return info.Ok ? 0 : 1;
            }

            // Packs a built game into one file the other PC can select:
            //   MMOOTSetup.exe --pack <build folder> <output zip> [content image]
            if (args.Length >= 3 && args[0] == "--pack")
            {
                string packed = GamePackage.Pack(args[1], args.Length >= 4 ? args[3] : null, args[2], Console.WriteLine);
                Console.WriteLine(packed != null ? "PACK OK " + packed : "PACK FAILED");
                return packed != null ? 0 : 1;
            }

            // Installs a game that was built on another PC, for checking without clicking:
            //   MMOOTSetup.exe --fuse <game folder or .zip> <mm rom> <oot rom> [install folder]
            if (args.Length >= 4 && args[0] == "--fuse")
            {
                string target = args.Length >= 5 ? args[4] : Program.DefaultInstallDir;
                bool fused = Installer.Run(args[2], args[3], target, Console.WriteLine);
                if (fused) fused = GamePackage.Install(args[1], target, Console.WriteLine);
                Console.WriteLine(fused ? "FUSE OK" : "FUSE FAILED");
                return fused ? 0 : 1;
            }

            // Builds a crash report exactly as the button would, writes it, and prints the draft issue URL length.
            // It opens no browser: the report is only handed over when the player chooses to.
            if (args.Length >= 1 && args[0] == "--report-check")
            {
                string sample = BugReport.Collect(Program.DefaultInstallDir, Builder.DetectSource(), "");
                string written = BugReport.Save(sample, args.Length > 1 ? args[1] : null);
                string issue = BugReport.IssueUrl(sample);
                Console.WriteLine("crash report written: " + written + " (" + sample.Length + " chars)");
                Console.WriteLine("github issue draft url: " + issue.Length + " chars; nothing opened, nothing sent");
                return 0;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (args.Length == 1 && args[0] == "--startup-check")
            {
                using (SetupForm form = new SetupForm())
                {
                    form.CreateControl();
                    StartupDiagnostics.Write("STARTUP CHECK PASSED: setup window constructed without project files.");
                }
                return 0;
            }
            StartupDiagnostics.Write("Opening setup window");
            Application.Run(new SetupForm());
            return 0;
        }

        public static string InstalledVersion()
        {
            try
            {
                string path = Path.Combine(DefaultInstallDir, "mmoot_version.txt");
                if (File.Exists(path))
                {
                    string text = File.ReadAllText(path).Trim();
                    if (text.Length != 0) return text;
                }
            }
            catch { }
            return "";
        }
    }

    internal static class StartupDiagnostics
    {
        public static string LogPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MMOOTSetup", "startup.log"); }
        }
        public static void Write(string message)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath));
                File.AppendAllText(LogPath, DateTime.Now.ToString("s", CultureInfo.InvariantCulture) + " " +
                    message + Environment.NewLine);
            }
            catch { }
        }
        public static void Report(Exception error)
        {
            Write(error.ToString());
            try { MessageBox.Show("MMOOT setup could not continue.\n\n" + error.Message +
                "\n\nDetails were written to:\n" + LogPath,
                "MMOOT setup", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            catch { }
        }
    }

    // Crash reports are retroactive and strictly opt-in. Nothing is captured while the game runs and nothing is
    // uploaded: this only reads the logs and crash dumps that already sit on the PC when the player presses the
    // button, writes the report into the player's own folder, and shows it for review. The one action that can leave
    // the machine is opening a GitHub issue draft, which the player still has to submit themselves.
    internal static class BugReport
    {
        public const string PrivacyNote =
            "Not data collection. Nothing is recorded while you play, nothing is uploaded, and no account is used. " +
            "This only reads the crash logs and dumps already on this PC, shows them to you, and saves the report in " +
            "your own folder. It is here so a crash can be fixed - close this window and nothing leaves your PC.";

        public static string ReportsFolder
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MMOOTSetup", "reports");
            }
        }

        public static string Collect(string installDir, string sourceDir, string windowLog)
        {
            StringBuilder text = new StringBuilder();
            text.AppendLine("MMOOT crash report");
            text.AppendLine("Written " + DateTime.Now.ToString("u", CultureInfo.InvariantCulture) +
                " on this PC. Nothing here was sent anywhere.");
            text.AppendLine();
            text.AppendLine("Launcher    : MMOOTSetup " + Program.InstallerVersion +
                " (" + (Environment.Is64BitProcess ? "64-bit" : "32-bit") + ")");
            text.AppendLine("Runtime     : .NET " + Environment.Version);
            text.AppendLine("Windows     : " + Environment.OSVersion.VersionString);
            string installed = Program.InstalledVersion();
            text.AppendLine("Installed   : " + (installed.Length == 0 ? "(no mmoot_version.txt)" : installed));
            text.AppendLine("Game folder : " + (string.IsNullOrEmpty(installDir) ? "(not set)" : installDir));
            text.AppendLine("Source      : " + (string.IsNullOrEmpty(sourceDir) ? "(not set)" : sourceDir));

            // A crash already left its files behind, so look where the play scripts put them: pc\logs next to the
            // executable, or under the source tree if the game was not installed here.
            List<string> candidates = new List<string>();
            if (!string.IsNullOrEmpty(installDir)) candidates.Add(Path.Combine(installDir, "pc", "logs"));
            if (!string.IsNullOrEmpty(sourceDir))
            {
                candidates.Add(Path.Combine(sourceDir, "pc", "logs"));
                candidates.Add(Path.Combine(sourceDir, "MMOOT", "pc", "logs"));
            }
            string logDir = null;
            foreach (string candidate in candidates)
                if (Directory.Exists(candidate)) { logDir = candidate; break; }

            AppendSection(text, "Crash dumps left by earlier runs", CrashDumps(logDir));
            AppendSection(text, "Last run (pc\\logs\\play_hyrule.log, tail)",
                ReadTail(logDir == null ? null : Path.Combine(logDir, "play_hyrule.log"), 200));
            AppendSection(text, "Previous runs kept in pc\\logs\\old", OldRuns(logDir));
            AppendSection(text, "Launcher startup log (tail)",
                ReadTail(StartupDiagnostics.LogPath, 60));
            AppendSection(text, "This launcher window's own log", Tail(windowLog, 40));
            return text.ToString();
        }

        public static string Save(string text, string folder)
        {
            string dir = string.IsNullOrEmpty(folder) ? ReportsFolder : folder;
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "MMOOT-report-" +
                DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".txt");
            File.WriteAllText(path, text);
            return path;
        }

        // The report is too long for a URL, so the issue draft carries the head of it and asks for the saved file.
        public static string IssueUrl(string report)
        {
            const int bodyLimit = 1800;
            string head = report.Length <= bodyLimit ? report : report.Substring(0, bodyLimit);
            string body = "What happened:\n\n\nWhat you expected:\n\n\n" + head;
            if (report.Length > bodyLimit)
                body += "\n\n[The rest of the report is in the MMOOT-report-<date>.txt file this button saved - " +
                        "please attach it.]";
            string title = "Crash report: MMOOT " + Program.InstallerVersion;
            return Program.IssuesUrl + "?title=" + Uri.EscapeDataString(title) +
                "&body=" + Uri.EscapeDataString(body);
        }

        private static void AppendSection(StringBuilder text, string title, string content)
        {
            text.AppendLine();
            text.AppendLine("---- " + title + " ----");
            text.AppendLine(string.IsNullOrEmpty(content) ? "(nothing found)" : content.TrimEnd());
        }

        private static string CrashDumps(string logDir)
        {
            if (logDir == null || !Directory.Exists(logDir)) return "(no pc\\logs folder found)";
            string[] dumps;
            try { dumps = Directory.GetFiles(logDir, "crash_*.dmp"); }
            catch { return "(could not list crash dumps)"; }
            if (dumps.Length == 0) return "(no crash_*.dmp files)";
            Array.Sort(dumps, delegate(string a, string b)
            { return File.GetLastWriteTime(b).CompareTo(File.GetLastWriteTime(a)); });
            StringBuilder text = new StringBuilder();
            for (int i = 0; i < dumps.Length && i < 5; i++)
            {
                try
                {
                    FileInfo info = new FileInfo(dumps[i]);
                    text.AppendLine(Path.GetFileName(dumps[i]) + "  " + info.Length.ToString("N0") + " bytes  " +
                        info.LastWriteTime.ToString("u", CultureInfo.InvariantCulture));
                }
                catch { }
            }
            if (dumps.Length > 5) text.AppendLine("(" + (dumps.Length - 5) + " older dumps not listed)");
            return text.ToString();
        }

        private static string OldRuns(string logDir)
        {
            if (logDir == null) return "(no pc\\logs folder found)";
            string old = Path.Combine(logDir, "old");
            if (!Directory.Exists(old)) return "(no logs\\old folder)";
            string[] runs;
            try { runs = Directory.GetFiles(old, "play_hyrule_*.log"); }
            catch { return "(could not list previous runs)"; }
            if (runs.Length == 0) return "(no previous runs kept)";
            Array.Sort(runs, delegate(string a, string b)
            { return File.GetLastWriteTime(b).CompareTo(File.GetLastWriteTime(a)); });
            StringBuilder text = new StringBuilder();
            for (int i = 0; i < runs.Length && i < 5; i++)
            {
                try
                {
                    FileInfo info = new FileInfo(runs[i]);
                    text.AppendLine(Path.GetFileName(runs[i]) + "  " + info.Length.ToString("N0") + " bytes  " +
                        info.LastWriteTime.ToString("u", CultureInfo.InvariantCulture));
                }
                catch { }
            }
            return text.ToString();
        }

        private static string ReadTail(string path, int lines)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return "(not found: " + (path ?? "path unknown") + ")";
            try
            {
                string[] all = File.ReadAllLines(path);
                int start = all.Length > lines ? all.Length - lines : 0;
                StringBuilder text = new StringBuilder();
                for (int i = start; i < all.Length; i++) text.AppendLine(all[i]);
                if (start > 0) text.Insert(0, "(" + start + " earlier lines not shown)\n");
                return text.ToString();
            }
            catch (Exception error) { return "(could not read " + path + ": " + error.Message + ")"; }
        }

        private static string Tail(string text, int lines)
        {
            if (string.IsNullOrEmpty(text)) return "(the window log is empty)";
            string[] all = text.Replace("\r\n", "\n").Split('\n');
            int start = all.Length > lines ? all.Length - lines : 0;
            return string.Join("\n", all, start, all.Length - start);
        }
    }

    // The review window. It sends nothing by itself: the report sits in a read-only box, and only the GitHub button
    // can leave the PC - into a browser page where the player still has to press Submit.
    internal sealed class ReportForm : Form
    {
        private readonly string report;
        private readonly Label hint = new Label();
        private string savedPath;
        public bool OpenedIssue;

        public ReportForm(string report)
        {
            this.report = report;
            Text = "Report a crash - nothing sent yet";
            Width = 840;
            Height = 640;
            MinimumSize = new Size(700, 540);
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Segoe UI", 9f);
            BackColor = Color.FromArgb(24, 22, 30);
            ForeColor = Color.Gainsboro;

            Label privacy = new Label();
            privacy.Text = BugReport.PrivacyNote;
            privacy.SetBounds(16, 12, 790, 56);
            privacy.ForeColor = Color.FromArgb(214, 170, 255);
            privacy.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(privacy);

            Label caption = new Label();
            caption.Text = "This is the whole report - it was just assembled from the files already on this PC:";
            caption.SetBounds(16, 74, 790, 18);
            Controls.Add(caption);

            TextBox preview = new TextBox();
            preview.SetBounds(16, 96, 790, 420);
            preview.Multiline = true;
            preview.ReadOnly = true;
            preview.ScrollBars = ScrollBars.Both;
            preview.WordWrap = false;
            preview.Text = report;
            preview.BackColor = Color.FromArgb(18, 16, 22);
            preview.ForeColor = Color.Gainsboro;
            preview.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            Controls.Add(preview);

            hint.SetBounds(16, 522, 790, 34);
            hint.ForeColor = Color.Gray;
            hint.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            hint.Text = "Nothing is sent from this window. \"Open GitHub issue\" only opens a draft in your browser; " +
                        "the report is posted only if you press Submit there.";
            Controls.Add(hint);

            Button github = new Button();
            github.Text = "Yes - open a GitHub issue...";
            github.SetBounds(16, 560, 230, 30);
            github.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
            github.Click += delegate { OpenGithub(); };
            Controls.Add(github);

            Button save = new Button();
            save.Text = "Save report...";
            save.SetBounds(254, 560, 130, 30);
            save.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
            save.Click += delegate { SaveAs(); };
            Controls.Add(save);

            Button copy = new Button();
            copy.Text = "Copy";
            copy.SetBounds(392, 560, 90, 30);
            copy.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
            copy.Click += delegate
            {
                try { Clipboard.SetText(report); hint.Text = "Report copied to the clipboard."; }
                catch (Exception error) { hint.Text = "Could not copy: " + error.Message; }
            };
            Controls.Add(copy);

            Button close = new Button();
            close.Text = "No thanks - close";
            close.SetBounds(660, 560, 146, 30);
            close.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
            close.DialogResult = DialogResult.Cancel;
            Controls.Add(close);
            CancelButton = close;
        }

        private string EnsureSaved()
        {
            if (savedPath != null) return savedPath;
            try { savedPath = BugReport.Save(report, null); }
            catch (Exception error) { hint.Text = "Could not save the report: " + error.Message; }
            return savedPath;
        }

        private void OpenGithub()
        {
            string path = EnsureSaved();
            try
            {
                Process.Start(new ProcessStartInfo(BugReport.IssueUrl(report)) { UseShellExecute = true });
                OpenedIssue = true;
                MessageBox.Show(this,
                    "Your browser is opening a draft issue. Nothing has been posted yet - press Submit there to send it." +
                    (path != null ? "\n\nThe full report is saved at:\n" + path +
                        "\nAttach that file to the issue so the whole log travels with it." : ""),
                    "Nothing sent yet", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception error)
            {
                MessageBox.Show(this, "Could not open the browser: " + error.Message +
                    (path != null ? "\n\nThe report is saved at:\n" + path : ""),
                    "Nothing sent", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            DialogResult = DialogResult.OK;
            Close();
        }

        private void SaveAs()
        {
            string suggested = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                "MMOOT-report-" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".txt");
            using (SaveFileDialog dialog = new SaveFileDialog())
            {
                dialog.Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*";
                dialog.FileName = suggested;
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    File.WriteAllText(dialog.FileName, report);
                    savedPath = dialog.FileName;
                    hint.Text = "Report saved to " + dialog.FileName + " - still nothing sent.";
                }
                catch (Exception error) { hint.Text = "Could not save: " + error.Message; }
            }
        }
    }

    internal struct RomCheckResult
    {
        public bool Ok;
        public string Message;
        public string GameCode;
        public long Size;
    }

    internal static class RomCheck
    {
        private const int HeaderSize = 0x40;

        public static string Describe(string path, RomKind kind)
        {
            RomCheckResult r = Check(path, kind);
            return (r.Ok ? "OK   " : "FAIL ") + path + Environment.NewLine + "     " + r.Message;
        }

        // Reads the N64 header: magic (three byte orders), game code at 0x3B ("NZLE" is Majora's Mask US,
        // "CZLE" is Ocarina of Time US), and size. Compressed and decompressed images both keep the header.
        public static RomCheckResult Check(string path, RomKind kind)
        {
            RomCheckResult result = new RomCheckResult();
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    result.Message = "file not found";
                    return result;
                }
                long length = new FileInfo(path).Length;
                result.Size = length;
                if (length < 0x1000000L || length > 0x8000000L)
                {
                    result.Message = string.Format("that file is {0:N0} bytes; a Nintendo 64 ROM is 16-128 MB", length);
                    return result;
                }

                byte[] header = new byte[HeaderSize];
                using (FileStream stream = File.OpenRead(path))
                {
                    if (stream.Read(header, 0, HeaderSize) != HeaderSize)
                    {
                        result.Message = "file too short to hold a Nintendo 64 header";
                        return result;
                    }
                }

                bool validMagic =
                    (header[0] == 0x80 && header[1] == 0x37 && header[2] == 0x12 && header[3] == 0x40) || // .z64 big endian
                    (header[0] == 0x37 && header[1] == 0x80 && header[2] == 0x40 && header[3] == 0x12) || // .v64 byte swapped
                    (header[0] == 0x40 && header[1] == 0x12 && header[2] == 0x37 && header[3] == 0x80);   // .n64 little endian
                if (!validMagic)
                {
                    result.Message = "not a Nintendo 64 ROM (bad signature at offset 0)";
                    return result;
                }

                // Cartridge ID lives at 0x3B..0x3D in the big-endian image; byte-swapped formats keep those bytes
                // swapped pairwise, so try both readings.
                string codeBE = Encoding.ASCII.GetString(header, 0x3B, 3);
                string codeSwap = "" + (char)header[0x3C] + (char)header[0x3B] + (char)header[0x3D];
                if (codeBE.Length != 3) codeBE = codeBE.PadRight(3);
                string code = codeBE;
                if (!IsGameCode(code, kind) && IsGameCode(codeSwap, kind)) code = codeSwap;
                result.GameCode = code;

                if (!IsGameCode(code, kind))
                {
                    string wanted = kind == RomKind.MajorasMask ? "NZLE (Majora's Mask, US)" : "CZLE (Ocarina of Time, US)";
                    result.Message = "this looks like cartridge \"" + Printable(code) + "\"; " + wanted + " is required";
                    return result;
                }

                // Version byte at 0x3F (0 = the 1.0 / rev 1 build the project is developed against). Reported, not
                // enforced: a good USA cart is what matters, and a newer print still boots.
                byte revision = header[0x3F];
                result.Ok = true;
                result.Message = (kind == RomKind.MajorasMask ? "Majora's Mask" : "Ocarina of Time") +
                                 " US, revision byte " + revision + ", " + string.Format("{0:N0} bytes", length);
                return result;
            }
            catch (Exception error)
            {
                result.Message = error.Message;
                return result;
            }
        }

        private static bool IsGameCode(string code, RomKind kind)
        {
            if (code == null || code.Length != 3) return false;
            if (kind == RomKind.MajorasMask) return code == "NZL" || code == "NZS";
            return code == "CZL";
        }

        private static string Printable(string code)
        {
            StringBuilder text = new StringBuilder();
            foreach (char c in code)
                text.Append(c >= 0x20 && c < 0x7F ? c : '?');
            return text.ToString();
        }
    }

    internal struct UpdateInfo
    {
        public bool Ok;
        public string Tag;
        public string Url;
        public string AssetName;
        public string AssetUrl;
        public string Error;

        public bool IsNewerThan(string installed)
        {
            if (!Ok || Tag.Length == 0) return false;
            if (string.IsNullOrEmpty(installed)) return true;
            return string.Compare(Normalize(Tag), Normalize(installed), StringComparison.OrdinalIgnoreCase) != 0;
        }

        private static string Normalize(string tag)
        {
            string value = tag.Trim();
            while (value.Length != 0 && (value[0] == 'v' || value[0] == 'V')) value = value.Substring(1);
            return value;
        }

        public string Describe(string installed)
        {
            if (!Ok) return "Update check failed: " + Error;
            string text = "Latest release: " + Tag + (string.IsNullOrEmpty(installed) ? "" : "  (installed: " + installed + ")");
            if (AssetUrl != null && AssetUrl.Length != 0) text += Environment.NewLine + "Download: " + AssetName;
            return text;
        }
    }

    internal static class GithubCheck
    {
        static GithubCheck()
        {
            try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; } // TLS 1.2
            catch { }
        }

        public static UpdateInfo Latest()
        {
            UpdateInfo info = new UpdateInfo();
            string releaseJson = null;
            string releaseError = null;

            // A repository with no published release answers 404 here. That is not a failure: the tags below still
            // tell the user whether their install is behind.
            try { releaseJson = Get(Program.LatestReleaseApi); }
            catch (Exception error) { releaseError = Friendly(error); }

            if (releaseJson != null)
            {
                Match tag = Regex.Match(releaseJson, "\"tag_name\"\\s*:\\s*\"([^\"]*)\"");
                if (tag.Success)
                {
                    info.Ok = true;
                    info.Tag = tag.Groups[1].Value;
                    Match url = Regex.Match(releaseJson, "\"html_url\"\\s*:\\s*\"([^\"]*)\"");
                    info.Url = url.Success ? url.Groups[1].Value : Program.RepoUrl;

                    // First downloadable asset (a Windows zip is what the release should carry).
                    MatchCollection assets = Regex.Matches(releaseJson,
                        "\"browser_download_url\"\\s*:\\s*\"([^\"]*)\"", RegexOptions.None);
                    foreach (Match asset in assets)
                    {
                        string href = asset.Groups[1].Value;
                        if (href.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                            href.EndsWith(".7z", StringComparison.OrdinalIgnoreCase))
                        {
                            info.AssetUrl = href;
                            info.AssetName = href.Substring(href.LastIndexOf('/') + 1);
                            break;
                        }
                    }
                    if (info.AssetUrl == null && assets.Count != 0)
                    {
                        string href = assets[0].Groups[1].Value;
                        info.AssetUrl = href;
                        info.AssetName = href.Substring(href.LastIndexOf('/') + 1);
                    }
                    return info;
                }
            }

            // No published release yet: fall back to the newest tag, which still tells the user whether their clone
            // is behind.
            try
            {
                string tags = Get(Program.TagsApi);
                Match name = Regex.Match(tags, "\"name\"\\s*:\\s*\"([^\"]*)\"");
                if (name.Success)
                {
                    info.Ok = true;
                    info.Tag = name.Groups[1].Value;
                    info.Url = Program.RepoUrl;
                    return info;
                }
                info.Error = releaseError ?? ("no release or tag found in " + Program.RepoUrl);
            }
            catch (Exception error)
            {
                info.Error = releaseError ?? Friendly(error);
            }
            return info;
        }

        private static string Get(string url)
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.UserAgent = "MMOOT-Setup/" + Program.InstallerVersion;
            request.Accept = "application/vnd.github+json";
            request.Timeout = 15000;
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            using (Stream stream = response.GetResponseStream())
            using (StreamReader reader = new StreamReader(stream))
                return reader.ReadToEnd();
        }

        public static string Friendly(Exception error)
        {
            WebException web = error as WebException;
            if (web != null && web.Response is HttpWebResponse)
            {
                int code = (int)((HttpWebResponse)web.Response).StatusCode;
                if (code == 404) return "no public release found (or the repository is private)";
                return "GitHub answered HTTP " + code;
            }
            return error.Message;
        }
    }

    // Recompiles the project and configures it: the project's own tools/build_pc.ps1 (WSL clang for the game code,
    // VS LLVM + Ninja for the PC host), then the freshly linked executable is installed as the play binary and the
    // newest Hyrule content image is put where the game looks for it.
    internal static class Builder
    {
        // The source tree: walk up from the installer until MMOOT/tools/build_pc.ps1 turns up.
        public static string DetectSource()
        {
            try
            {
                DirectoryInfo dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
                for (int level = 0; level < 6 && dir != null; level++, dir = dir.Parent)
                {
                    string candidate = Path.Combine(dir.FullName, "MMOOT", "tools", "build_pc.ps1");
                    if (File.Exists(candidate)) return dir.FullName;
                }
            }
            catch { }
            return "";
        }

        public static string BuildScript(string source)
        {
            return Path.Combine(source, "MMOOT", "tools", "build_pc.ps1");
        }

        public static string BuildNinja(string source)
        {
            return Path.Combine(source, "MMOOT", "pc", "Zelda64Recomp", "build-ninja");
        }

        // What the build needs, checked so the log says which piece is missing instead of failing deep inside ninja.
        public static string[] Preflight(string source)
        {
            List<string> problems = new List<string>();
            if (!File.Exists(BuildScript(source))) problems.Add("build_pc.ps1 not found - is this the MMOOT source folder?");
            if (!Directory.Exists(BuildNinja(source))) problems.Add("build-ninja folder missing - configure the CMake project first");

            string wsl = FindOnPath("wsl.exe");
            if (wsl == null) problems.Add("WSL not found (the game code is compiled with Ubuntu's clang)");
            else if (!RunsOk("wsl.exe", "-d Ubuntu -u root -e bash -lc \"true\""))  // --list prints UTF-16, so ask the distro instead
                problems.Add("no runnable WSL 'Ubuntu' distribution");
            if (FindOnPath("python.exe") == null && FindOnPath("py.exe") == null)
                problems.Add("Python not found (the content tools are Python)");
            if (FindOnPath("cmake.exe") == null) problems.Add("CMake not found");
            if (!NinjaFound()) problems.Add("Ninja not found (PATH, CMake, or Visual Studio's bundled copy)");
            if (!LlmFound()) problems.Add("Visual Studio LLVM (clang-cl) not found - build_pc.ps1 expects VS 18 Community's " +
                                           "VC\\Tools\\Llvm\\x64\\bin");
            return problems.ToArray();
        }

        private static bool LlmFound()
        {
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string[] versions = { "18", "2022", "17" };
            string[] editions = { "Community", "Professional", "Enterprise", "BuildTools" };
            foreach (string version in versions)
                foreach (string edition in editions)
                {
                    string path = Path.Combine(programFiles, "Microsoft Visual Studio", version, edition,
                                               "VC", "Tools", "Llvm", "x64", "bin", "clang-cl.exe");
                    if (File.Exists(path)) return true;
                }
            return false;
        }

        // Visual Studio bundles ninja under Common7\IDE; the project's build finds it there, not on PATH.
        private static bool NinjaFound()
        {
            if (FindOnPath("ninja.exe") != null) return true;
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (File.Exists(Path.Combine(programFiles, "CMake", "bin", "ninja.exe"))) return true;
            string[] versions = { "18", "2022", "17" };
            string[] editions = { "Community", "Professional", "Enterprise", "BuildTools" };
            foreach (string version in versions)
                foreach (string edition in editions)
                    if (File.Exists(Path.Combine(programFiles, "Microsoft Visual Studio", version, edition, "Common7", "IDE",
                                                 "CommonExtensions", "Microsoft", "CMake", "Ninja", "ninja.exe"))) return true;
            return false;
        }

        private static bool RunsOk(string exe, string arguments)
        {
            try
            {
                ProcessStartInfo info = new ProcessStartInfo(exe, arguments);
                info.UseShellExecute = false;
                info.RedirectStandardOutput = true;
                info.RedirectStandardError = true;
                info.CreateNoWindow = true;
                using (Process process = Process.Start(info))
                {
                    process.StandardOutput.ReadToEnd();
                    process.StandardError.ReadToEnd();
                    if (!process.WaitForExit(20000)) { try { process.Kill(); } catch { } return false; }
                    return process.ExitCode == 0;
                }
            }
            catch { return false; }
        }

        private static string FindOnPath(string exe)
        {
            string path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (string part in path.Split(';'))
            {
                if (part.Trim().Length == 0) continue;
                try
                {
                    string candidate = Path.Combine(part.Trim(), exe);
                    if (File.Exists(candidate)) return candidate;
                }
                catch { }
            }
            return null;
        }

        private static string RunCaptured(string exe, string arguments)
        {
            try
            {
                ProcessStartInfo info = new ProcessStartInfo(exe, arguments);
                info.UseShellExecute = false;
                info.RedirectStandardOutput = true;
                info.RedirectStandardError = true;
                info.CreateNoWindow = true;
                using (Process process = Process.Start(info))
                {
                    string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
                    process.WaitForExit(15000);
                    return output;
                }
            }
            catch { return null; }
        }

        // Runs build_pc.ps1 -BuildOnly and streams every line it prints into the log.
        public static bool Recompile(string source, Action<string> append)
        {
            try
            {
                ProcessStartInfo info = new ProcessStartInfo("powershell.exe",
                    "-NoProfile -ExecutionPolicy Bypass -File \"" + BuildScript(source) + "\" -BuildOnly");
                info.WorkingDirectory = source;
                info.UseShellExecute = false;
                info.RedirectStandardOutput = true;
                info.RedirectStandardError = true;
                info.CreateNoWindow = true;
                info.StandardOutputEncoding = Encoding.UTF8;
                info.StandardErrorEncoding = Encoding.UTF8;
                using (Process process = Process.Start(info))
                {
                    process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e)
                    {
                        if (e.Data != null) append(e.Data);
                    };
                    process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e)
                    {
                        if (e.Data != null) append("! " + e.Data);
                    };
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    process.WaitForExit();
                    append("build_pc.ps1 exit code " + process.ExitCode);
                    return process.ExitCode == 0;
                }
            }
            catch (Exception error)
            {
                append("could not run the build: " + error.Message);
                return false;
            }
        }

        // The freshly linked executable becomes the play binary the launchers start.
        private static void CopyRuntimeFiles(string fromDir, string toDir, Action<string> append)
        {
            foreach (string required in new string[] { "SDL2.dll", "dxcompiler.dll", "dxil.dll" })
                if (!File.Exists(Path.Combine(fromDir, required)))
                    throw new FileNotFoundException("The game package is incomplete: " + required + " is missing.", required);
            if (string.Equals(Path.GetFullPath(fromDir), Path.GetFullPath(toDir), StringComparison.OrdinalIgnoreCase)) return;
            foreach (string dll in Directory.GetFiles(fromDir, "*.dll"))
            {
                File.Copy(dll, Path.Combine(toDir, Path.GetFileName(dll)), true);
                append("installed runtime " + Path.GetFileName(dll));
            }
        }

        public static bool InstallExecutable(string source, Action<string> append)
        {
            string dir = BuildNinja(source);
            string built = Path.Combine(dir, "Zelda64Recompiled.exe");
            string play = Path.Combine(dir, "Zelda64Recompiled-ootplay.exe");
            if (!File.Exists(built)) { append("no freshly built executable at " + built); return false; }
            try
            {
                CopyRuntimeFiles(dir, dir, append);
                if (File.Exists(play))
                {
                    string backup = play + ".pre_setup_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".bak";
                    File.Copy(play, backup, true);
                    append("previous play binary kept as " + Path.GetFileName(backup));
                }
                File.Copy(built, play, true);
                append("installed " + play + " (" + new FileInfo(play).Length.ToString("N0") + " bytes) - this is the file " +
                       "pc\\play_hyrule.cmd and pc\\play_deku_tree.cmd start");
                // The repo also keeps a copy one level up; keep it identical so an install can never leave the launcher and
                // the visible copy on different builds (that is exactly how a stale play binary survived several rounds).
                string sibling = Path.Combine(source, "MMOOT", "pc", "Zelda64Recomp", "Zelda64Recompiled-ootplay.exe");
                if (File.Exists(sibling) && !string.Equals(sibling, play, StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(built, sibling, true);
                    CopyRuntimeFiles(dir, Path.GetDirectoryName(sibling), append);
                    append("mirrored to " + sibling);
                }
                return true;
            }
            catch (Exception error)
            {
                append("could not install the executable: " + error.Message);
                return false;
            }
        }

        // Newest content image the project has built: MMOOT/build/m3/<tag>/hyrule.z64.
        public static string NewestImage(string source)
        {
            try
            {
                string root = Path.Combine(source, "MMOOT", "build", "m3");
                if (!Directory.Exists(root)) return "";
                string best = "";
                DateTime bestTime = DateTime.MinValue;
                foreach (string file in Directory.GetFiles(root, "hyrule*.z64", SearchOption.AllDirectories))
                {
                    DateTime time = File.GetLastWriteTimeUtc(file);
                    if (time > bestTime) { bestTime = time; best = file; }
                }
                return best;
            }
            catch { return ""; }
        }

        // The game loads the Hyrule content image from its app folder under this name (mmoot_content.cpp).
        public static bool InstallImage(string source, string gameFolder, Action<string> append)
        {
            string image = NewestImage(source);
            if (image.Length == 0) { append("no Hyrule image built yet (MMOOT/build/m3/*/hyrule.z64) - run the content tools first"); return false; }
            try
            {
                string destination = Path.Combine(gameFolder, "mmoot.hyrule.opening.preview.1.z64");
                // Never overwrite a working image: the executable only accepts the exact build it was compiled against
                // (MMOOT_OOT_RUNTIME_HASH) and deletes a stranger. A fresh install gets the newest one; an existing
                // install is left alone until the content tools rebuild for this binary.
                if (File.Exists(destination))
                {
                    append("keeping the content image already in the game folder (" + new FileInfo(destination).Length.ToString("N0") +
                           " bytes); rebuild it with the MMOOT/tools content scripts when the patches change");
                    return true;
                }
                File.Copy(image, destination, true);
                append("installed content image from " + image.Replace(source + Path.DirectorySeparatorChar, ""));
                return true;
            }
            catch (Exception error)
            {
                append("could not install the content image: " + error.Message);
                return false;
            }
        }
    }

    // Installs a game that was built on another PC. A machine without WSL, LLVM and CMake cannot compile the
    // project, so Fuse copies the finished game instead: the executable, its runtime DLLs, the assets and the Hyrule
    // content image, on top of the two ROMs. Nothing is downloaded and nothing Nintendo-made comes from here - the
    // files come from the player's own build folder.
    internal static class GamePackage
    {
        public static bool Install(string source, string gameFolder, Action<string> append)
        {
            string unpacked = null;
            try
            {
                string fromDir = source;
                if (!string.IsNullOrEmpty(source) && File.Exists(source) &&
                    source.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    unpacked = Path.Combine(Path.GetTempPath(), "MMOOT-game-" +
                        DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture));
                    Directory.CreateDirectory(unpacked);
                    ZipFile.ExtractToDirectory(source, unpacked);
                    append("unpacked " + Path.GetFileName(source) + " (" +
                           new FileInfo(source).Length.ToString("N0") + " bytes)");
                    fromDir = SingleRoot(unpacked);
                }
                if (string.IsNullOrEmpty(fromDir) || !Directory.Exists(fromDir))
                {
                    append("Fuse needs the game package (.zip) from the PC that built the game (pick it above), or use Build and configure here.");
                    return false;
                }
                string exe = Path.Combine(fromDir, "Zelda64Recompiled-ootplay.exe");
                if (!File.Exists(exe)) exe = Path.Combine(fromDir, "Zelda64Recompiled.exe");
                if (!File.Exists(exe))
                {
                    append("No Zelda64Recompiled-ootplay.exe in " + fromDir + " - pick the .zip packed on the PC that built " +
                           "the game, or its build-ninja folder.");
                    return false;
                }
                if (string.IsNullOrEmpty(gameFolder))
                {
                    append("Pick an install folder.");
                    return false;
                }

                Directory.CreateDirectory(gameFolder);
                CopyRuntimeFiles(fromDir, gameFolder, append);

                string play = Path.Combine(gameFolder, "Zelda64Recompiled-ootplay.exe");
                if (File.Exists(play))
                {
                    string backup = play + ".pre_fuse_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".bak";
                    File.Copy(play, backup, true);
                    append("previous game kept as " + Path.GetFileName(backup));
                }
                File.Copy(exe, play, true);
                append("installed " + play + " (" + new FileInfo(play).Length.ToString("N0") + " bytes)");

                foreach (string folder in new string[] { "assets", "shaders", "mods" })
                {
                    string sourceDir = Path.Combine(fromDir, folder);
                    if (!Directory.Exists(sourceDir)) continue;
                    CopyTree(sourceDir, Path.Combine(gameFolder, folder));
                    append("copied " + folder + "\\");
                }

                string image = FindImage(fromDir);
                string destination = Path.Combine(gameFolder, "mmoot.hyrule.opening.preview.1.z64");
                if (image == null)
                {
                    append("no Hyrule content image in that folder; if the game asks for one, copy the hyrule.z64 built on that " +
                           "PC into " + gameFolder + " as " + Path.GetFileName(destination));
                }
                else if (File.Exists(destination))
                {
                    append("keeping the content image already installed (" +
                           new FileInfo(destination).Length.ToString("N0") + " bytes)");
                }
                else
                {
                    File.Copy(image, destination, true);
                    append("installed content image from " + Path.GetFileName(image));
                }
                return true;
            }
            catch (Exception error)
            {
                append("Fuse failed: " + error.Message);
                return false;
            }
            finally
            {
                if (unpacked != null) { try { Directory.Delete(unpacked, true); } catch { } }
            }
        }

        // A zip that wraps everything in one folder is the normal shape; use that folder rather than its parent.
        private static string SingleRoot(string dir)
        {
            try
            {
                string[] files = Directory.GetFiles(dir);
                string[] dirs = Directory.GetDirectories(dir);
                if (files.Length == 0 && dirs.Length == 1) return dirs[0];
            }
            catch { }
            return dir;
        }

        // One file the other PC can pick: the executable, its DLLs, assets, shaders, mods and the content image.
        public static string Pack(string gameDir, string imagePath, string zipPath, Action<string> append)
        {
            try
            {
                if (string.IsNullOrEmpty(gameDir) || !Directory.Exists(gameDir))
                {
                    append("Pack needs the game folder (build-ninja) built on this PC.");
                    return null;
                }
                string exe = Path.Combine(gameDir, "Zelda64Recompiled-ootplay.exe");
                if (!File.Exists(exe)) exe = Path.Combine(gameDir, "Zelda64Recompiled.exe");
                if (!File.Exists(exe)) { append("No game executable in " + gameDir + "."); return null; }
                foreach (string required in new string[] { "SDL2.dll", "dxcompiler.dll", "dxil.dll" })
                    if (!File.Exists(Path.Combine(gameDir, required)))
                    {
                        append("That folder is incomplete: " + required + " is missing.");
                        return null;
                    }

                string image = imagePath != null && File.Exists(imagePath) ? imagePath : FindImage(gameDir);
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(zipPath)));
                using (FileStream stream = new FileStream(zipPath, FileMode.Create))
                using (ZipArchive archive = new ZipArchive(stream, ZipArchiveMode.Create))
                {
                    // Only the game itself: build-ninja also holds every old smoke build, which must not travel.
                    AddFile(archive, exe, "Zelda64Recompiled-ootplay.exe");
                    foreach (string dll in new string[] { "SDL2.dll", "dxcompiler.dll", "dxil.dll" })
                        AddFile(archive, Path.Combine(gameDir, dll), dll);
                    foreach (string folder in new string[] { "assets", "shaders", "mods" })
                    {
                        string source = Path.Combine(gameDir, folder);
                        if (!Directory.Exists(source)) continue;
                        AddTree(archive, source, folder);
                    }
                    if (image != null) AddFile(archive, image, "mmoot.hyrule.opening.preview.1.z64");
                }
                FileInfo packed = new FileInfo(zipPath);
                append("packed " + packed.Length.ToString("N0") + " bytes into " + zipPath);
                append(image != null
                    ? "included content image " + Path.GetFileName(image)
                    : "no Hyrule image was included - pass its path as the third argument");
                return zipPath;
            }
            catch (Exception error)
            {
                append("Pack failed: " + error.Message);
                return null;
            }
        }

        private static void AddTree(ZipArchive archive, string dir, string prefix)
        {
            foreach (string file in Directory.GetFiles(dir))
                AddFile(archive, file, Path.Combine(prefix, Path.GetFileName(file)));
            foreach (string sub in Directory.GetDirectories(dir))
                AddTree(archive, sub, Path.Combine(prefix, Path.GetFileName(sub)));
        }

        private static void AddFile(ZipArchive archive, string path, string entryName)
        {
            ZipArchiveEntry entry = archive.CreateEntry(entryName.Replace('\\', '/'), CompressionLevel.Optimal);
            using (Stream target = entry.Open())
            using (FileStream source = File.OpenRead(path))
                source.CopyTo(target);
        }

        private static void CopyRuntimeFiles(string fromDir, string toDir, Action<string> append)
        {
            foreach (string required in new string[] { "SDL2.dll", "dxcompiler.dll", "dxil.dll" })
                if (!File.Exists(Path.Combine(fromDir, required)))
                    throw new FileNotFoundException("That game folder is incomplete: " + required + " is missing.", required);
            int copied = 0;
            foreach (string dll in Directory.GetFiles(fromDir, "*.dll"))
            {
                File.Copy(dll, Path.Combine(toDir, Path.GetFileName(dll)), true);
                copied++;
            }
            append("installed " + copied + " runtime DLLs");
        }

        private static void CopyTree(string fromDir, string toDir)
        {
            Directory.CreateDirectory(toDir);
            foreach (string file in Directory.GetFiles(fromDir))
                File.Copy(file, Path.Combine(toDir, Path.GetFileName(file)), true);
            foreach (string dir in Directory.GetDirectories(fromDir))
                CopyTree(dir, Path.Combine(toDir, Path.GetFileName(dir)));
        }

        // The content image sits beside the executable when the build was packaged, or under build\m3\<tag> in a full tree.
        private static string FindImage(string fromDir)
        {
            string[] patterns = { "mmoot.hyrule*.z64", "hyrule*.z64" };
            foreach (string pattern in patterns)
            {
                string[] top = Directory.GetFiles(fromDir, pattern);
                if (top.Length != 0) return Newest(top);
            }
            try
            {
                string m3 = Path.Combine(fromDir, "build", "m3");
                if (Directory.Exists(m3))
                {
                    foreach (string pattern in patterns)
                    {
                        string[] found = Directory.GetFiles(m3, pattern, SearchOption.AllDirectories);
                        if (found.Length != 0) return Newest(found);
                    }
                }
            }
            catch { }
            return null;
        }

        private static string Newest(string[] files)
        {
            string best = files[0];
            DateTime bestTime = File.GetLastWriteTimeUtc(best);
            foreach (string file in files)
            {
                DateTime time = File.GetLastWriteTimeUtc(file);
                if (time > bestTime) { best = file; bestTime = time; }
            }
            return best;
        }
    }

    // The install itself, shared by the window and by `MMOOTSetup.exe --install <mm> <oot> <folder>` so it can be
    // run and checked without clicking.
    internal static class Installer
    {
        public static bool Run(string mmSource, string ootSource, string dir, Action<string> append)
        {
            try
            {
                RomCheckResult mm = RomCheck.Check(mmSource, RomKind.MajorasMask);
                RomCheckResult oot = RomCheck.Check(ootSource, RomKind.Ocarina);
                if (!mm.Ok) { append("Majora's Mask ROM: " + mm.Message); return false; }
                if (!oot.Ok) { append("Ocarina of Time ROM: " + oot.Message); return false; }
                if (string.IsNullOrEmpty(dir)) { append("Pick an install folder."); return false; }

                Directory.CreateDirectory(dir);
                Copy(mmSource, Path.Combine(dir, Program.MmRomName), append);
                Copy(ootSource, Path.Combine(dir, Program.OotRomName), append);

                File.WriteAllText(Path.Combine(dir, "mmoot_version.txt"), Program.InstallerVersion + Environment.NewLine);
                File.WriteAllText(Path.Combine(dir, "mmoot_roms.txt"),
                    "Majora's Mask: " + mm.Message + Environment.NewLine +
                    "Ocarina of Time: " + oot.Message + Environment.NewLine +
                    "Installed by MMOOTSetup " + Program.InstallerVersion + " on " +
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + Environment.NewLine);

                append("ROMs installed into " + dir);
                append("  " + Program.MmRomName + "  (" + mm.Message + ")");
                append("  " + Program.OotRomName + "  (" + oot.Message + ")");
                return true;
            }
            catch (Exception error)
            {
                append("Install failed: " + error.Message);
                return false;
            }
        }

        private static void Copy(string source, string destination, Action<string> append)
        {
            string fullSource = Path.GetFullPath(source);
            string fullDestination = Path.GetFullPath(destination);
            if (string.Equals(fullSource, fullDestination, StringComparison.OrdinalIgnoreCase))
            {
                append("Already in place: " + Path.GetFileName(destination));
                return;
            }
            File.Copy(fullSource, fullDestination, true);
            append("Copied " + Path.GetFileName(destination));
        }
    }

    // The whole job in one place: ROMs into the game folder, recompile, install the executable and the image.
    internal static class BuilderFlow
    {
        public static bool Run(string source, string mmRom, string ootRom, Action<string> append)
        {
            string gameFolder = Program.DefaultInstallDir;
            append("MMOOT source: " + source);
            append("Game folder:  " + gameFolder);

            if (!Installer.Run(mmRom, ootRom, gameFolder, append)) return false;

            string[] problems = Builder.Preflight(source);
            foreach (string problem in problems) append("missing: " + problem);
            if (problems.Length != 0) append("continuing anyway - the compiler will say more if it really cannot build");

            append("--- recompiling (MMOOT/tools/build_pc.ps1 -BuildOnly) ---");
            if (!Builder.Recompile(source, append)) { append("recompile failed; nothing was installed"); return false; }

            bool ok = Builder.InstallExecutable(source, append);
            ok &= Builder.InstallImage(source, gameFolder, append);
            if (ok) append("done: the game is built and configured for these ROMs");
            return ok;
        }
    }

    internal sealed class SetupForm : Form
    {
        private readonly TextBox mmPath = new TextBox();
        private readonly TextBox ootPath = new TextBox();
        private readonly TextBox installDir = new TextBox();
        private readonly TextBox sourceDir = new TextBox();
        private readonly Label mmStatus = new Label();
        private readonly Label ootStatus = new Label();
        private readonly Label updateStatus = new Label();
        private readonly TextBox log = new TextBox();
        private readonly Button installButton = new Button();
        private readonly Button updateButton = new Button();
        private readonly Button openRelease = new Button();
        private readonly TextBox gameDir = new TextBox();
        private UpdateInfo lastUpdate;
        private string lastUpdateHref;

        public SetupForm()
        {
            Text = "LoZ: Masks of Time - setup";
            Width = 760;
            Height = 620;
            MinimumSize = new Size(680, 580);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9f);
            BackColor = Color.FromArgb(24, 22, 30);
            ForeColor = Color.Gainsboro;

            Panel body = new Panel();
            body.Dock = DockStyle.Fill;
            body.AutoScroll = true;
            body.Padding = new Padding(16);
            body.BackColor = BackColor;
            Controls.Add(body);

            int y = 12;
            Label title = new Label();
            title.Text = "LoZ: Masks of Time";
            title.Font = new Font("Segoe UI Semibold", 15f);
            title.ForeColor = Color.FromArgb(214, 170, 255);
            title.SetBounds(16, y, 640, 30);
            body.Controls.Add(title);
            y += 34;

            Label blurb = new Label();
            blurb.Text = "Majora's Mask slowly eating and absorbing Hyrule. Pick the two Nintendo 64 ROMs from files you own;\n" +
                         "nothing is bundled here. The repository is recompiled and the result is installed as the play binary.";
            blurb.SetBounds(16, y, 700, 40);
            body.Controls.Add(blurb);
            y += 48;

            Label sourceLabel = new Label();
            sourceLabel.Text = "MMOOT source folder (this repository - it gets recompiled)";
            sourceLabel.SetBounds(16, y, 500, 18);
            body.Controls.Add(sourceLabel);
            y += 20;
            sourceDir.SetBounds(16, y, 590, 24);
            string detectedSource = Builder.DetectSource();
            sourceDir.Text = detectedSource;
            body.Controls.Add(sourceDir);
            Button browseSource = new Button();
            browseSource.Text = "Browse...";
            browseSource.SetBounds(614, y - 1, 90, 26);
            browseSource.Click += delegate
            {
                using (FolderBrowserDialog dialog = new FolderBrowserDialog())
                {
                    dialog.SelectedPath = sourceDir.Text;
                    if (dialog.ShowDialog(this) == DialogResult.OK) sourceDir.Text = dialog.SelectedPath;
                }
            };
            body.Controls.Add(browseSource);
            y += 38;

            y = AddRomRow(body, y, "Majora's Mask ROM (US rev 1)", mmPath, mmStatus, RomKind.MajorasMask);
            y = AddRomRow(body, y, "Ocarina of Time ROM (US 1.0)", ootPath, ootStatus, RomKind.Ocarina);

            Label dirLabel = new Label();
            dirLabel.Text = "Install folder";
            dirLabel.SetBounds(16, y, 200, 18);
            body.Controls.Add(dirLabel);
            y += 20;
            installDir.SetBounds(16, y, 590, 24);
            installDir.Text = Program.DefaultInstallDir;
            body.Controls.Add(installDir);
            Button browseDir = new Button();
            browseDir.Text = "Browse...";
            browseDir.SetBounds(614, y - 1, 90, 26);
            browseDir.Click += delegate
            {
                using (FolderBrowserDialog dialog = new FolderBrowserDialog())
                {
                    dialog.SelectedPath = installDir.Text;
                    if (dialog.ShowDialog(this) == DialogResult.OK) installDir.Text = dialog.SelectedPath;
                }
            };
            body.Controls.Add(browseDir);
            y += 38;

            Label gameLabel = new Label();
            gameLabel.Text = "Game package from the PC that built the game (the .zip it packed; empty = build here)";
            gameLabel.SetBounds(16, y, 700, 18);
            body.Controls.Add(gameLabel);
            y += 20;
            gameDir.SetBounds(16, y, 590, 24);
            body.Controls.Add(gameDir);
            Button browseGame = new Button();
            browseGame.Text = "Browse...";
            browseGame.SetBounds(614, y - 1, 90, 26);
            browseGame.Click += delegate
            {
                using (OpenFileDialog dialog = new OpenFileDialog())
                {
                    dialog.Title = "Pick the game package (MMOOT-game.zip) made on the PC that built the game";
                    dialog.Filter = "MMOOT game package (*.zip)|*.zip|All files (*.*)|*.*";
                    dialog.CheckFileExists = true;
                    if (dialog.ShowDialog(this) == DialogResult.OK) gameDir.Text = dialog.FileName;
                }
            };
            body.Controls.Add(browseGame);
            y += 38;

            installButton.Text = "Fuse (ROMs + game folder)";
            installButton.SetBounds(16, y, 210, 32);
            installButton.Click += delegate { Fuse(); };
            body.Controls.Add(installButton);

            Button buildButton = new Button();
            buildButton.Text = "Build and configure";
            buildButton.SetBounds(236, y, 160, 32);
            buildButton.Click += delegate { BuildAll(); };
            body.Controls.Add(buildButton);

            Button romsOnly = new Button();
            romsOnly.Text = "ROMs only";
            romsOnly.SetBounds(406, y, 100, 32);
            romsOnly.Click += delegate { Install(); };
            body.Controls.Add(romsOnly);

            updateButton.Text = "Check for updates";
            updateButton.SetBounds(516, y, 150, 32);
            updateButton.Click += delegate { CheckUpdates(true); };
            body.Controls.Add(updateButton);
            y += 38;

            openRelease.Text = "Open release page";
            openRelease.SetBounds(16, y, 150, 32);
            openRelease.Enabled = false;
            openRelease.Click += delegate
            {
                string href = lastUpdateHref ?? Program.RepoUrl;
                try { Process.Start(new ProcessStartInfo(href) { UseShellExecute = true }); }
                catch (Exception error) { Append("cannot open browser: " + error.Message); }
            };
            body.Controls.Add(openRelease);
            y += 42;

            updateStatus.SetBounds(16, y, 700, 46);
            updateStatus.Text = "";
            body.Controls.Add(updateStatus);
            y += 50;

            Label reportNote = new Label();
            reportNote.Text = "Optional crash report - not data collection: nothing is recorded while you play, and nothing " +
                              "is sent unless you send it.";
            reportNote.ForeColor = Color.Gray;
            reportNote.SetBounds(16, y, 700, 18);
            body.Controls.Add(reportNote);
            y += 20;

            Button reportButton = new Button();
            reportButton.Text = "Report a crash... (optional, no data collection)";
            reportButton.SetBounds(16, y, 330, 28);
            reportButton.Click += delegate { ShowReportDialog(); };
            body.Controls.Add(reportButton);
            y += 34;

            log.SetBounds(16, y, 700, 150);
            log.Multiline = true;
            log.ReadOnly = true;
            log.ScrollBars = ScrollBars.Vertical;
            log.BackColor = Color.FromArgb(18, 16, 22);
            log.ForeColor = Color.Gainsboro;
            log.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            body.Controls.Add(log);

            Append("Installer " + Program.InstallerVersion + ". Game folder: " + Program.DefaultInstallDir);
            Append("Updates come from " + Program.RepoUrl);
            Shown += delegate
            {
                PickUpExisting();
                CheckUpdates(false);
            };
        }

        private int AddRomRow(Panel body, int y, string caption, TextBox box, Label status, RomKind kind)
        {
            Label label = new Label();
            label.Text = caption;
            label.SetBounds(16, y, 300, 18);
            body.Controls.Add(label);
            y += 20;

            box.SetBounds(16, y, 590, 24);
            body.Controls.Add(box);
            Button browse = new Button();
            browse.Text = "Browse...";
            browse.SetBounds(614, y - 1, 90, 26);
            browse.Click += delegate
            {
                using (OpenFileDialog dialog = new OpenFileDialog())
                {
                    dialog.Filter = "Nintendo 64 ROMs (*.z64;*.n64;*.v64)|*.z64;*.n64;*.v64|All files (*.*)|*.*";
                    dialog.CheckFileExists = true;
                    if (dialog.ShowDialog(this) == DialogResult.OK)
                    {
                        box.Text = dialog.FileName;
                        Validate(box, status, kind);
                    }
                }
            };
            body.Controls.Add(browse);
            y += 26;

            status.SetBounds(16, y, 700, 18);
            status.ForeColor = Color.Gray;
            body.Controls.Add(status);
            y += 26;

            TextBox captured = box;
            Label capturedStatus = status;
            captured.Leave += delegate { Validate(captured, capturedStatus, kind); };
            return y;
        }

        private void Validate(TextBox box, Label status, RomKind kind)
        {
            if (box.Text.Trim().Length == 0)
            {
                status.Text = "";
                return;
            }
            RomCheckResult result = RomCheck.Check(box.Text.Trim(), kind);
            status.Text = (result.Ok ? "OK  " : "Problem: ") + result.Message;
            status.ForeColor = result.Ok ? Color.FromArgb(150, 220, 150) : Color.FromArgb(240, 150, 150);
            UpdateInstallEnabled();
        }

        private void UpdateInstallEnabled()
        {
            // Fuse validates on click and says what is wrong, so it stays available even before both ROMs are picked.
            installButton.Enabled = true;
        }

        // A previous install leaves both ROMs in the folder; pick them up so an update does not ask again.
        private void PickUpExisting()
        {
            try
            {
                string dir = installDir.Text.Trim();
                string mm = Path.Combine(dir, Program.MmRomName);
                string oot = Path.Combine(dir, Program.OotRomName);
                if (mmPath.Text.Length == 0 && File.Exists(mm))
                {
                    mmPath.Text = mm;
                    Validate(mmPath, mmStatus, RomKind.MajorasMask);
                }
                if (ootPath.Text.Length == 0 && File.Exists(oot))
                {
                    ootPath.Text = oot;
                    Validate(ootPath, ootStatus, RomKind.Ocarina);
                }
                UpdateInstallEnabled();
            }
            catch { }
        }

        // Fuse: the two ROMs plus a game built on another PC. This is the path for a machine that cannot compile the
        // project, so nothing here needs WSL, LLVM, CMake or the source tree.
        private void Fuse()
        {
            installButton.Enabled = false;
            try
            {
                string dir = installDir.Text.Trim();
                Append("--- fusing ---");
                if (!Installer.Run(mmPath.Text.Trim(), ootPath.Text.Trim(), dir, Append)) return;
                if (!GamePackage.Install(gameDir.Text.Trim(), dir, Append)) return;
                Append("done: the game is installed for these ROMs - start it with pc\\play_hyrule.cmd");
                CheckUpdates(false);
            }
            finally { installButton.Enabled = true; }
        }

        private void Install()
        {
            installButton.Enabled = false;
            try
            {
                if (!Installer.Run(mmPath.Text.Trim(), ootPath.Text.Trim(), installDir.Text.Trim(), Append))
                {
                    Append("Nothing was installed; fix the problem above and try again.");
                    return;
                }
                string dir = installDir.Text.Trim();
                string exe = Path.Combine(dir, "Zelda64Recompiled-ootplay.exe");
                if (File.Exists(exe)) Append("Game executable found. Launch it, or run the Hyrule launcher.");
                else Append("Note: the game executable is not in this folder yet - download the latest release " +
                            "(Check for updates) or build it with MMOOT/tools/build_pc.ps1.");
                CheckUpdates(false);
            }
            finally
            {
                installButton.Enabled = true;
            }
        }

        // Recompiles the tree and installs the result, off the UI thread so the log keeps scrolling.
        private void BuildAll()
        {
            string source = sourceDir.Text.Trim();
            if (source.Length == 0) { Append("Pick the MMOOT source folder."); return; }
            installButton.Enabled = false;
            updateButton.Enabled = false;
            Append("=== build and configure ===");
            string mm = mmPath.Text.Trim();
            string oot = ootPath.Text.Trim();
            ThreadPool.QueueUserWorkItem(delegate
            {
                bool ok;
                try { ok = BuilderFlow.Run(source, mm, oot, LogFromThread); }
                catch (Exception error) { LogFromThread("build and configure failed: " + error.Message); ok = false; }
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        installButton.Enabled = true;
                        updateButton.Enabled = true;
                        if (ok)
                        {
                            Append("Build and configure finished.");
                            CheckUpdates(false);
                        }
                    });
                }
                catch { }
            });
        }

        private void LogFromThread(string line)
        {
            try
            {
                if (InvokeRequired) BeginInvoke((MethodInvoker)delegate { Append(line); });
                else Append(line);
            }
            catch { }
        }

        private void CheckUpdates(bool manual)
        {
            updateButton.Enabled = false;
            updateStatus.Text = "Checking " + Program.RepoUrl + " ...";
            Application.DoEvents();
            UpdateInfo info = GithubCheck.Latest();
            lastUpdate = info;
            if (!info.Ok)
            {
                updateStatus.Text = "Update check failed: " + info.Error;
                updateStatus.ForeColor = Color.FromArgb(240, 180, 140);
                lastUpdateHref = Program.RepoUrl;
                openRelease.Enabled = true;
                updateButton.Enabled = true;
                return;
            }
            string installed = Program.InstalledVersion();
            bool newer = info.IsNewerThan(installed);
            lastUpdateHref = (info.Url != null && info.Url.Length != 0) ? info.Url : Program.RepoUrl;
            openRelease.Enabled = true;
            openRelease.Text = newer ? "Open release page" : "Open repository";
            updateStatus.ForeColor = newer ? Color.FromArgb(214, 170, 255) : Color.FromArgb(150, 220, 150);
            if (string.IsNullOrEmpty(installed))
                updateStatus.Text = "Latest on GitHub: " + info.Tag + ". Local version unknown - press Install to record one.";
            else if (newer)
                updateStatus.Text = "Update available: " + info.Tag + " (you have " + installed + ").";
            else
                updateStatus.Text = "Up to date: you have " + installed + ", GitHub has " + info.Tag + ".";
            if (info.AssetName != null && info.AssetName.Length != 0)
                updateStatus.Text += Environment.NewLine + "Release file: " + info.AssetName;
            if (manual) Append(info.Describe(installed));
            updateButton.Enabled = true;
        }

        // Retroactive by design: the crash already happened, so the report is built from the logs and dumps left behind.
        // Nothing is sent here - the dialog only offers to open a draft issue the player submits themselves.
        private void ShowReportDialog()
        {
            string report;
            try { report = BugReport.Collect(installDir.Text.Trim(), sourceDir.Text.Trim(), log.Text); }
            catch (Exception error)
            {
                Append("Could not assemble a crash report: " + error.Message);
                return;
            }
            using (ReportForm dialog = new ReportForm(report))
            {
                dialog.ShowDialog(this);
                Append(dialog.OpenedIssue
                    ? "Crash report: opened a GitHub issue draft in the browser; nothing is posted until you submit it there."
                    : "Crash report: closed, nothing was sent.");
            }
        }

        private void Append(string line)
        {
            log.AppendText("[" + DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "] " + line + Environment.NewLine);
        }
    }
}
