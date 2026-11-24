# MMOOT installer

`MMOOTSetup.exe` is a plain Windows program (no installer framework, no registry, no dependencies beyond the
.NET Framework 4 that ships with Windows). Source: `MMOOTSetup.cs`.

## What it does

0. **Build and configure** is the main button: it places the ROMs, then runs the project's own
   `MMOOT/tools/build_pc.ps1 -BuildOnly` (WSL Ubuntu clang for the MMOOT game code, Visual Studio LLVM 21 + Ninja for
   the PC host), then installs the freshly linked executable as
   `MMOOT/pc/Zelda64Recomp/build-ninja/Zelda64Recompiled-ootplay.exe` - the file `pc/play_hyrule.cmd` and
   `pc/play_deku_tree.cmd` actually start - and mirrors it to the repo-root copy. The previous play binary is kept as
   `.pre_setup_<time>.bak`. Prerequisites are checked first (WSL Ubuntu runnable, Python, CMake, Ninja, VS LLVM) and
   every missing piece is named in the log instead of failing deep inside ninja.
1. **Two ROM pickers.** Majora's Mask (US) and Ocarina of Time (US). Each is checked before anything is copied:
   N64 signature at offset 0, cartridge id `NZL` (Majora's Mask) or `CZL` (Ocarina of Time) at 0x3B, revision byte
   reported, and a 16-128 MB size. A ROM in the wrong slot is refused by name.
2. **Copies them where the game reads them** in the install folder (default `%LOCALAPPDATA%\Zelda64Recompiled`,
   which is `zelda64::get_app_folder_path()`):
   * `mm.n64.us.1.0.z64` - the name `mmoot_content.cpp` opens for the Termina scene table.
   * `oot.n64.us.1.0.z64` - the OoT ROM the Hyrule image is built from.
   It also writes `mmoot_version.txt` (the installed version) and `mmoot_roms.txt` (which ROMs, when, verified).
3. **Checks https://github.com/Faewns-Hellion/MMOOT for updates** through the GitHub API: latest release tag and its
   first `.zip`/`.7z` asset, falling back to the newest tag when nothing is published yet. It reports "up to date",
   "update available", or the reason the check failed (a private repository answers 404 without a token).
4. **Content image policy.** A fresh install takes the newest `MMOOT/build/m3/*/hyrule.z64` and puts it in the game
   folder as `mmoot.hyrule.opening.preview.1.z64`. An existing image is never overwritten: the executable only accepts
   the build its `MMOOT_OOT_RUNTIME_HASH` was compiled against.

Nothing from the ROMs is bundled and the installer downloads nothing except, on request, the project's own release.

## Command line (for checking without clicking)

    MMOOTSetup.exe --validate mm  path\to\mm.z64
    MMOOTSetup.exe --validate oot path\to\oot.z64
    MMOOTSetup.exe --check-updates
    MMOOTSetup.exe --install mm.z64 oot.z64 D:\MMOOT
    MMOOTSetup.exe --check-build "C:\Twilight Wind Mask of Time"     # prerequisites only
    MMOOTSetup.exe --build "C:\Twilight Wind Mask of Time" mm.z64 oot.z64   # recompile + install (the main button)

The console build (`-Console`) prints those results; the window build (`MMOOTSetup.exe`) ignores them and always
opens the window.

## Building

    powershell -NoProfile -File MMOOT/tools/make_installer.ps1            # build/installer/MMOOTSetup.exe
    powershell -NoProfile -File MMOOT/tools/make_installer.ps1 -Console   # .../MMOOTSetup-console.exe

## What it does not do

Recompiling needs the developer toolchain listed above; a player machine without WSL/Visual Studio cannot build from
source, so a release should also carry a prebuilt game folder. The Hyrule content image
(`mmoot.hyrule.opening.preview.1.z64`) is Nintendo data derived from the player's OoT ROM by the Python tools in
`MMOOT/tools` and is not shipped; the installer installs it only on a fresh install and otherwise leaves the working
image alone.
