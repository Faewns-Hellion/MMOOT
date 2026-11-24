# MMOOT — LoZ: Masks of Time

Ocarina of Time rebuilt inside Majora's Mask's own engine: OoT's scenes, actors, items, songs and
dialogue running on MM's renderer, inventory, timer and save system. Majora's Mask is slowly eating
and absorbing Hyrule.

This repository carries the MMOOT setup launcher's source; the launcher itself is published on the
[Releases page](https://github.com/Faewns-Hellion/MMOOT/releases). The game itself is built from the
project tree on your machine — nothing Nintendo ships here.

## The launcher

Download **[MMOOTSetup.exe](https://github.com/Faewns-Hellion/MMOOT/releases/latest/download/MMOOTSetup.exe)** from the **[latest release](https://github.com/Faewns-Hellion/MMOOT/releases/latest)** and run it on **64-bit Windows 10 or Windows 11**. Use that link, rather than saving a GitHub file-preview page as an EXE.

The portable launcher includes its .NET runtime. Copy the single EXE to the other PC; a separate .NET installation and the developer's original folder layout are not required to open it. This launcher is distinct from the game executable, which needs its runtime DLLs and assets alongside it.

If startup fails, the launcher displays the error and writes `%LOCALAPPDATA%\MMOOTSetup\startup.log`. Windows errors before managed startup (for example a blocked or incomplete download) cannot appear in that log. `MMOOTSetup.exe --startup-check` constructs the window and exits without installing ROMs, starting a build, or checking the network.

**Report a crash... (optional, no data collection)** handles crashes that already happened. It reads only files that are
already on the PC - `pc\logs\play_hyrule.log`, the runs kept in `pc\logs\old`, any `crash_*.dmp`, and the launcher's own
startup log - writes them to `%LOCALAPPDATA%\MMOOTSetupeports\MMOOT-report-<date>.txt`, and shows the whole report for
review. It is not telemetry: nothing is recorded while you play and nothing is uploaded. Only **Yes - open a GitHub issue...**
leaves the PC, into a draft issue in your browser that still has to be submitted by hand; **No thanks - close** sends nothing.

**Build and configure** is the main button. It:

1. Asks for your own **Majora's Mask (US)** and **Ocarina of Time (US)** ROMs and checks both before
   copying anything — N64 signature, cartridge id (`NZL` for MM, `CZL` for OoT) at 0x3B, revision,
   and a 16–128 MB size. A ROM in the wrong slot is refused by name.
2. Installs them where the game reads them (`%LOCALAPPDATA%\Zelda64Recompiled` as
   `mm.n64.us.1.0.z64` and `oot.n64.us.1.0.z64`) plus `mmoot_version.txt` and `mmoot_roms.txt`.
3. Recompiles and reconfigures the project so the game is what the source tree currently is: it runs
   `MMOOT/tools/build_pc.ps1 -BuildOnly` with the log streaming into the window, then installs the
   freshly linked executable as the play binary the launcher scripts start.
4. Checks `https://github.com/Faewns-Hellion/MMOOT` for updates (latest release, or the newest tag)
   and says whether your install is behind.

Content image policy: a fresh install takes the newest `MMOOT/build/m3/*/hyrule.z64`; an existing
image is never overwritten, because the executable only accepts the build its runtime hash was
compiled against.

### Command line (for checking without clicking)

    MMOOTSetup.exe --check-updates
    MMOOTSetup.exe --report-check [folder]   # write a crash report, open nothing
    MMOOTSetup.exe --validate mm  path\to\mm.z64
    MMOOTSetup.exe --validate oot path\to\oot.z64
    MMOOTSetup.exe --install mm.z64 oot.z64 D:\MMOOT
    MMOOTSetup.exe --check-build "C:\path\to\MMOOT source"
    MMOOTSetup.exe --build "C:\path\to\MMOOT source" mm.z64 oot.z64

The window build always opens its window; build `src/make_installer.ps1 -Console` for a variant that
prints those results instead.

## Requirements for rebuilding

The launcher compiles the project, so the machine needs the project's own toolchain: WSL with an
Ubuntu distribution, Python, CMake, Ninja and Visual Studio's LLVM. Missing pieces are named in the
launcher log instead of failing deep inside ninja.

## Building the launcher from source

    powershell -NoProfile -File src/make_installer.ps1
    powershell -NoProfile -File src/make_installer.ps1 -Console

The default build uses the .NET 8 SDK and publishes a self-contained single EXE to `build/installer/`. `-Runtime win-arm64` and `-Runtime win-x86` build those architectures. `-Framework` explicitly selects the legacy .NET Framework compiler; that smaller EXE requires .NET Framework on the destination PC. Attach the tested portable output to the GitHub release for the round (tag `16z66`, asset named `MMOOTSetup.exe`) to publish: the page's download link and the launcher's update check both read the newest release.

## Legal

No ROM, no Nintendo data. Both ROMs come from your own copies, and the Hyrule content image is
derived from your own OoT ROM by the project's Python tools on your own machine.
