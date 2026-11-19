use crate::events::{self, CountsEvent};
use crate::proc::{self, ManagedProcess, SpawnSpec, StopHandle, WaitError};
use crate::settings::AppSettings;
use crate::status::{self, StatusMap};
use crate::paths;
use std::collections::{HashMap, HashSet, VecDeque};
use std::path::PathBuf;
use std::sync::atomic::{AtomicBool, AtomicU64, AtomicUsize, Ordering};
use std::sync::{Arc, Mutex};
use std::time::Duration;
use tauri::AppHandle;

pub struct AppState {
    pub root: PathBuf,
    pub settings: Mutex<AppSettings>,
    pub statuses: Mutex<StatusMap>,
    pub current: Mutex<HashMap<u64, StopHandle>>,
    next_handle_id: AtomicU64,
    pub stop: AtomicBool,
    pub busy: AtomicBool,
}

impl AppState {
    pub fn new(root: PathBuf, settings: AppSettings, statuses: StatusMap) -> Self {
        Self {
            root,
            settings: Mutex::new(settings),
            statuses: Mutex::new(statuses),
            current: Mutex::new(HashMap::new()),
            next_handle_id: AtomicU64::new(0),
            stop: AtomicBool::new(false),
            busy: AtomicBool::new(false),
        }
    }
}

struct ScriptOutcome {
    code: i32,
    timed_out: bool,
    lines: Vec<String>,
    stopped: bool,
}

impl ScriptOutcome {
    fn has(&self, needle: &str) -> bool {
        self.lines.iter().any(|line| line.contains(needle))
    }

    fn first_with<'a>(&'a self, needle: &str) -> Option<&'a str> {
        self.lines
            .iter()
            .find(|line| line.contains(needle))
            .map(|s| s.as_str())
    }
}

enum Prep {
    Ready,
    Stopped,
    Failed(String),
}

const MINUTE: u64 = 60;

/// 启动一个脚本并等待；输出逐行转发到界面，同时收集给解析用。
/// 并行模式下 `log_prefix` 为学号，仅给事件加 `[学号] ` 前缀，
/// 收集到 `lines` 的仍是原始行（供 parse_counts / parse_skip 解析）。
fn run_script(
    state: &Arc<AppState>,
    app: &AppHandle,
    spec: SpawnSpec,
    timeout: Duration,
    log_prefix: Option<&str>,
) -> Result<ScriptOutcome, String> {
    let lines: Arc<Mutex<Vec<String>>> = Arc::new(Mutex::new(Vec::new()));
    let label = spec.label.clone();
    let app_for_line = app.clone();
    let prefix = log_prefix.map(|value| value.to_string());
    let sink = {
        let lines = Arc::clone(&lines);
        Arc::new(move |is_error: bool, line: String| {
            let shown = match &prefix {
                Some(p) => format!("[{p}] {line}"),
                None => line.clone(),
            };
            events::log(&app_for_line, &label, &shown, is_error);
            if let Ok(mut guard) = lines.lock() {
                guard.push(line);
            }
        })
    };

    let handle_id = state.next_handle_id.fetch_add(1, Ordering::SeqCst);
    let process: ManagedProcess = proc::spawn(spec, sink).map_err(|e| format!("无法启动脚本: {e}"))?;
    state.current.lock().unwrap().insert(handle_id, process.stop_handle());

    let wait = process.wait(timeout);
    state.current.lock().unwrap().remove(&handle_id);

    let (code, timed_out) = match wait {
        Ok(code) => (code, false),
        Err(WaitError::Timeout) => {
            process.terminate();
            let _ = process.wait(Duration::from_secs(5));
            (-1, true)
        }
        Err(WaitError::Disconnected) => (-1, false),
    };

    let stopped = state.stop.load(Ordering::SeqCst);
    let collected = lines.lock().map(|g| g.clone()).unwrap_or_default();
    Ok(ScriptOutcome {
        code,
        timed_out,
        lines: collected,
        stopped,
    })
}

fn kv(line: &str, key: &str) -> Option<String> {
    let prefix = format!("{key}=");
    line.split_whitespace().find_map(|token| {
        token
            .strip_prefix(&prefix)
            .map(|value| {
                value
                    .trim_end_matches(|c: char| !c.is_ascii_alphanumeric() && c != '-' && c != '_')
                    .to_string()
            })
    })
}

fn powershell_host(settings: &AppSettings) -> String {
    crate::hosts::resolve(&settings.script_host_path)
}

fn mumu_spec(settings: &AppSettings, root: &PathBuf, params: Vec<(&str, String)>) -> SpawnSpec {
    SpawnSpec::powershell(
        &powershell_host(settings),
        &paths::scripts_dir(root).join("mumu.ps1"),
        &params,
        root.clone(),
    )
}

fn adb_prep_spec(settings: &AppSettings, root: &PathBuf, port: &str) -> SpawnSpec {
    SpawnSpec::powershell(
        &powershell_host(settings),
        &paths::scripts_dir(root).join("adb-prep.ps1"),
        &[("Port", port.to_string())],
        root.clone(),
    )
}

