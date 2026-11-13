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
| `gui/gui-settings.json` | PascalCase，字段保持旧 WinForms 格式（如 `PythonLauncher`、`SelectedStudents`、`WeekLimit`） |
| `gui/exercise-status.json` | 练习记录；`WeekKey` 为本周一本地日期 `yyyy-MM-dd`，`ReadAt` 为读取时间 |
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
