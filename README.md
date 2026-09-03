# MAF Motion Emulator

NPU sports after-class exercise automation: route simulation via [Motion Emulator](https://github.com/zhufucdev/MotionEmulator) plus exercise check-in on the NPU Sports app (西工大体育), running on MuMu and other Android emulators. Powered by **[MaaFramework](https://github.com/MaaXYZ/MaaFramework)**!

> Successor of the PowerShell/adb-based `motion-emulate-automation` skill — rewritten on MaaFramework: image-recognition driven, low-code pipeline, ships with the generic UI and CI/CD.

## Features

- **Log in to NPU Sports**: privacy dialog → guide pages → CAS unified authentication (account = student ID, password passed in memory only)
- **Start route simulation**: scroll Motion Emulator to the simulation card → verify route → set repeat count (2.4 km → 6 / 3.2 km → 8) → start
- **Start after-class exercise**: open 课外锻炼 → pick the field (长安校区星天苑田径场) → confirm the workout is running
- **Wait until the distance target is reached**: passive OCR polling of the top stats card (never taps, never disturbs the foreground app) + distance-stall detection (protects against the simulation dying silently with 模拟已取消)
- **Stop & save the record**: tap the red stop button → confirm inside the 60-second countdown sheet → verify the record page — the workout is never left running
- **Self-healing state machine**: handles cold-start scrolling, whitelist guide page, time-window and abnormal-record dialogs, orphaned workouts from previous crashes (stop-save-restart, never "continue"), post-save record/Discover pages, and re-login when the session expires

## Requirements

- Windows host, Python 3.10+ (`pip install maafw`)
- [MuMu Player 12](https://mumu.163.com/) with at least one Android instance; adb access enabled
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

### 2. Run

Batch (every row in `user-list.json`) or a single student:

```bash
python tools/e2e.py              # all rows
python tools/e2e.py 2025302319   # one student
```

`tools/e2e.py` is the standard CLI entry. Per row it resolves laps and target
distance from `distance_km` (2.4 km → 6 laps / target 2.5 km; 3.2 km → 8 laps /
target 3.3 km), injects credentials through environment variables only
(never written to disk or logs), and runs the full chain via
`tools/run_task.py`. Exit code 0 = all rows succeeded, 1 = some failed
(also reported as `[e2e] done. failed=[...]`).

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
├── tools/
│   ├── e2e.py               # CLI entry: batch / single-student runs
│   ├── run_task.py          # low-level: run one pipeline entry against adb
│   ├── install.py           # release packaging
│   └── validate_schema.py   # pipeline/interface schema validation
└── user-list.json           # credentials — local only, gitignored
```

## Local development

1. Install the Python bindings: `pip install maafw` (debugger: `pip install MaaDebugger`)
2. Prepare the OCR model (either):
   - download [ppocr_v6-small.zip](https://download.maafw.xyz/MaaCommonAssets/OCR/ppocr_v6/ppocr_v6-small.zip) and extract to `assets/resource/model/ocr/`
   - or add the submodule: `git submodule update --init assets/MaaCommonAssets`
3. Install the [maa-support VSCode extension](https://marketplace.visualstudio.com/items?itemName=nekosu.maa-support) to debug the pipeline with breakpoints via `interface.json`
4. Connect MaaDebugger / the generic UI to the emulator adb (MuMu default
   `127.0.0.1:16384 + 32 × instance index`), capture live screenshots, crop
   template assets into `assets/resource/image/`

## CI/CD

- **check** (push/PR): `@nekosu/maa-tools check` + JSON Schema validation (pipeline / interface)
- **install** (push/PR → CI artifacts; `v*` tag → automatic GitHub Release):
  downloads MaaFramework + MFAAvalonia releases → assembles installers for 4 OS × 2 arch → git-cliff changelog

Release flow:

```bash
git tag v0.1.0
git push origin v0.1.0
```

> Before the first release, enable `Settings → Actions → General → Read and write permissions` in the repository settings.

## License

TBD. MaaFramework is LGPL-3.0; modifications to it follow that license. The license for this project's own code is to be decided.