fn run_e2e(
    state: &Arc<AppState>,
    app: &AppHandle,
    sid: &str,
    skip_if_done: bool,
    log_prefix: Option<&str>,
) -> Result<(ScriptOutcome, bool), String> {
    let (settings, root) = {
        let settings = state.settings.lock().unwrap().clone();
        (settings, state.root.clone())
    };

    let mut env: Vec<(String, String)> = settings
        .environment
        .iter()
        .map(|(k, v)| (k.clone(), v.clone()))
        .collect();
    // 用户显式配置的环境变量优先（与旧 GUI 一致）。
    let has_env = |key: &str| env.iter().any(|(k, _)| k == key);
    let has_mumu = has_env("MAF_MUMU_MANAGER");
    let has_week = has_env("MAF_WEEK_LIMIT");
    let has_total = has_env("MAF_TOTAL_LIMIT");
    let has_debug = has_env("MAF_DEBUG_DIR");
    if !has_mumu {
        env.push(("MAF_MUMU_MANAGER".into(), settings.mumu_manager_path.clone()));
    }
    if !has_week {
        env.push(("MAF_WEEK_LIMIT".into(), settings.week_limit.to_string()));
    }
    if !has_total {
        env.push(("MAF_TOTAL_LIMIT".into(), settings.total_limit.to_string()));
    }
    // 并行模式下给每个学号独立 MaaFW 调试目录；用户显式设置的 MAF_DEBUG_DIR 优先。
    if log_prefix.is_some() && !has_debug {
        let dir = root.join("debug").join("parallel").join(sid);
        let _ = std::fs::create_dir_all(&dir);
        env.push(("MAF_DEBUG_DIR".into(), dir.to_string_lossy().to_string()));
    }

    let mut args = vec!["tools/e2e.py".to_string(), sid.to_string()];
    if skip_if_done {
        args.push("--skip-if-done".into());
    }

    let spec = SpawnSpec::python(&settings.python_launcher, args, root, env);

    // SKIP 行只对当前学号生效；计数行随时写缓存。
    let skipped = Arc::new(AtomicBool::new(false));
    let lines: Arc<Mutex<Vec<String>>> = Arc::new(Mutex::new(Vec::new()));
    let app_for_line = app.clone();
    let state_for_line = Arc::clone(state);
    let skipped_for_line = Arc::clone(&skipped);
    let sid_owned = sid.to_string();
    let prefix = log_prefix.map(|value| value.to_string());
    let sink = {
        let lines = Arc::clone(&lines);
        Arc::new(move |is_error: bool, line: String| {
            let shown = match &prefix {
                Some(p) => format!("[{p}] {line}"),
                None => line.clone(),
            };
            events::log(&app_for_line, "python", &shown, is_error);
            if let Ok(mut guard) = lines.lock() {
                guard.push(line.clone());
            }
            handle_e2e_line(&state_for_line, &app_for_line, &sid_owned, &skipped_for_line, &line);
        })
    };

    let process = proc::spawn(spec, sink).map_err(|e| format!("无法启动 e2e: {e}"))?;
    let handle_id = state.next_handle_id.fetch_add(1, Ordering::SeqCst);
    state.current.lock().unwrap().insert(handle_id, process.stop_handle());

    let wait = process.wait(Duration::from_secs(6 * 3600));
    state.current.lock().unwrap().remove(&handle_id);

    let (code, timed_out) = match wait {
        Ok(code) => (code, false),
        Err(WaitError::Timeout) => {
            process.terminate();
            let _ = process.wait(Duration::from_secs(5));
            (-1, true)
        }
        Err(WaitError::Disconnected) => (-1, false),
    };

    let stopped = state.stop.load(Ordering::SeqCst);
    let collected = lines.lock().map(|g| g.clone()).unwrap_or_default();
    Ok((
        ScriptOutcome {
            code,
            timed_out,
            lines: collected,
            stopped,
        },
        skipped.load(Ordering::SeqCst),
    ))
}

/// 解析 `[e2e] COUNTS sid=.. week=.. total=..`；负数（探针失败）返回 None。
pub fn parse_counts(line: &str) -> Option<(String, u32, u32)> {
    if !line.contains("COUNTS") {
        return None;
    }
    let sid = kv(line, "sid")?;
    let week = kv(line, "week")?.parse::<i64>().ok()?;
    let total = kv(line, "total")?.parse::<i64>().ok()?;
    if week < 0 || total < 0 {
        return None;
    }
    Some((sid, week as u32, total as u32))
}

/// 解析 `[e2e] SKIP sid=..`。
pub fn parse_skip(line: &str) -> Option<String> {
    if !line.contains("[e2e] SKIP") {
        return None;
    }
    kv(line, "sid")
}

