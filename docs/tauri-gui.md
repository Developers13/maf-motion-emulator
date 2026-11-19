# Tauri GUI 开发/运维文档

MAF Motion Emulator 桌面 UI（Tauri 2 + Vite，原生 TypeScript），位于 `gui/`，
替换了旧的 WinForms C# 版本。本文面向后续维护者，事实以仓库实际代码为准。

## 1. 架构
- 前端 `gui/src`：Vite + 原生 TypeScript，负责渲染与交互；`main.ts`（入口）、`api.ts`（命令封装）、`mock.ts`（浏览器内存 mock）、`ui/*`（页面）、`lib/*`（纯逻辑）、`styles/*`（主题）。
- 后端 `gui/src-tauri/src`（Rust）：`paths`（路径解析）、`settings`（设置）、`userlist`（学生列表）、`status`（练习状态）、`hosts`（脚本主机扫描）、`proc`（进程管理）、`orchestrator`（运行编排）、`events`（事件发射）、`lib.rs`（命令注册）。
- 通信：前端通过 `invoke` 调命令、通过 `listen` 收事件。
- 命令名：`bootstrap`、`save_settings`、`save_user_rows`、`list_script_hosts`、`start_run`、`stop_run`、`hot_reload`。
- 事件名：`log`、`run-state`、`student-counts`、`toast`。
- `bootstrap` 返回初始状态（设置 + 练习状态 + 提权状态）；`hot_reload` 不重启刷新状态。

## 2. 目录与数据契约
| 路径 | 说明 |
|---|---|
| `gui/gui-settings.json` | PascalCase，字段保持旧 WinForms 格式（如 `PythonLauncher`、`SelectedStudents`、`WeekLimit`）；`SkipOnConfirmedCounts`（默认开）控制批量缓存预跳过；`ExecutionMode`（`serial`/`parallel`，缺省 serial）与 `ParallelVmCount`（1–8，缺省 2）控制串行/并行执行（见 §6.2） |
| `gui/exercise-status.json` | 练习记录；`WeekKey` 为本周一本地日期 `yyyy-MM-dd`，`ReadAt` 为读取时间，`Confirmed` 表示数值是否经 App 实读确认（见下） |
| `user-list.json`（仓库根） | snake_case（`student_id`、`password`、`distance_km`）；**密码只存这里** |
| `gui/scripts/*.ps1` | 运行脚本（`adb-prep.ps1`、`mumu.ps1`、`root-recover.ps1`） |
| `gui/tools/nemu/nemu.exe` | MuMu 模拟器可执行文件 |
| `gui/public/fonts/` | JetBrains Mono 字体及 OFL 许可文件 |

## 3. 开发
```
cd gui
pnpm install
pnpm tauri dev
```
- 前端逻辑测试：`pnpm test`（vitest，测试在 `gui/src/lib/*.test.ts`）。
- Rust 测试：`cargo test --manifest-path gui/src-tauri/Cargo.toml`（bin 已设 `test = false`，逻辑测试在 lib）。
- `pnpm tauri dev` 必须在**管理员终端**启动：exe 嵌入 `requireAdministrator` 清单，未提权运行会失败（`os error 740`）。

## 4. 构建
```
pnpm tauri build --no-bundle
```
- 只产出 exe，位于 `gui/src-tauri/target/release/`。
- 不带 `--no-bundle` 会额外打包 NSIS 安装包（`bundle.targets: ["nsis"]`）。
- 构建机需要 Rust MSVC toolchain + VS Build Tools + WebView2 Runtime。

## 5. 浏览器预览
```
pnpm dev
```
- 打开 `http://127.0.0.1:5173`；非 Tauri 环境自动启用 `gui/src/mock.ts` 内存 mock，不碰文件与进程。
- `/#settings` 直接打开设置页、`/#running` 模拟运行（深链接便于截图检查）。
- 截图用 design-taste-frontend 技能的 `shoot.mjs`；Chrome profile 放系统 TEMP，**不要放仓库内**（否则 Vite 监听报 `EBUSY ... chrome-check`）。

## 6. 进程管理
- 每次 spawn 创建独立 Windows Job Object（`JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`），Job 关闭时整树终结。
- 停止流程：先向目标进程发送 Ctrl+C（`GenerateConsoleCtrlEvent`），宽限 2 秒后调用 `TerminateJobObject`。
- Job 对象不可用（如宿主已在不可嵌套的 Job 中）时，退回 `taskkill /T /F` 杀进程树。
- Python 启动器（`py`/`python`）spawn 前解析为真实 `python.exe`（结果缓存），否则"正在运行"判定与进程树会落空。
- **已知副作用**：`adb-prep.ps1` 里 `adb connect` 孵化的 adb server 守护进程会随 Job 一起被杀；
  因此每个学号运行时，`tools/e2e.py` 会先用 DEVNULL stdio 预启动一个独立的 adb server
  （`ensure_adb_server`），避免 MaaFW 在捕获管道的进程链里再孵守护进程把管道写端占死
  （2026-11-15 实测：探针 `subprocess.run(capture_output=True)` 因此永久卡死，现已改为临时文件重定向）。

