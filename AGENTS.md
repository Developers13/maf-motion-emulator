# AGENTS.md

Orientation for end users, operators, developers, and automated agents working
in this repository.

- End-user installation: [README → Quick Start](README.md#quick-start-non-developers)
- GUI internals and troubleshooting: [docs/tauri-gui.md](docs/tauri-gui.md)
- Pipeline and agent internals: `assets/interface.json`,
  `assets/resource/pipeline/motion_emulator.json`, `agent/`

---

## Part 1 — End users / operators

This section is for someone who only wants to run the Windows GUI.

### 1.1 What it does

The tool logs in to the NPU Sports app, starts a Motion Emulator route, starts
the after-class exercise, waits until the target distance is reached, and saves
the record. Each student runs on a separate MuMu instance cloned from a
template.

### 1.2 Required environment

| Component | Requirement |
|---|---|
| OS | Windows 10/11 x64; run the GUI as administrator |
| Python | 3.10 or newer; `py --version` should work |
| Python package | `maafw` (MaaFramework bindings) |
| Emulator | MuMu Player 12 with adb enabled |
| Template VM | A MuMu instance named `模板` by default (or set another name in Settings) |
| Apps in template | NPU Sports app + Motion Emulator with a recorded/selected route |
| Root | Magisk + LSPosed in the template; the GUI checks this during preparation |
| Credentials | Student ID + NPU Sports password; stored only in local `user-list.json` |

The release packages include the OCR model. When running from source, the OCR
model lives in `assets/resource/model/ocr/` and is gitignored.

### 1.3 Install a release package

Download from GitHub Releases:

- Portable: `MAF-motion-emulator-gui-win-x64-*.zip`
  - Extract to a writable folder such as `D:\MAF-Motion-Emulator`.
  - Do not extract into `Program Files` if you want to avoid permission issues.
- Installer: `MAF Motion Emulator_*_x64-setup.exe`
  - Installs the GUI plus the same runtime resources under the install folder.

Both packages contain:

```text
maf-motion-emulator.exe
tools/
agent/
assets/            # includes assets/resource/model/ocr
gui/scripts/
gui/tools/nemu/    # nemu.cs; nemu.exe may be absent in CI builds
README.md
AGENTS.md
docs/
```

### 1.4 Install the Python dependency

Open PowerShell in the package root and run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\setup_runtime.ps1
```

The script:

- installs/upgrades `maafw` (skips when `import maa` already works);
- checks the OCR model (release packages already contain it);
- warns when `gui/tools/nemu/nemu.exe` is missing.

Manual fallback:

```powershell
py -m pip install --upgrade maafw
```

### 1.5 Prepare the MuMu template

1. Open MuMu Player 12 and create or locate an instance.
2. Rename it to `模板` (or remember its name and change Settings later).
3. Install the NPU Sports app and Motion Emulator in that instance.
4. In Motion Emulator, record and select a route.
5. Enable adb in MuMu settings.
6. Make sure the template has a working Magisk + LSPosed root environment.
7. Keep the template stopped; the GUI clones and launches copies on demand.
8. If the template has another name, set it in the GUI:
   Settings → Environment → Template VM.

The template is the clone source and is never closed by the `shutdown-all`
step.

### 1.6 Configure and run

1. Right-click `maf-motion-emulator.exe` → **Run as administrator**.
2. Open Settings → Environment and check:
   - Python launcher (`py`, or `python` if the launcher is unavailable)
   - `MuMuManager.exe` path
   - Template VM name
3. Open Settings → Users → Add row and enter:
   - Student ID
   - Password
   - Distance: `2.4` or `3.2` km
4. Save, select students in the left rail, choose Serial or Parallel, then click
   Start run.
5. Use Stop to cancel. The GUI closes the instances it started.

A batch closes all non-template MuMu instances before starting. Save unrelated
work in other instances first.

### 1.7 Common problems

| Symptom | Fix |
|---|---|
| `os error 740` / UAC | Run the exe as administrator. |
| Python or `maafw` not found | Check `py --version`; set the Python launcher to `python` in Settings. |
| `MuMu manager not found` | Fix the `MuMuManager.exe` path in Settings. |
| Template not found / clone failed | Check the template VM name and that the instance still exists. |
| `LSPOSED MISSING` / root recovery failed | Prepare Magisk + LSPosed in the template manually. |
| Invalid sports record | Verify the Motion Emulator route and the sports login; try a manual run in the template. |

For GUI-specific details, see [docs/tauri-gui.md](docs/tauri-gui.md).

---

## Part 2 — Developers / maintainers

### 2.1 Repository map

```text
agent/                      MaaFW custom actions/recognitions (Python)
assets/
  interface.json            ProjectInterface V2 entry
  resource/pipeline/        MaaFW pipeline JSON
  resource/image/           720p template crops
  resource/model/ocr/       OCR model (gitignored; provided by release/CI)
gui/
  src/                      Vite + vanilla TypeScript frontend
  src-tauri/                Rust backend and Tauri configuration
  scripts/                  mumu.ps1, adb-prep.ps1, root-recover.ps1
  tools/nemu/               MuMu SDK tap fallback (C# source)
tools/
  e2e.py                    Batch/single CLI entry
  run_task.py               Low-level pipeline runner
  setup_runtime.ps1         maafw + OCR setup for users
  package_gui_win.ps1       Portable Windows runtime package
  install.py                Generic release packaging
  validate_schema.py        JSON Schema validation
user-list.json              Local credentials, gitignored
```

### 2.2 Toolchain

| Tool | Version used here |
|---|---|
| Node | 24.x |
| pnpm | 11.x (`gui/pnpm-lock.yaml`) |
| Rust | stable MSVC toolchain |
| C++ build tools | Visual Studio 2022 Build Tools + Windows SDK |
| WebView2 | installed on Windows 10/11 |
| Python | 3.10+; current local env is 3.14 |
| MaaFW | `pip install maafw` |
| MuMu | MuMu Player 12 |

The GUI exe embeds a `requireAdministrator` manifest. `pnpm tauri dev` and the
built exe must run from an elevated terminal/session.

### 2.3 Setup

```powershell
# Runtime dependencies (maafw + OCR model)
powershell -NoProfile -ExecutionPolicy Bypass -File tools/setup_runtime.ps1

# GUI dependencies
cd gui
pnpm install
```

### 2.4 Build, test, run

```powershell
# Browser-only UI preview (in-memory mock; no files/processes)
cd gui
pnpm dev                 # http://127.0.0.1:5173

# Frontend logic tests and typecheck/build
pnpm test                # vitest
pnpm build               # tsc --noEmit && vite build

# Rust logic tests
cargo test --manifest-path gui/src-tauri/Cargo.toml

# Tauri dev (elevated terminal)
pnpm tauri dev

# Release exe only
pnpm tauri build --no-bundle

# Release exe + NSIS installer (includes runtime resources)
pnpm tauri build
```

If Cargo is stuck on the local proxy, prefix commands with
`CARGO_HTTP_PROXY=` (empty value). See docs §7.

### 2.5 GUI architecture

Frontend (`gui/src`):

- `main.ts` — bootstrap, events, start/stop, mode controls
- `api.ts` — Tauri command wrappers
- `state.ts` — UI state and selection
- `ui/` — student list, log console, settings modal, toasts
- `lib/` — pure settings/userlist/status/counts/log-classify logic
- `mock.ts` — browser preview backend

Rust (`gui/src-tauri/src`):

- `paths.rs` — project root discovery and data paths
- `settings.rs` — `gui/gui-settings.json` (PascalCase)
- `userlist.rs` — root `user-list.json` (snake_case)
- `status.rs` — counts cache and two-tier confirmation
- `hosts.rs` — PowerShell host discovery
- `proc.rs` — child processes, Job Objects, Ctrl+C/terminate, adb prewarm
- `orchestrator.rs` — single/batch/parallel orchestration
- `events.rs` — log/run-state/counts/toast events
- `lib.rs` — Tauri commands

Commands: `bootstrap`, `save_settings`, `save_user_rows`,
`save_student_counts`, `list_script_hosts`, `start_run`, `stop_run`,
`hot_reload`.

### 2.6 Orchestration and process model

Single run:

```text
prepare_student (mumu ensure -> adb-prep -> optional root-recover)
  -> e2e FullRun
  -> shutdown_student
```

Batch (serial): for each student, `shutdown-all -KeepStudentId`, prepare,
`e2e --skip-if-done`, shutdown student; final `shutdown-all`.

Parallel: K = min(`ParallelVmCount`, selected students). A worker pool holds at
most K VMs including boot/prep/run. Each student's VM is shut down as soon as
that student finishes. The batch performs one global `shutdown-all` at the start
and one at the end. A failure does not stop other workers; Stop cancels all and
still runs the final cleanup.

Key Windows details:

- Every spawned script runs in its own Job Object with
  `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`; the stop path sends Ctrl+C where
  possible and terminates the job after 2 seconds.
- `AppState.current` is a multi-handle registry, so parallel workers can all be
  stopped.
- The adb server is prewarmed by the GUI outside any run Job; otherwise a
  finishing e2e job could kill the shared server and disturb other workers.
- In parallel mode the child logger prefixes every line with
  `[student_id]`; parsed lines (`[e2e] COUNTS`, `[e2e] SKIP`) stay original.
- Parallel MaaFW debug logs go to `debug/parallel/<student_id>/` to avoid
  concurrent rotation of one `debug/debug/maafw.log`.
- `mumu.ps1` serializes clone+rename with the named mutex `MafMuMuClone`;
  concurrent clones otherwise risk renaming each other's new instance.

### 2.7 Data contracts

`gui/gui-settings.json` — PascalCase, local only (excerpt; other fields omitted):

```json
{
  "PythonLauncher": "py",
  "MuMuManagerPath": "C:\\Program Files\\Netease\\MuMu\\nx_main\\MuMuManager.exe",
  "TemplateVmName": "模板",
  "WeekLimit": 5,
  "TotalLimit": 32,
  "SkipOnConfirmedCounts": true,
  "ExecutionMode": "serial",
  "ParallelVmCount": 2,
  "AutoScroll": true
}
```

`gui/exercise-status.json` — local counts cache:

- `Week`, `Total`
- `WeekKey`: Monday date `yyyy-MM-dd`
- `ReadAt`
- `Confirmed`: true only after an app read (or manual entry); false after a
  local `+1`
- `LastRunDay`: date of the last successful run

Review timing: the app reviews records after midnight; the local cutoff is
06:00 (`REVIEW_CUTOFF_HOUR`). Same-day reads, or next-day reads before 06:00,
are not trusted to confirm the estimate.

`user-list.json` — root, snake_case, gitignored:

```json
[
  { "student_id": "2025302319", "password": "...", "distance_km": 3.2 }
]
```

Distance is `2.4` or `3.2` km. Passwords exist only here and in the process
environment (`MAF_PASS`); they must never reach logs, reports, settings files,
pipeline overrides, or commits.

### 2.8 Security rules

- Never print, copy, or commit `user-list.json` contents or any password.
- `user-list.json`, `gui/gui-settings.json`, `gui/exercise-status.json`,
  `assets/resource/model/ocr/`, `gui/tools/nemu/nemu.exe`, `debug/`,
  `node_modules/`, `gui/node_modules/`, `gui/dist/`, `gui/src-tauri/target/`
  are local and gitignored.
- The password is consumed by `agent/my_action.py` (`cas_input_password`) from
  `MAF_PASS`; it is deliberately not passed through `pipeline_override` because
  MaaFW would log the override.
- Credential leak scans should report file names only, never values.

### 2.9 Release and CI

`.github/workflows/check.yml` validates the MaaFW pipeline/interface schemas
(`npm ci`, `pip install maafw`, `tools/validate_schema.py`).

`.github/workflows/install.yml`:

- `install` matrix builds generic MaaFramework/MFAAvalonia packages.
- `gui-windows` (windows-latest) downloads the OCR model, runs
  `pnpm install --frozen-lockfile`, `pnpm tauri build`, and assembles the
  portable runtime with `tools/package_gui_win.ps1`.
- Artifacts:
  - `MAF-motion-emulator-gui-win-x64` (portable runtime)
  - `maf-motion-emulator-gui-installer` (NSIS setup exe)
- On `v*` tags the release job zips the portable artifact and uploads it plus
  the raw NSIS installer.

Tauri bundles runtime resources via `gui/src-tauri/tauri.conf.json`
(`bundle.resources`): `tools/`, `agent/`, `assets/`, `gui/scripts/`,
`gui/tools/nemu/`, README and docs. The installer therefore finds
`tools/e2e.py` next to the exe and needs no repo checkout.

### 2.10 Testing and verification checklist

Current baselines:

- `cargo test --manifest-path gui/src-tauri/Cargo.toml` — 25 tests
- `pnpm test` — 57 tests
- `pnpm build` — typecheck + Vite build
- `pnpm tauri build` — release exe + NSIS installer
- `tools/package_gui_win.ps1 -ExePath ...` — portable runtime zip

Before shipping a GUI/release change:

1. Run the Rust and frontend suites.
2. Run `pnpm tauri build`; confirm the NSIS installer and `target/release`
   resource layout (`tools/e2e.py`, `assets/.../ocr`, `gui/scripts/...`).
3. Run `tools/package_gui_win.ps1`; confirm the exe is at the zip root.
4. Verify `.gitignore` still covers credentials and local artifacts.

### 2.11 Working rules for automated agents

- Read this file, README, and `docs/tauri-gui.md` before changing behavior.
- Never print or commit passwords, `user-list.json`, GUI settings, or status
  files.
- Do not launch the GUI, MuMu, or emulator runs unless the user asks for it;
  use the Rust/vitest suites for verification.
- Do not delete ignored local data (OCR model, `nemu.exe`, settings, status,
  logs) without explicit confirmation.
- Do not commit unless the user asks. Do not push.
- Prefer `git rm --cached` + `.gitignore` over deleting local files when the
  goal is a clean push.
- Use `CARGO_HTTP_PROXY=` if Cargo hits the local proxy.
- Keep `gui/gui-settings.json` PascalCase and `user-list.json` snake_case;
  changing field names breaks existing user configuration.
