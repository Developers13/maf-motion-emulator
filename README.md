# MAF Motion Emulator

NPU sports after-class exercise automation: route simulation via [Motion Emulator](https://github.com/zhufucdev/MotionEmulator) plus exercise check-in on the NPU Sports app (西工大体育), running on MuMu and other Android emulators. Powered by **[MaaFramework](https://github.com/MaaXYZ/MaaFramework)**!

> Successor of the PowerShell/adb-based `motion-emulate-automation` skill — rewritten on MaaFramework: image-recognition driven, low-code pipeline, ships with the generic UI and CI/CD.

For a role-based orientation (end users vs. developers), see [AGENTS.md](AGENTS.md).

## Features

- **Log in to NPU Sports**: privacy dialog → guide pages → CAS unified authentication (account = student ID, password passed in memory only)
- **Start route simulation**: scroll Motion Emulator to the simulation card → verify route → set repeat count (2.4 km → 6 / 3.2 km → 8) → start
- **Start after-class exercise**: open 课外锻炼 → pick the field (长安校区星天苑田径场) → confirm the workout is running
- **Wait until the distance target is reached**: passive OCR polling of the top stats card (never taps, never disturbs the foreground app) + distance-stall detection (protects against the simulation dying silently with 模拟已取消)
- **Stop & save the record**: tap the red stop button → confirm inside the 60-second countdown sheet → verify the record page — the workout is never left running
- **Self-healing state machine**: handles cold-start scrolling, whitelist guide page, time-window and abnormal-record dialogs, orphaned workouts from previous crashes (stop-save-restart, never "continue"), post-save record/Discover pages, and re-login when the session expires

## Quick Start (Non-Developers)

> Just want to run the GUI on Windows? Follow the five steps below. Development/CLI usage is documented later.

### 0. Prerequisites

- Windows 10/11 (x64)
- [Python 3.10+](https://www.python.org/downloads/windows/) (check **Add python.exe to PATH** during installation)
- [MuMu Player 12](https://mumu.163.com/)
- A usable MuMu **template instance** (default name `模板`) with:
  - the NPU Sports app
  - [Motion Emulator](https://github.com/zhufucdev/MotionEmulator) with a route recorded and selected
  - a Magisk + LSPosed root environment (the GUI checks this during preparation; missing Magisk may be repaired automatically, while missing LSPosed needs manual handling)
- Your student ID and NPU Sports password

### 1. Download and extract

Download a Windows GUI asset from [Releases](../../releases):

- **Portable**: `MAF-motion-emulator-gui-win-x64-*.zip` — extract to a **writable folder** (for example `D:\MAF-Motion-Emulator`, not `Program Files`).
- **Installer**: `MAF Motion Emulator_*_x64-setup.exe` — run the installer.

Both packages include `tools/`, `gui/scripts/`, `agent/`, `assets/` (including the OCR model), the README, and AGENTS.md.

### 2. Install MaaFW (Python dependency)

Open PowerShell in the portable folder (or install folder):

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\setup_runtime.ps1
```

The script installs `maafw` and checks the OCR model (the release package already contains it, so this usually skips). If it cannot find Python, install Python 3.10+ first. If pip is slow, run manually:

```powershell
py -m pip install --upgrade maafw
```

### 3. Prepare the MuMu template

1. Open MuMu Player 12 and create/find an instance; rename it to `模板` (or remember its actual name).
2. Install the NPU Sports app and Motion Emulator in that instance, then record and select a route in Motion Emulator.
3. Make sure adb is enabled for the instance (MuMu Settings → Other → ADB debugging).
4. Keep the template instance stopped; the GUI clones and launches it on demand.
5. If the template is not named `模板`: open the GUI → Settings → Environment → Template VM and enter the actual name.

### 4. Add students

1. Right-click `maf-motion-emulator.exe` → **Run as administrator** (MuMu/adb require elevation).
2. Open Settings → Users → Add row and fill in:
   - **Student ID**: the NPU Sports login account
   - **Password**: the NPU Sports password (stored only in the local `user-list.json`; never written to logs or caches)
   - **Distance**: `2.4` or `3.2` km (controls the simulation lap count and target distance)
3. Save.

### 5. Run

1. Select one or more students in the left rail.
2. Choose **Serial** or **Parallel**; parallel mode also lets you set the number of VMs to run at once.
3. Click **Start run** and confirm.
4. The first run clones/launches an instance per student and takes roughly 20–40 minutes per student; the log shows progress and a Windows notification is sent when it finishes.
5. Click **Stop** to abort; the GUI cancels the run and closes the instances it started.

> **Note**: A batch closes every non-template instance before starting. Save your work in other MuMu instances first.

### Troubleshooting

| Symptom | Fix |
|---|---|
| `os error 740` / UAC prompt on launch | Start the exe with **Run as administrator**. |
| Python / `maafw` not found | Make sure `py --version` works; or set the Python launcher to `python` in Settings → Environment. |
| MuMu manager not found | Set the correct `MuMuManager.exe` path in Settings → Environment. |
| Template not found / clone failed | Make sure the template VM name matches Settings and the template instance still exists. |
| `LSPOSED MISSING` / root recovery failed | The template's root environment is incomplete; prepare Magisk + LSPosed in the template before running the GUI. |
| NPU Sports record is invalid | Check that the Motion Emulator route works and the sports app can log in; try one manual run in the template first. |
| More detailed troubleshooting | See [docs/tauri-gui.md](docs/tauri-gui.md). |

## Requirements

- Windows host, Python 3.10+ (`pip install maafw`)
- [MuMu Player 12](https://mumu.163.com/) with at least one Android instance; adb access enabled
- A template instance (default `模板`) with the apps below and a Magisk + LSPosed root environment; the GUI clones it per student
- Motion Emulator installed in the emulator, with a recorded route already selected
- NPU Sports app installed in the emulator
- OCR model in `assets/resource/model/ocr/` (see [Local development](#local-development)) — only needed when running from source; release packages bundle it via CI

## Usage (CLI)

### 1. Start a MuMu instance

Each student runs on their own MuMu instance. Launch it and note its adb port
(`16384 + 32 × instance index`, e.g. instance 0 → `127.0.0.1:16384`):

```powershell
& "C:\Program Files\Netease\MuMu\nx_main\MuMuManager.exe" info -v all   # list instances & ports
```

(If you keep the legacy `motion-emulate-automation` skill around, its
`scripts/mumu.ps1 -Action ensure -StudentId <id>` wraps clone/rename/launch/boot-poll.)

The GUI's `gui/scripts/mumu.ps1` also provides `shutdown-all`, which closes every
running instance except the template:

```powershell
.\gui\scripts\mumu.ps1 -Action shutdown-all                          # close all students' instances
.\gui\scripts\mumu.ps1 -Action shutdown-all -KeepStudentId 2025302319  # keep only this student's
```

The template instance (default 模板) is never closed by `shutdown-all`; it is the
clone source and belongs to no student. In the GUI, selecting multiple students
uses this automatically: for each student it closes the others, prepares and runs
that student, then closes that instance before moving on, and closes everything
when the batch finishes.

The GUI also pushes a native Windows notification whenever a run or pre-run prep
ends — success, failure, or manual stop — as long as the window is not focused.
In a batch each student's outcome is pushed as it happens plus a final summary, so
you can walk away and still see progress. The in-app log and status bar always
show the outcome regardless.

### 2. Run

Batch (every row in `user-list.json`) or a single student:

```bash
python tools/e2e.py              # all rows
python tools/e2e.py 2025302319   # one student
python tools/e2e.py --dry-run    # print each student's target adb address, run nothing
```

`tools/e2e.py` is the standard CLI entry. Per row it resolves laps and target
distance from `distance_km` (2.4 km → 6 laps / target 2.5 km; 3.2 km → 8 laps /
target 3.3 km), injects credentials through environment variables only
(never written to disk or logs), and runs the full chain via
`tools/run_task.py`. Exit code 0 = all rows succeeded, 1 = some failed
(also reported as `[e2e] done. failed=[...]`).

**Multi-instance routing**: each row is dispatched to *that student's own
instance* — `e2e.py` looks up the MuMu instance named after the student ID and
passes its adb address explicitly to `run_task.py`. Without this, `run_task.py`
would fall back to the first device in the adb list, so every student would be
driven on whichever emulator happened to be first. Instances must be started
first (`gui/scripts/mumu.ps1 -Action ensure -StudentId <id>`; the GUI's 「开始」
`button does this automatically before every run); `--dry-run` shows the resolved
address per student. The manager path can be overridden with `MAF_MUMU_MANAGER`.

**Screencap resilience**: `MaaToolkit` offers MuMu devices only the
`EmulatorExtras` screencap method (the MuMu SDK path). MuMu assigns apps to
displays dynamically, and when the resolved display id goes stale the extras
call fails and the framework has *no* method left — the run dies mid-flow with
`No available screencap method`. `run_task.py` therefore requests
`EmulatorExtras | Encode | EncodeToFileAndPull`: extras still wins the speed
test when healthy, and an adb method takes over when it isn't (verified to
reach the app's display for both capture and input). It also performs a
screencap self-check right after connecting, so a broken controller aborts
before the login flow starts instead of halfway through.

**Manual verification (CAS risk control)**: repeating logins for the same
account can trip the school's risk control, and the CAS page then answers a
submit with a 「安全验证」 dialog (usually an SMS code to a masked phone
number) instead of returning to the app. Previously that state had no branch,
so the login chain simply timed out and the whole run was reported failed —
even though a human only needed to tap twice on the emulator.

The pipeline now has a `SportLogin-安全验证` node, checked in every CAS
polling window (`打开CAS` / `收键盘1` / `收键盘2` / `提交`). On a match
`agent/my_verify.py` **pauses and waits for you**: it prints a banner, then
polls purely passively (screenshot + OCR — it never clicks, so it will not
auto-request an SMS). Completing the verification on the emulator resumes the
flow by itself, re-entering login or continuing to the main page. It gives up
after `max_wait_sec` (default 20 min) or when you press Stop in the GUI, and
at most 5 rounds per run, so it can never wait forever.

### 3. Low-level single task (debugging)

```bash
# optional env: MAF_USER / MAF_PASS / MAF_LAPS / MAF_TARGET_KM
python tools/run_task.py FullRun 127.0.0.1:16384
python tools/run_task.py StopExercise 127.0.0.1:16384   # any pipeline node name
```

Note: `run_task.py` does not inject credentials itself — login flows require
`MAF_USER`/`MAF_PASS` set, or just use `tools/e2e.py`.

### `user-list.json`

Lives in the project root. **Gitignored** — keep it local, never commit
plaintext passwords.

```json
[
  { "student_id": "2025302319", "password": "...", "distance_km": 3.2 }
]
```

### What a valid record needs

The sports app grades a session on three conditions (shown on its end sheet):

1. distance ≥ 3.2 km (the run targets 3.3 km for margin)
2. duration between 10 and 40 minutes (8 laps ≈ 15–17 min)
3. started within the school's allowed exercise time window — schedule runs
   accordingly; off-window runs still complete but are graded invalid/pending

## Desktop GUI (Tauri v2)

The GUI is a Tauri v2 app in `gui/` (Vite + vanilla TypeScript frontend, Rust
backend). It replaces the old WinForms launcher and keeps the same on-disk
contracts: `gui/gui-settings.json`, `gui/exercise-status.json`, the root
`user-list.json`, and the PowerShell scripts under `gui/scripts/`.

Prerequisites: Node 22+/pnpm, Rust (MSVC toolchain), Visual Studio Build Tools
with the Windows SDK, and the WebView2 runtime (preinstalled on Windows 10/11).

```powershell
cd gui
pnpm install

# Development (the exe requires administrator; run from an elevated terminal)
pnpm tauri dev

# Release build (exe in gui/src-tauri/target/release/)
pnpm tauri build --no-bundle   # or omit --no-bundle to also build the NSIS installer
```

The release exe embeds a `requireAdministrator` manifest, so Windows shows a UAC
prompt on launch — MuMu/adb operations need the elevated token. Test suites:
`pnpm test` (frontend logic) and `cargo test --manifest-path src-tauri/Cargo.toml`
(Rust settings/status/process-job logic).

## Project structure

```tree
maf-motion-emulator/
├── .github/workflows/       # CI/CD (check validation / install packaging & release)
├── agent/                   # Python agent: custom recognition/actions
│   ├── my_wait.py           #   wait_exercise_finish: passive polling + stall detection
│   ├── my_reco.py           #   resumed_orphan: detects workouts resumed from a crash
│   └── my_action.py         #   stop_run failure hook
├── assets/
│   ├── interface.json       # ProjectInterface V2 (generic UI entry)
│   └── resource/
│       ├── image/           # template images (cropped at 720p scale)
│       ├── model/ocr/       # OCR model (not committed; CI provisions it)
│       └── pipeline/        # task pipeline (low-code logic)
├── deps/tools/              # JSON schemas (validation)
├── gui/                     # Tauri v2 desktop app (replaces the old WinForms GUI)
│   ├── src/                 #   vanilla TypeScript frontend (Vite)
│   ├── src-tauri/           #   Rust backend: process control + orchestration
│   ├── scripts/             #   mumu / adb-prep / root-recover PowerShell scripts
│   └── tools/nemu/          #   MuMu SDK tap fallback used by root recovery
├── tools/
│   ├── e2e.py               # CLI entry: batch / single-student runs
│   ├── run_task.py          # low-level: run one pipeline entry against adb
│   ├── setup_runtime.ps1    # one-shot maafw + OCR model setup
│   ├── package_gui_win.ps1  # portable Windows GUI runtime zip
│   ├── install.py           # release packaging
│   └── validate_schema.py   # pipeline/interface schema validation
├── AGENTS.md                # end-user setup + developer reference
└── user-list.json           # credentials — local only, gitignored
```

## Local development

### One-shot runtime setup

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/setup_runtime.ps1
```

This installs the `maafw` Python bindings (skipped if already importable) and
downloads `ppocr_v6-small` into `assets/resource/model/ocr/` (skipped if the
directory already has files). Use `-Force` to reinstall/redownload,
`-PythonLauncher python` if `py` is not available, and `-Pre` to install a
pre-release `maafw`.

The manual steps below are equivalent.

1. Install the Python bindings: `pip install maafw` (debugger: `pip install MaaDebugger`)
2. Prepare the OCR model (either):
   - download [ppocr_v6-small.zip](https://download.maafw.xyz/MaaCommonAssets/OCR/ppocr_v6/ppocr_v6-small.zip) and extract to `assets/resource/model/ocr/`
   - or add the submodule: `git submodule update --init assets/MaaCommonAssets`
3. Install the [maa-support VSCode extension](https://marketplace.visualstudio.com/items?itemName=nekosu.maa-support) to debug the pipeline with breakpoints via `interface.json`
4. Connect MaaDebugger / the generic UI to the emulator adb (MuMu default
   `127.0.0.1:16384 + 32 × instance index`), capture live screenshots, crop
   template assets into `assets/resource/image/`

## Windows GUI release

GitHub Releases publish two Windows GUI assets in addition to the existing
MaaFramework bundles:

- `MAF-motion-emulator-gui-win-x64-<tag>.zip` — portable runtime. Extract to a
  writable folder and run `maf-motion-emulator.exe` as administrator. The exe is
  at the zip root together with `tools/`, `gui/scripts/`, `agent/`, `assets/`
  (including the OCR model) and the README.
- `MAF Motion Emulator_<version>_x64-setup.exe` — NSIS installer. It installs the
  GUI plus the same runtime resources next to the executable.

Still external prerequisites (not bundled): Python 3.10+ with `maafw`
(`tools/setup_runtime.ps1` installs it), MuMu Player 12 with an Android instance
and the required apps, and `user-list.json` (add students from Settings). The
GUI writes `gui/gui-settings.json`, `gui/exercise-status.json`, `user-list.json`
and `debug/` under its project root, so the portable zip should live in a
writable folder.

The `nemu.exe` fallback used by root recovery is not built by CI (only its
`nemu.cs` source is included); the primary adb path works without it. Build it
locally if recovery needs the MuMu SDK fallback.

## CI/CD

- **check** (push/PR): `@nekosu/maa-tools check` + JSON Schema validation (pipeline / interface)
- **install** (push/PR → CI artifacts; `v*` tag → automatic GitHub Release):
  downloads MaaFramework + MFAAvalonia releases → assembles installers for 4 OS × 2 arch → git-cliff changelog
- **gui-windows** (part of `install`): `pnpm install --frozen-lockfile` →
  `pnpm tauri build` → NSIS installer + portable Windows runtime zip

Release flow:

```bash
git tag v0.1.0
git push origin v0.1.0
```

> Before the first release, enable `Settings → Actions → General → Read and write permissions` in the repository settings.

## License

TBD. MaaFramework is LGPL-3.0; modifications to it follow that license. The license for this project's own code is to be decided.