## 6.1 次数记录与批量预跳过（两级确认）
- App 页面 OCR 是唯一权威：每个学号跑前由 `ReadCounts` 探针实读「评分次数」（总）与
  「本周有效锻炼数」（周），写入 `exercise-status.json`。
- 单次运行成功后本地 +1，并置 `Confirmed=false`（"待审核"估算），
  同时记录 `LastRunDay`（当天日期）。**审核每日凌晨进行**：App 的统计在每天
  凌晨审核后才更新，因此以本地 06:00 为审核时点（`REVIEW_CUTOFF_HOUR`，保守值）：
  - **读数可信**（与 LastRunDay 隔日且过了 06:00）→ 视为已确认，`Confirmed=true`
    写回缓存；
  - **读数滞后**（同日，或次日 06:00 前）→ 不确认：保留本地估算（取读数与估算的
    较大者；跨周时周次数以读数为准，避免上周次数污染新周），等审核时点过后再确认。
- 批量模式下（`SkipOnConfirmedCounts` 开启时）两类学号**不开模拟器直接跳过**：
  1. 已实读确认（Confirmed=true）且达上限（周 ≥ WeekLimit 且 WeekKey 属于本周，
     或总 ≥ TotalLimit）；
  2. 待审核估算达上限、且读数尚不可信（同日或次日清晨）——开模拟器也读不出真值。
  其余待审核值不跳过：审核时点过后由探针实读确认，此后免读跳过。
- 读数失败 fail-open：跳过决策只基于实读/已确认数据，读不到一律放行。
- 单选手动运行永远强制跑，不受预跳过影响。
- **手动填写次数**（设置 → 用户列表的「本周 / 总次数」两列）：填写后视为已确认
  （`Confirmed=true`，参与批量预跳过），两列同时留空即清除该学号缓存；下一次
  App 实读成功会覆盖手动值（命令：`save_student_counts`）。

## 6.2 串行 / 并行执行
- 主界面左侧导轨底部提供「执行模式」（串行/并行）与「并行台数（1–8，默认 2）」
  控件，默认串行；修改立即写入 `gui-settings.json`；运行中（`isBusy()` 为 true）
  控件禁用；开始运行时模式锁定（启动前持久化，后端读到当前模式与台数）。
  执行模式不重复放在设置页「运行规则」。
- 并行调度：K = min(台数, 选中人数)；启动/准备/运行都占名额；每个学号结束
  （成功/失败/跳过）立即关闭其虚拟机；批次开始与结束各执行一次 `shutdown-all`；
  单个失败不中断其他；停止会取消所有并关闭所有实例；单选 1 个学号仍走单跑。
- 并行日志每行带 `[学号] ` 前缀；进度状态显示「并行运行 N 台 · 完成 x/y」。
- 并行时 MaaFW 调试目录为 `debug/parallel/<学号>/`（串行仍为 `debug/`），
  避免多进程同时写 `debug/debug/maafw.log`。
- 批次开始时 GUI 在 Job 之外预启动 adb server，避免某个 e2e 的 Job 关闭时
  带走共享 server；`mumu.ps1` 的 clone+rename 用会话级命名互斥体 `MafMuMuClone`
  串行化，避免并发克隆串名。

## 7. cargo 网络
- 本机 `~/.cargo/config.toml` 已配置代理：`proxy = "http://127.0.0.1:31181"`。
- 代理未运行时用 `CARGO_HTTP_PROXY=`（空值）清空再构建。
- 长时间停在 `Updating crates.io index` 时加：`CARGO_HTTP_MULTIPLEXING=false CARGO_HTTP_TIMEOUT=30`。

## 8. 常见故障
| 症状 | 原因与处理 |
|---|---|
| `os error 740` | dev exe 未提权运行；用管理员终端启动（见第 3 节） |
| Vite `EBUSY ... chrome-check` | 截图 profile 放在了仓库内；移到系统 TEMP（见第 5 节） |
| `cargo test` 报 bin 无法执行 | bin 已设 `test = false`；逻辑测试在 lib |
| cargo 停在 `Updating crates.io index` | 见第 7 节（清代理或降 multiplexing / timeout） |

## 9. 旧 C# → 新位置映射
| 旧文件 | 新位置 |
|---|---|
| `MainForm.cs` | `gui/src/main.ts` + `gui/src/ui/*` + `gui/src-tauri/src/orchestrator.rs` |
| `SettingsForm.cs` | `gui/src/ui/settings.ts` |
| `StudentSelectDialog.cs` / `StudentStatusList.cs` | `gui/src/ui/students.ts` |
| `AppSettings.cs` | `gui/src-tauri/src/settings.rs` + `gui/src/lib/settings.ts` |
| `UserListService.cs` | `gui/src-tauri/src/userlist.rs` |
| `ExerciseStatusStore.cs` | `gui/src-tauri/src/status.rs` |
| `RunnerService.cs` / `PowerShellRunner.cs` / `ProcessRegistry.cs` / `ConsoleCtrl.cs` | `gui/src-tauri/src/proc.rs` |
| `UiTheme.cs` / `StatusDot.cs` | `gui/src/styles/*` |
