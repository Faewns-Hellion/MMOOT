// MMOOT installer (LoZ Masks of Time).
//
// One window: pick the Majora's Mask ROM and the Ocarina of Time ROM from files you own, put them where the
// game finds them, and check github.com/Faewns-Hellion/MMOOT for a newer release.
//
// No game data is bundled and nothing is downloaded from anywhere but the project's own GitHub releases.
//
// Build: MMOOT/tools/make_installer.ps1  (uses the .NET Framework csc that ships with Windows)

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Net;
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
        public const string InstallerVersion = "16z64";

        [STAThread]
        private static int Main(string[] args)
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

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
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
        public static bool InstallExecutable(string source, Action<string> append)
        {
            string dir = BuildNinja(source);
            string built = Path.Combine(dir, "Zelda64Recompiled.exe");
            string play = Path.Combine(dir, "Zelda64Recompiled-ootplay.exe");
            if (!File.Exists(built)) { append("no freshly built executable at " + built); return false; }
            try
            {
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
        private UpdateInfo lastUpdate;
        private string lastUpdateHref;

        public SetupForm()
        {
            Text = "LoZ: Masks of Time - setup";
            Width = 760;
            Height = 560;
            MinimumSize = new Size(680, 520);
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
            sourceDir.Text = detectedSource.Length != 0 ? detectedSource : @"C:\Twilight Wind Mask of Time";
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

            installButton.Text = "Build and configure";
            installButton.SetBounds(16, y, 160, 32);
            installButton.Click += delegate { BuildAll(); };
            body.Controls.Add(installButton);

            Button romsOnly = new Button();
            romsOnly.Text = "ROMs only";
            romsOnly.SetBounds(186, y, 100, 32);
            romsOnly.Click += delegate { Install(); };
            body.Controls.Add(romsOnly);

            updateButton.Text = "Check for updates";
            updateButton.SetBounds(296, y, 150, 32);
            updateButton.Click += delegate { CheckUpdates(true); };
            body.Controls.Add(updateButton);

            openRelease.Text = "Open release page";
            openRelease.SetBounds(456, y, 150, 32);
            openRelease.Enabled = false;
            openRelease.Click += delegate
            {
                string href = lastUpdateHref ?? Program.RepoUrl;
                try { Process.Start(href); } catch (Exception error) { Append("cannot open browser: " + error.Message); }
            };
            body.Controls.Add(openRelease);
            y += 42;

            updateStatus.SetBounds(16, y, 700, 46);
            updateStatus.Text = "";
            body.Controls.Add(updateStatus);
            y += 50;

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
            installButton.Enabled = RomCheck.Check(mmPath.Text.Trim(), RomKind.MajorasMask).Ok &&
                                    RomCheck.Check(ootPath.Text.Trim(), RomKind.Ocarina).Ok;
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

        private void Append(string line)
        {
            log.AppendText("[" + DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "] " + line + Environment.NewLine);
        }
    }
}
