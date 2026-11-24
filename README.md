# MMOOT — LoZ: Masks of Time

Ocarina of Time rebuilt inside Majora's Mask's own engine: OoT's scenes, actors, items, songs and
dialogue running on MM's renderer, inventory, timer and save system. Majora's Mask is slowly eating
and absorbing Hyrule.

This repository carries the MMOOT setup launcher and its source. The game itself is built from the
project tree on your machine — nothing Nintendo ships here.

## The launcher

Download **[MMOOTSetup.exe](MMOOTSetup.exe)** and run it. It is a single plain Windows program: no
installer framework, no registry, no runtime to install beyond the .NET Framework 4 that already
ships with Windows.

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

Both use the .NET Framework 4 `csc.exe` that ships with Windows and write the executable to
`build/installer/`. Copy it here as `MMOOTSetup.exe` to publish.

## Legal

No ROM, no Nintendo data. Both ROMs come from your own copies, and the Hyrule content image is
derived from your own OoT ROM by the project's Python tools on your own machine.