fn handle_e2e_line(
    state: &Arc<AppState>,
    app: &AppHandle,
    sid: &str,
    skipped: &Arc<AtomicBool>,
    line: &str,
) {
    if let Some((counts_sid, week, total)) = parse_counts(line) {
        let (week_key, read_at, confirmed) = {
            let mut map = state.statuses.lock().unwrap();
            status::set_from_read(&mut map, &counts_sid, week, total);
            let _ = status::save(&paths::status_path(&state.root), &map);
            let entry = map.get(&counts_sid).cloned().unwrap_or_default();
            (entry.week_key, entry.read_at, entry.confirmed)
        };
        events::counts(
            app,
            CountsEvent {
                student: counts_sid,
                week,
                total,
                week_key,
                read_at,
                confirmed,
            },
        );
    }

    if parse_skip(line).as_deref() == Some(sid) {
        skipped.store(true, Ordering::SeqCst);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_counts_line() {
        assert_eq!(
            parse_counts("[e2e] COUNTS sid=2025302319 week=0 total=33"),
            Some(("2025302319".into(), 0, 33))
        );
    }

    #[test]
    fn rejects_failed_probe_counts() {
        assert_eq!(parse_counts("[e2e] COUNTS sid=2025302319 week=-1 total=-1"), None);
    }

    #[test]
    fn parses_skip_line() {
        assert_eq!(
            parse_skip("[e2e] SKIP sid=2025302319 reason=total week=0/5 total=33/32"),
            Some("2025302319".into())
        );
        assert_eq!(parse_skip("nothing here"), None);
    }

    #[test]
    fn worker_count_is_clamped_and_capped() {
        assert_eq!(worker_count(2, 10), 2);
        assert_eq!(worker_count(0, 10), 1);
        assert_eq!(worker_count(99, 5), 5);
        assert_eq!(worker_count(3, 0), 1);
    }
}

fn prepare_student(
    state: &Arc<AppState>,
    app: &AppHandle,
    sid: &str,
    log_prefix: Option<&str>,
) -> Prep {
    let (settings, root) = {
        let settings = state.settings.lock().unwrap().clone();
        (settings, state.root.clone())
    };

    let mut ready = None;
    for attempt in 1..=2 {
        let spec = mumu_spec(
            &settings,
            &root,
            vec![
                ("Action", "ensure".into()),
                ("StudentId", sid.to_string()),
                ("Template", settings.template_vm_name.clone()),
                ("ManagerPath", settings.mumu_manager_path.clone()),
            ],
        );
        let outcome = match run_script(
            state,
            app,
            spec,
            Duration::from_secs(6 * MINUTE),
            log_prefix,
        ) {
            Ok(outcome) => outcome,
            Err(e) => return Prep::Failed(e),
        };
        if outcome.stopped {
            return Prep::Stopped;
        }
        if outcome.code != 0 {
            let detail = outcome
                .first_with("[mumu] ERROR")
                .map(|s| s.to_string())
                .unwrap_or_else(|| format!("exit code {}", outcome.code));
            return Prep::Failed(format!("Instance launch failed: {detail}"));
        }
        let port = outcome
            .first_with("READY")
            .and_then(|line| kv(line, "port"));
        let index = outcome
            .first_with("READY")
            .and_then(|line| kv(line, "index"));
        if let (Some(port), Some(index)) = (port, index) {
            ready = Some((port, index));
            break;
        }
        if attempt == 1 {
            events::log(
                app,
                "gui",
                "=== ensure exited 0 without a READY line (retry 2/2) ===",
                false,
            );
        }
    }

    let Some((port, index)) = ready else {
        return Prep::Failed(
            "Failed to parse adb port from script output (ensure succeeded twice without a READY line)."
                .into(),
        );
    };

    events::log(
        app,
        "gui",
        &format!("=== Instance ready (adb port {port}), checking root environment... ==="),
        false,
    );

    let prep = match run_script(
        state,
        app,
        adb_prep_spec(&settings, &root, &port),
        Duration::from_secs(3 * MINUTE),
        log_prefix,
    ) {
        Ok(outcome) => outcome,
        Err(e) => return Prep::Failed(e),
    };
    if prep.stopped {
        return Prep::Stopped;
    }

    let recovery_incomplete = prep.has("RECOVERY_INCOMPLETE");
    if prep.code != 0 && !recovery_incomplete {
        let detail = prep
            .first_with("FAIL")
            .map(|s| s.to_string())
            .unwrap_or_else(|| format!("exit code {}", prep.code));
        return Prep::Failed(format!("adb environment check failed: {detail}"));
    }

    let magisk_missing = prep.has("MAGISK NOT-FOUND");
    let lsposed_missing = prep.has("LSPOSED MISSING");

    if magisk_missing || recovery_incomplete {
        events::log(
            app,
            "gui",
            if recovery_incomplete {
                "=== Root recovery was interrupted (installer page foreground); completing it (reboot + verify) ==="
            } else {
                "=== Root environment missing (MAGISK NOT-FOUND); starting automatic recovery ==="
            },
            false,
        );
        events::run_state(
            app,
            "preparing",
            Some(sid),
            None,
            None,
            "正在修复 root 环境（安装 Magisk 并重启）…",
            "info",
        );

        let spec = SpawnSpec::powershell(
            &powershell_host(&settings),
            &paths::scripts_dir(&root).join("root-recover.ps1"),
            &[
                ("Port", port.clone()),
                ("Index", index.clone()),
                ("NemuPath", paths::nemu_path(&root).to_string_lossy().to_string()),
            ],
            root.clone(),
        );
        let recovery = match run_script(
            state,
            app,
            spec,
            Duration::from_secs(15 * MINUTE),
            log_prefix,
        ) {
            Ok(outcome) => outcome,
            Err(e) => return Prep::Failed(e),
        };
        if recovery.stopped {
            return Prep::Stopped;
        }
        if recovery.code != 0 {
            let detail = recovery
                .first_with("[root-recover] ERROR")
                .map(|s| s.to_string())
                .unwrap_or_else(|| format!("exit code {}", recovery.code));
            return Prep::Failed(format!("Root recovery failed: {detail}"));
        }

        events::log(
            app,
            "gui",
            "=== Root recovery done; re-checking environment ===",
            false,
        );
        let recheck = match run_script(
            state,
            app,
            adb_prep_spec(&settings, &root, &port),
            Duration::from_secs(3 * MINUTE),
            log_prefix,
        ) {
            Ok(outcome) => outcome,
            Err(e) => return Prep::Failed(e),
        };
        if recheck.stopped {
            return Prep::Stopped;
        }
        if recheck.code != 0
            || recheck.has("MAGISK NOT-FOUND")
            || recheck.has("LSPOSED MISSING")
        {
            return Prep::Failed(
                "Root recovery finished but the environment re-check still fails (MAGISK/LSPOSED missing)."
                    .into(),
            );
        }
    } else if lsposed_missing {
        return Prep::Failed(
            "LSPOSED MISSING while Magisk is present - hidden-root stack is broken; manual inspection required."
                .into(),
        );
    }

    Prep::Ready
}

fn shutdown_instances(state: &Arc<AppState>, app: &AppHandle, keep: Option<&str>, timeout: Duration) {
    let (settings, root) = {
        let settings = state.settings.lock().unwrap().clone();
        (settings, state.root.clone())
    };
    let spec = mumu_spec(
        &settings,
        &root,
        vec![
            ("Action", "shutdown-all".into()),
            ("KeepStudentId", keep.unwrap_or("").to_string()),
            ("Template", settings.template_vm_name.clone()),
            ("ManagerPath", settings.mumu_manager_path.clone()),
        ],
    );
    match run_script(state, app, spec, timeout, None) {
        Ok(outcome) if outcome.code == 0 => {}
        Ok(outcome) => events::log(
            app,
            "gui",
            &format!("[mumu] WARN shutdown-all failed: exit code {}", outcome.code),
            false,
        ),
        Err(e) => events::log(app, "gui", &format!("[mumu] WARN shutdown-all failed: {e}"), false),
    }
}

fn shutdown_student(
    state: &Arc<AppState>,
    app: &AppHandle,
    sid: &str,
    log_prefix: Option<&str>,
) {
    let (settings, root) = {
        let settings = state.settings.lock().unwrap().clone();
        (settings, state.root.clone())
    };
    let spec = mumu_spec(
        &settings,
        &root,
        vec![
            ("Action", "shutdown".into()),
            ("StudentId", sid.to_string()),
            ("ManagerPath", settings.mumu_manager_path.clone()),
        ],
    );
    match run_script(state, app, spec, Duration::from_secs(3 * MINUTE), log_prefix) {
        Ok(outcome) if outcome.code == 0 => {}
        // instance_not_found 是正常情况：该学号本来就没在跑。
        Ok(outcome) if outcome.has("instance_not_found") => {}
        Ok(outcome) => events::log(
            app,
            "gui",
            &format!("[mumu] WARN shutdown {sid} failed: exit code {}", outcome.code),
            false,
        ),
        Err(e) => events::log(app, "gui", &format!("[mumu] WARN shutdown {sid} failed: {e}"), false),
    }
}

fn increment_and_emit(state: &Arc<AppState>, app: &AppHandle, sid: &str) {
    let (week_key, read_at, week, total, confirmed) = {
        let mut map = state.statuses.lock().unwrap();
        status::increment_after_success(&mut map, sid);
        let _ = status::save(&paths::status_path(&state.root), &map);
        let entry = map.get(sid).cloned().unwrap_or_default();
        (entry.week_key, entry.read_at, entry.week, entry.total, entry.confirmed)
    };
    events::counts(
        app,
        CountsEvent {
            student: sid.to_string(),
            week,
            total,
            week_key,
            read_at,
            confirmed,
        },
    );
}

/// 缓存预跳过：两条规则（原 run_batch 内联逻辑）。
/// 1. 已实读确认（Confirmed=true）且达上限——上次开模拟器已验证过；
/// 2. +1 待审核估算达上限、且读数尚不可信（同日，或次日清晨 06:00 前，
///    审核每日凌晨进行）——开模拟器也读不出真值，按估算直接跳过。
/// 其余待审核值不跳过：审核时点过后由探针实读确认。
fn pre_skip_entry(
    state: &Arc<AppState>,
    sid: &str,
) -> Option<(&'static str, status::ExerciseStatus, u32, u32)> {
    let (skip_enabled, week_limit, total_limit) = {
        let s = state.settings.lock().unwrap();
        (s.skip_on_confirmed_counts, s.week_limit, s.total_limit)
    };
    if !skip_enabled {
        return None;
    }
    let now = chrono::Local::now();
    let entry = state.statuses.lock().unwrap().get(sid).cloned()?;
    if status::confirmed_at_cap(&entry, week_limit, total_limit, &now) {
        Some(("已实读确认", entry, week_limit, total_limit))
    } else if status::pending_estimate_at_cap(&entry, week_limit, total_limit, &now) {
        Some(("待审核估算", entry, week_limit, total_limit))
    } else {
        None
    }
}

pub fn start(state: Arc<AppState>, app: AppHandle, students: Vec<String>) {
    if state.busy.swap(true, Ordering::SeqCst) {
        events::log(&app, "gui", "=== A run is already in progress ===", false);
        return;
    }
    state.stop.store(false, Ordering::SeqCst);

    let (mode, limit) = {
        let settings = state.settings.lock().unwrap();
        (settings.execution_mode.clone(), settings.parallel_vm_count as usize)
    };

    std::thread::spawn(move || {
        // adb server 必须在任何 Job 之外预启动：否则某个 e2e 结束关闭 Job 时
        // 会把共享 server 一起带走，打断其它并行任务。
        let mumu = state.settings.lock().unwrap().mumu_manager_path.clone();
        if proc::ensure_adb_server(&mumu) {
            events::log(&app, "gui", "=== adb server ready (outside run Job) ===", false);
        } else {
            events::log(&app, "gui", "[adb] WARN adb start-server failed; MaaFW will retry", false);
        }

        if students.len() > 1 && mode == "parallel" && limit > 1 {
            run_parallel(&state, &app, &students, limit);
        } else if students.len() > 1 {
            run_batch(&state, &app, &students);
        } else if let Some(sid) = students.first() {
            run_single(&state, &app, sid);
        }
        state.busy.store(false, Ordering::SeqCst);
    });
}

fn run_single(state: &Arc<AppState>, app: &AppHandle, sid: &str) {
    events::log(app, "gui", &format!("=== Start run: {sid} ==="), false);
    events::run_state(
        app,
        "preparing",
        Some(sid),
        None,
        None,
        &format!("正在准备 {sid}…"),
        "info",
    );

    match prepare_student(state, app, sid, None) {
        Prep::Stopped => {
            events::log(app, "gui", "=== Run stopped by user during prep ===", false);
            events::run_state(app, "stopped", Some(sid), None, None, "已停止", "neutral");
            events::toast(app, "neutral", "运行已停止", &format!("{sid} 在准备阶段被手动停止。"));
            return;
        }
        Prep::Failed(message) => {
            events::log(
                app,
                "gui",
                &format!("=== Run failed during prep: {message} ==="),
                true,
            );
            events::run_state(app, "failed", Some(sid), None, None, "失败", "error");
            events::toast(app, "fail", &format!("运行失败 · {sid}"), &format!("准备阶段失败：{message}"));
            return;
        }
        Prep::Ready => {}
    }

    if state.stop.load(Ordering::SeqCst) {
        events::log(app, "gui", "=== Run stopped by user during prep ===", false);
        events::run_state(app, "stopped", Some(sid), None, None, "已停止", "neutral");
        return;
    }

    events::run_state(app, "running", Some(sid), None, None, "运行中…", "info");
    let (outcome, _) = match run_e2e(state, app, sid, false, None) {
        Ok(result) => result,
        Err(e) => {
            events::log(app, "gui", &format!("=== Run failed: {e} ==="), true);
            events::run_state(app, "failed", Some(sid), None, None, "失败", "error");
            return;
        }
    };

    if outcome.stopped || outcome.timed_out {
        events::log(app, "gui", "=== Run stopped by user ===", false);
        events::run_state(app, "stopped", Some(sid), None, None, "已停止", "neutral");
        events::toast(app, "neutral", "运行已停止", &format!("{sid} 已被手动停止。"));
        return;
    }

    if outcome.code == 0 {
        increment_and_emit(state, app, sid);
        events::log(app, "gui", "=== Finished: OK (exit code 0) ===", false);
        events::run_state(app, "done", Some(sid), None, None, "完成", "ok");
        events::toast(app, "ok", &format!("运行完成 · {sid}"), "全部流程已正常结束。");
    } else {
        events::log(
            app,
            "gui",
            &format!("=== Finished: FAILED (exit code {}) ===", outcome.code),
            true,
        );
        events::run_state(app, "failed", Some(sid), None, None, "失败", "error");
        events::toast(
            app,
            "fail",
            &format!("运行失败 · {sid}"),
            &format!("退出码 {}，请查看日志排查。", outcome.code),
        );
    }
}

fn run_batch(state: &Arc<AppState>, app: &AppHandle, students: &[String]) {
    let total = students.len();
    events::run_state(app, "batch", None, Some(0), Some(total), "批量运行中…", "info");

    let mut succeeded: Vec<String> = Vec::new();
    let mut failed: Vec<String> = Vec::new();
    let mut skipped: Vec<String> = Vec::new();

    for (i, sid) in students.iter().enumerate() {
        if state.stop.load(Ordering::SeqCst) {
            break;
        }
        let progress = i + 1;
        events::log(
            app,
            "gui",
            &format!("=== Batch run ({progress}/{total}): {sid} ==="),
            false,
        );
        events::run_state(
            app,
            "batch",
            Some(sid),
            Some(progress),
            Some(total),
            &format!("批量运行 {sid} ({progress}/{total})…"),
            "info",
        );

        if let Some((reason, entry, week_limit, total_limit)) = pre_skip_entry(&state, &sid) {
            skipped.push(sid.clone());
            events::log(
                app,
                "gui",
                &format!(
                    "[batch] {sid} -> SKIP ({reason}: week={}/{week_limit} total={}/{total_limit})",
                    entry.week, entry.total
                ),
                false,
            );
            events::toast(
                app,
                "neutral",
                &format!("批量运行 ({progress}/{total}) 跳过"),
                &format!("{sid} 次数已达上限（{reason}），未开模拟器直接跳过。"),
            );
            continue;
        }

        shutdown_instances(state, app, Some(sid), Duration::from_secs(5 * MINUTE));
        if state.stop.load(Ordering::SeqCst) {
            break;
        }

        match prepare_student(state, app, sid, None) {
            Prep::Stopped => break,
            Prep::Failed(message) => {
                failed.push(sid.clone());
                events::log(app, "gui", &format!("[batch] {sid} prep -> FAILED: {message}"), true);
                events::toast(
                    app,
                    "fail",
                    &format!("批量运行 ({progress}/{total}) 失败"),
                    &format!("{sid} 准备失败：{message}"),
                );
                shutdown_student(state, app, sid, None);
                continue;
            }
            Prep::Ready => {}
        }

        if state.stop.load(Ordering::SeqCst) {
            break;
        }

        events::log(app, "gui", &format!("=== Start run: {sid} ==="), false);
        events::run_state(
            app,
            "running",
            Some(sid),
            Some(progress),
            Some(total),
            &format!("运行中 {sid} ({progress}/{total})…"),
            "info",
        );

        let (outcome, was_skipped) = match run_e2e(state, app, sid, true, None) {
            Ok(result) => result,
            Err(e) => {
                failed.push(sid.clone());
                events::log(app, "gui", &format!("[batch] {sid} -> FAILED: {e}"), true);
                shutdown_student(state, app, sid, None);
                continue;
            }
        };

        if state.stop.load(Ordering::SeqCst) || outcome.stopped {
            break;
        }

        if outcome.code == 3 || was_skipped {
            skipped.push(sid.clone());
            events::log(
                app,
                "gui",
                &format!("[batch] {sid} -> SKIP (weekly/total cap reached)"),
                false,
            );
            events::toast(
                app,
                "neutral",
                &format!("批量运行 ({progress}/{total}) 跳过"),
                &format!("{sid} 本周或总次数已达上限，已跳过。"),
            );
            shutdown_student(state, app, sid, None);
            continue;
        }

        if outcome.code == 0 {
            succeeded.push(sid.clone());
            increment_and_emit(state, app, sid);
            events::log(app, "gui", &format!("[batch] {sid} -> OK"), false);
            events::toast(
                app,
                "ok",
                &format!("批量运行 ({progress}/{total}) 完成"),
                &format!("{sid} 成功，继续处理下一个学号。"),
            );
        } else {
            failed.push(sid.clone());
            events::log(
                app,
                "gui",
                &format!("[batch] {sid} -> FAILED (exit code {})", outcome.code),
                true,
            );
            events::toast(
                app,
                "fail",
                &format!("批量运行 ({progress}/{total}) 失败"),
                &format!("{sid} 失败（退出码 {}），已跳过并继续后续学号。", outcome.code),
            );
        }

        shutdown_student(state, app, sid, None);
    }

    events::log(app, "gui", "=== Closing all emulator instances ===", false);
    shutdown_instances(state, app, None, Duration::from_secs(5 * MINUTE));

    if state.stop.load(Ordering::SeqCst) {
        events::log(app, "gui", "=== Batch run stopped by user ===", false);
        events::run_state(app, "stopped", None, None, Some(total), "已停止", "neutral");
        events::toast(
            app,
            "neutral",
            "批量运行已停止",
            &format!("已完成 {} 个，失败 {} 个。", succeeded.len(), failed.len()),
        );
        return;
    }

    let summary = if succeeded.is_empty() && failed.is_empty() && !skipped.is_empty() {
        format!(
            "批量运行完成：{} 个学号均已达上限（本周或总次数），已全部跳过。",
            skipped.len()
        )
    } else {
        let mut text = format!("批量运行完成：成功 {} 个", succeeded.len());
        if !skipped.is_empty() {
            text.push_str(&format!("，跳过 {} 个（已达上限）", skipped.len()));
        }
        if failed.is_empty() {
            text.push_str("，全部通过");
        } else {
            text.push_str(&format!("，失败 {} 个（{}）", failed.len(), failed.join(", ")));
        }
        text.push('。');
        text
    };

    let has_failure = !failed.is_empty();
    events::log(app, "gui", &format!("=== {summary} ==="), has_failure);
    events::run_state(
        app,
        if has_failure { "failed" } else { "done" },
        None,
        None,
        Some(total),
        if has_failure { "部分失败" } else { "完成" },
        if has_failure { "error" } else { "ok" },
    );
    events::toast(
        app,
        if has_failure { "fail" } else { "ok" },
        if has_failure { "批量运行完成（部分失败）" } else { "批量运行完成" },
        &summary,
    );
}

#[derive(Default)]
struct BatchOutcome {
    succeeded: Vec<String>,
    failed: Vec<String>,
    skipped: Vec<String>,
}

/// 并行 worker 数：夹取 1..=8 并受学号总数限制。
fn worker_count(limit: usize, total: usize) -> usize {
    limit.clamp(1, 8).min(total.max(1))
}

fn emit_parallel_progress(app: &AppHandle, active: usize, completed: usize, total: usize) {
    events::run_state(
        app,
        "batch",
        None,
        Some(completed),
        Some(total),
        &format!("并行运行 {active} 台 · 完成 {completed}/{total}"),
        "info",
    );
}

fn run_parallel(state: &Arc<AppState>, app: &AppHandle, students: &[String], limit: usize) {
    let total = students.len();
    let workers = worker_count(limit, total);

    events::log(
        app,
        "gui",
        &format!("=== Parallel batch: {total} student(s), up to {workers} VM(s) at a time ==="),
        false,
    );
    events::run_state(
        app,
        "batch",
        None,
        Some(0),
        Some(total),
        &format!("并行运行（最多 {workers} 台）…"),
        "info",
    );

    // 批次开始先关闭所有已有实例；共享 adb server 已由 start() 预热。
    // 即使此时用户已经点了停止，也继续走完 worker 循环（worker 会立即
    // 因 stop 退出）和收尾 shutdown-all，保证「停止 = 全部关闭」。
    shutdown_instances(state, app, None, Duration::from_secs(5 * MINUTE));

    let queue = Arc::new(Mutex::new(VecDeque::from(students.to_vec())));
    let outcome = Arc::new(Mutex::new(BatchOutcome::default()));
    let completed = Arc::new(AtomicUsize::new(0));
    let active = Arc::new(Mutex::new(HashSet::<String>::new()));

    let mut joins: Vec<std::thread::JoinHandle<()>> = Vec::new();
    for _ in 0..workers {
        let state = Arc::clone(state);
        let app = app.clone();
        let queue = Arc::clone(&queue);
        let outcome = Arc::clone(&outcome);
        let completed = Arc::clone(&completed);
        let active = Arc::clone(&active);
        let total = total;
        joins.push(std::thread::spawn(move || {
            loop {
                if state.stop.load(Ordering::SeqCst) {
                    break;
                }
                let sid = {
                    let mut q = queue.lock().unwrap();
                    match q.pop_front() {
                        Some(s) => s,
                        None => break,
                    }
                };

                // 预跳过（与串行同规则）；并行下不发 per-student toast（避免刷屏）。
                if let Some((reason, entry, week_limit, total_limit)) = pre_skip_entry(&state, &sid) {
                    {
                        let mut o = outcome.lock().unwrap();
                        o.skipped.push(sid.clone());
                    }
                    events::log(
                        &app,
                        "gui",
                        &format!(
                            "[batch] {sid} -> SKIP ({reason}: week={}/{} total={}/{})",
                            entry.week, week_limit, entry.total, total_limit
                        ),
                        false,
                    );
                    let done = completed.fetch_add(1, Ordering::SeqCst) + 1;
                    emit_parallel_progress(&app, active.lock().unwrap().len(), done, total);
                    continue;
                }

                {
                    let mut a = active.lock().unwrap();
                    a.insert(sid.clone());
                }
                let done = completed.load(Ordering::SeqCst);
                let active_len = active.lock().unwrap().len();
                emit_parallel_progress(&app, active_len, done, total);

                // 两个明确状态：stop_worker 中断本 worker；prepared 决定是否进入 e2e。
                let mut stop_worker = false;
                let mut prepared = false;

                match prepare_student(&state, &app, &sid, Some(&sid)) {
                    Prep::Stopped => {
                        stop_worker = true;
                    }
                    Prep::Failed(message) => {
                        {
                            let mut o = outcome.lock().unwrap();
                            o.failed.push(sid.clone());
                        }
                        events::log(
                            &app,
                            "gui",
                            &format!("[batch] {sid} prep -> FAILED: {message}"),
                            true,
                        );
                        events::toast(
                            &app,
                            "fail",
                            &format!("并行运行失败 · {sid}"),
                            &format!("准备失败：{message}"),
                        );
                        // prepared 保持 false：准备失败绝不进入 e2e（与串行一致）。
                    }
                    Prep::Ready => {
                        if state.stop.load(Ordering::SeqCst) {
                            stop_worker = true;
                        } else {
                            prepared = true;
                        }
                    }
                }

                if stop_worker {
                    {
                        let mut a = active.lock().unwrap();
                        a.remove(&sid);
                    }
                    break;
                }

                if prepared {
                    events::log(&app, "gui", &format!("=== Start run: {sid} ==="), false);
                    match run_e2e(&state, &app, &sid, true, Some(&sid)) {
                        Ok((outcome_res, was_skipped)) => {
                            if outcome_res.stopped || outcome_res.timed_out {
                                {
                                    let mut a = active.lock().unwrap();
                                    a.remove(&sid);
                                }
                                break;
                            }
                            if outcome_res.code == 3 || was_skipped {
                                {
                                    let mut o = outcome.lock().unwrap();
                                    o.skipped.push(sid.clone());
                                }
                                events::log(
                                    &app,
                                    "gui",
                                    &format!("[batch] {sid} -> SKIP (weekly/total cap reached)"),
                                    false,
                                );
                            } else if outcome_res.code == 0 {
                                {
                                    let mut o = outcome.lock().unwrap();
                                    o.succeeded.push(sid.clone());
                                }
                                increment_and_emit(&state, &app, &sid);
                                events::log(&app, "gui", &format!("[batch] {sid} -> OK"), false);
                                events::toast(
                                    &app,
                                    "ok",
                                    &format!("并行运行完成 · {sid}"),
                                    "全部流程已正常结束。",
                                );
                            } else {
                                {
                                    let mut o = outcome.lock().unwrap();
                                    o.failed.push(sid.clone());
                                }
                                events::log(
                                    &app,
                                    "gui",
                                    &format!("[batch] {sid} -> FAILED (exit code {})", outcome_res.code),
                                    true,
                                );
                                events::toast(
                                    &app,
                                    "fail",
                                    &format!("并行运行失败 · {sid}"),
                                    &format!("退出码 {}，请查看日志排查。", outcome_res.code),
                                );
                            }
                        }
                        Err(e) => {
                            {
                                let mut o = outcome.lock().unwrap();
                                o.failed.push(sid.clone());
                            }
                            events::log(&app, "gui", &format!("[batch] {sid} -> FAILED: {e}"), true);
                            events::toast(
                                &app,
                                "fail",
                                &format!("并行运行失败 · {sid}"),
                                &format!("运行失败：{e}"),
                            );
                        }
                    }
                }

                // 正常路径：无论成功/失败/跳过都关闭本学号的 VM。
                shutdown_student(&state, &app, &sid, Some(&sid));
                {
                    let mut a = active.lock().unwrap();
                    a.remove(&sid);
                }
                let done = completed.fetch_add(1, Ordering::SeqCst) + 1;
                emit_parallel_progress(&app, active.lock().unwrap().len(), done, total);
            }
        }));
    }
    for h in joins {
        let _ = h.join();
    }

    // 收尾：无条件关闭所有实例（即使 stop 为真也要跑；run_script 不检查 stop）。
    events::log(app, "gui", "=== Closing all emulator instances ===", false);
    shutdown_instances(state, app, None, Duration::from_secs(5 * MINUTE));

    let outcome = outcome.lock().unwrap();
    if state.stop.load(Ordering::SeqCst) {
        events::log(app, "gui", "=== Parallel batch stopped by user ===", false);
        events::run_state(app, "stopped", None, None, Some(total), "已停止", "neutral");
        events::toast(
            app,
            "neutral",
            "并行运行已停止",
            &format!("已完成 {} 个，失败 {} 个。", outcome.succeeded.len(), outcome.failed.len()),
        );
        return;
    }

    let summary = if outcome.succeeded.is_empty() && outcome.failed.is_empty() && !outcome.skipped.is_empty() {
        format!(
            "并行运行完成：{} 个学号均已达上限（本周或总次数），已全部跳过。",
            outcome.skipped.len()
        )
    } else {
        let mut text = format!("并行运行完成：成功 {} 个", outcome.succeeded.len());
        if !outcome.skipped.is_empty() {
            text.push_str(&format!("，跳过 {} 个（已达上限）", outcome.skipped.len()));
        }
        if outcome.failed.is_empty() {
            text.push_str("，全部通过");
        } else {
            text.push_str(&format!("，失败 {} 个（{}）", outcome.failed.len(), outcome.failed.join(", ")));
        }
        text.push('。');
        text
    };

    let has_failure = !outcome.failed.is_empty();
    events::log(app, "gui", &format!("=== {summary} ==="), has_failure);
    events::run_state(
        app,
        if has_failure { "failed" } else { "done" },
        None,
        None,
        Some(total),
        if has_failure { "部分失败" } else { "完成" },
        if has_failure { "error" } else { "ok" },
    );
    events::toast(
        app,
        if has_failure { "fail" } else { "ok" },
        if has_failure { "并行运行完成（部分失败）" } else { "并行运行完成" },
        &summary,
    );
}

/// 停止：置标志 → Ctrl+C → 2s 宽限后 Job 强杀。空闲时只记日志。
pub fn stop(state: &Arc<AppState>, app: &AppHandle) {
    let busy = state.busy.load(Ordering::SeqCst);
    let handles: Vec<StopHandle> = {
        let mut map = state.current.lock().unwrap();
        map.drain().map(|(_, handle)| handle).collect()
    };
    if !busy && handles.is_empty() {
        events::log(app, "gui", "=== Nothing is running ===", false);
        return;
    }

    state.stop.store(true, Ordering::SeqCst);
    let signalled = handles.iter().filter(|handle| handle.request_ctrl_c()).count();
    proc::terminate_after_grace(handles, Duration::from_secs(2));
    events::log(
        app,
        "gui",
        &format!(
            "=== Stop requested: Ctrl+C signalled {signalled} process(es); terminating within 2s ==="
        ),
        false,
    );
    events::run_state(app, "stopping", None, None, None, "停止中…", "neutral");
}
