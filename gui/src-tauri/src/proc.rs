//! 子进程管理：每次启动独立 Job Object，停止时先尽力 Ctrl+C、宽限后必杀整棵树。
//!
//! 旧 WinForms 版踩过的坑在这里一并规避：
//! - `py.exe` 只是转发器，启动后立刻退出；必须先用 `py -c "import sys;print(sys.executable)"`
//!   解析成真实 `python.exe`，否则"正在运行"判定与进程树都会落空。
//! - 子进程一律用 CREATE_NO_WINDOW，GUI 自身无控制台才能 AttachConsole 到子进程发 Ctrl+C。

use std::io::{BufRead, BufReader, Read};
use std::path::{Path, PathBuf};
use std::process::{Child, Command, Stdio};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{mpsc, Arc, Mutex, OnceLock};
use std::time::Duration;

pub const CREATE_NO_WINDOW: u32 = 0x0800_0000;

#[cfg(windows)]
use std::os::windows::process::CommandExt;

/// CREATE_NO_WINDOW 只存在于 Windows；抽一层便于非 Windows 平台编译（本项目只发布 Windows）。
fn hide_window(command: &mut Command) {
    #[cfg(windows)]
    command.creation_flags(CREATE_NO_WINDOW);
    #[cfg(not(windows))]
    let _ = command;
}

#[cfg(windows)]
mod win {
    use windows::core::PCWSTR;
    use windows::Win32::Foundation::{CloseHandle, HANDLE};
    use windows::Win32::System::Console::{
        AttachConsole, FreeConsole, GenerateConsoleCtrlEvent, SetConsoleCtrlHandler, CTRL_C_EVENT,
    };
    use windows::Win32::System::JobObjects::{
        AssignProcessToJobObject, CreateJobObjectW, JobObjectExtendedLimitInformation,
        SetInformationJobObject, TerminateJobObject, JOBOBJECT_EXTENDED_LIMIT_INFORMATION,
        JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
    };
    use windows::Win32::System::Threading::{OpenProcess, PROCESS_SET_QUOTA, PROCESS_TERMINATE};

    pub struct JobHandle(pub HANDLE);

    // HANDLE 是内核对象句柄，跨线程移动/共享是安全的（仅关闭一次，由 Drop 负责）。
    unsafe impl Send for JobHandle {}
    unsafe impl Sync for JobHandle {}

    impl Drop for JobHandle {
        fn drop(&mut self) {
            unsafe {
                let _ = CloseHandle(self.0);
            }
        }
    }

    pub fn create_job() -> Option<JobHandle> {
        unsafe {
            let job = CreateJobObjectW(None, PCWSTR::null()).ok()?;
            let mut info = JOBOBJECT_EXTENDED_LIMIT_INFORMATION::default();
            info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
            let set = SetInformationJobObject(
                job,
                JobObjectExtendedLimitInformation,
                &info as *const _ as *const std::ffi::c_void,
                std::mem::size_of::<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>() as u32,
            );
            if set.is_err() {
                let _ = CloseHandle(job);
                return None;
            }
            Some(JobHandle(job))
        }
    }

    pub fn assign(job: &JobHandle, pid: u32) {
        unsafe {
            if let Ok(process) = OpenProcess(PROCESS_SET_QUOTA | PROCESS_TERMINATE, false, pid) {
                let _ = AssignProcessToJobObject(job.0, process);
                let _ = CloseHandle(process);
            }
        }
    }

    pub fn terminate(job: &JobHandle) {
        unsafe {
            let _ = TerminateJobObject(job.0, 1);
        }
    }

    /// 尽力优雅停止：附加到目标所在控制台并广播 CTRL_C_EVENT。
    /// GUI 自身无控制台，所以先 FreeConsole；若当前有控制台（debug 终端、cargo test）
    /// AttachConsole 会失败，调用方据此退回强制终止。
    pub fn send_ctrl_c(pid: u32) -> bool {
        unsafe {
            let _ = FreeConsole();
            if AttachConsole(pid).is_err() {
                return false;
            }
            let _ = SetConsoleCtrlHandler(None, true);
            let ok = GenerateConsoleCtrlEvent(CTRL_C_EVENT, 0).is_ok();
            let _ = FreeConsole();
            let _ = SetConsoleCtrlHandler(None, false);
            ok
        }
    }
}

#[cfg(not(windows))]
mod win {
    pub struct JobHandle;
    pub fn create_job() -> Option<JobHandle> {
        None
    }
    pub fn assign(_job: &JobHandle, _pid: u32) {}
    pub fn terminate(_job: &JobHandle) {}
    pub fn send_ctrl_c(_pid: u32) -> bool {
        false
    }
}

/// 在 Job 之外预启动 adb server。
///
/// 并行运行时会同时存在多个 e2e 子进程；每个子进程都跑在自己的
/// JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE Job 里。若 adb server 是某个子进程孵化的，
/// 该子进程一结束，Job 关闭就会把共享的 adb server 一起杀掉，打断其它并行任务。
/// 因此在批次开始前由 GUI 自己（不 assign Job）拉起 server，后续 e2e 复用。
pub fn ensure_adb_server(mumu_manager_path: &str) -> bool {
    let adb = Path::new(mumu_manager_path)
        .parent()
        .map(|dir| dir.join("adb.exe"))
        .filter(|path| path.is_file())
        .unwrap_or_else(|| PathBuf::from("adb"));

    let mut command = Command::new(adb);
    command
        .args(["start-server"])
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::null());
    hide_window(&mut command);
    command.status().map(|status| status.success()).unwrap_or(false)
}

#[derive(Clone)]
pub struct SpawnSpec {
    pub program: String,
    pub args: Vec<String>,
    pub cwd: PathBuf,
    pub env: Vec<(String, String)>,
    /// 日志来源标记（python / mumu / adb-prep / root-recover），只用于界面分组。
    pub label: String,
}

impl SpawnSpec {
    pub fn python(launcher: &str, args: Vec<String>, cwd: PathBuf, env: Vec<(String, String)>) -> Self {
        Self {
            program: resolve_python_launcher(launcher),
            args,
            cwd,
            env,
            label: "python".into(),
        }
    }

    pub fn powershell(
        host: &str,
        script: &Path,
        params: &[(&str, String)],
        cwd: PathBuf,
    ) -> Self {
        let mut args = vec![
            "-NoProfile".to_string(),
            "-ExecutionPolicy".to_string(),
            "Bypass".to_string(),
            "-File".to_string(),
            script.to_string_lossy().to_string(),
        ];
        for (key, value) in params {
            args.push(format!("-{key}"));
            args.push(value.clone());
        }
        let label = script
            .file_stem()
            .map(|s| s.to_string_lossy().to_string())
            .unwrap_or_else(|| "powershell".into());
        Self {
            program: host.to_string(),
            args,
            cwd,
            env: Vec::new(),
            label,
        }
    }
}

type LineSink = Arc<dyn Fn(bool, String) + Send + Sync>;

pub struct ManagedProcess {
    pub pid: u32,
    job: Option<Arc<win::JobHandle>>,
    exited: Arc<AtomicBool>,
    exit_rx: mpsc::Receiver<i32>,
}

impl ManagedProcess {
    pub fn wait(&self, timeout: Duration) -> Result<i32, WaitError> {
        match self.exit_rx.recv_timeout(timeout) {
            Ok(code) => Ok(code),
            Err(mpsc::RecvTimeoutError::Timeout) => Err(WaitError::Timeout),
            Err(mpsc::RecvTimeoutError::Disconnected) => Err(WaitError::Disconnected),
        }
    }

    /// 无条件杀整棵进程树（Job 内全部进程）。
    pub fn terminate(&self) {
        if let Some(job) = &self.job {
            win::terminate(job);
        }
    }
}

#[derive(Debug)]
pub enum WaitError {
    Timeout,
    Disconnected,
}

/// 启动子进程并把 stdout/stderr 行流式回调出来。
pub fn spawn(
    spec: SpawnSpec,
    on_line: LineSink,
) -> std::io::Result<ManagedProcess> {
    let mut command = Command::new(&spec.program);
    command
        .args(&spec.args)
        .current_dir(&spec.cwd)
        .stdin(Stdio::null())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped());
    hide_window(&mut command);
    for (key, value) in &spec.env {
        command.env(key, value);
    }

    let mut child = command.spawn()?;
    let pid = child.id();

    let job = win::create_job().map(Arc::new);
    if let Some(job) = &job {
        win::assign(job, pid);
    }

    if let Some(stdout) = child.stdout.take() {
        stream_lines(stdout, false, Arc::clone(&on_line));
    }
    if let Some(stderr) = child.stderr.take() {
        stream_lines(stderr, true, Arc::clone(&on_line));
    }

    let exited = Arc::new(AtomicBool::new(false));
    let (tx, exit_rx) = mpsc::channel();
    let exited_flag = Arc::clone(&exited);
    std::thread::spawn(move || {
        let code = wait_child(child);
        exited_flag.store(true, Ordering::SeqCst);
        let _ = tx.send(code);
    });

    Ok(ManagedProcess {
        pid,
        job,
        exited,
        exit_rx,
    })
}

fn wait_child(mut child: Child) -> i32 {
    match child.wait() {
        Ok(status) => status.code().unwrap_or(-1),
        Err(_) => -1,
    }
}

fn stream_lines<R: Read + Send + 'static>(reader: R, is_error: bool, sink: LineSink) {
    std::thread::spawn(move || {
        let mut reader = BufReader::new(reader);
        let mut buffer = Vec::new();
        loop {
            buffer.clear();
            match reader.read_until(b'\n', &mut buffer) {
                Ok(0) => break,
                Ok(_) => {
                    let raw = String::from_utf8_lossy(&buffer);
                    let text =
                        strip_ansi(raw.trim_end_matches(|c| c == '\r' || c == '\n'));
                    sink(is_error, text.to_string());
                }
                Err(_) => break,
            }
        }
    });
}

/// MaaFW 等程序会往 stdout 写 ANSI 颜色序列；界面按纯文本渲染，
/// 不剥掉会显示成 `[31m…[0m` 这样的乱码。这里剥掉 CSI 序列。
fn strip_ansi(line: &str) -> String {
    let mut out = String::with_capacity(line.len());
    let mut chars = line.chars().peekable();
    while let Some(c) = chars.next() {
        if c != '\u{1b}' {
            out.push(c);
            continue;
        }
        if chars.peek() == Some(&'[') {
            chars.next();
            // CSI：参数/中间字节后以 0x40–0x7E 范围的字节结束。
            while let Some(n) = chars.next() {
                if ('\u{40}'..='\u{7e}').contains(&n) {
                    break;
                }
            }
        }
    }
    out
}

/// 兜底：用系统 taskkill 杀掉指定 pid 及其后代（Job 不可用时的退路）。
fn kill_tree(pid: u32) {
    let mut command = Command::new("taskkill");
    command.args(["/PID", &pid.to_string(), "/T", "/F"]);
    hide_window(&mut command);
    let _ = command.output();
}

/// 把 `py` / `python` 解析成真实解释器；缓存结果，避免每次运行都探测。
pub fn resolve_python_launcher(launcher: &str) -> String {
    if launcher.trim().is_empty() {
        return "python".into();
    }
    static CACHE: OnceLock<Mutex<std::collections::HashMap<String, String>>> = OnceLock::new();
    let cache = CACHE.get_or_init(|| Mutex::new(std::collections::HashMap::new()));
    if let Ok(map) = cache.lock() {
        if let Some(hit) = map.get(launcher) {
            return hit.clone();
        }
    }

    let resolved = probe_python(launcher).unwrap_or_else(|| launcher.to_string());
    if let Ok(mut map) = cache.lock() {
        map.insert(launcher.to_string(), resolved.clone());
    }
    resolved
}

fn probe_python(launcher: &str) -> Option<String> {
    let mut command = Command::new(launcher);
    command
        .args(["-c", "import sys;print(sys.executable)"])
        .stdin(Stdio::null())
        .stdout(Stdio::piped())
        .stderr(Stdio::null());
    hide_window(&mut command);
    let output = command
        .output()
        .ok()?;
    if !output.status.success() {
        return None;
    }
    let path = String::from_utf8_lossy(&output.stdout).trim().to_string();
    if Path::new(&path).is_file() {
        Some(path)
    } else {
        None
    }
}

/// 可跨线程持有的停止句柄（ManagedProcess 内含 Receiver，不能整体放进 Arc 跨线程）。
#[derive(Clone)]
pub struct StopHandle {
    pid: u32,
    job: Option<Arc<win::JobHandle>>,
    exited: Arc<AtomicBool>,
}

impl StopHandle {
    pub fn is_running(&self) -> bool {
        !self.exited.load(Ordering::SeqCst)
    }

    pub fn request_ctrl_c(&self) -> bool {
        if !self.is_running() {
            return false;
        }
        win::send_ctrl_c(self.pid)
    }

    pub fn terminate(&self) {
        if let Some(job) = &self.job {
            win::terminate(job);
        } else {
            // Job 创建/分配失败时（例如宿主已在不可嵌套的 Job 里），退回 taskkill 杀树。
            kill_tree(self.pid);
        }
    }
}

impl ManagedProcess {
    pub fn stop_handle(&self) -> StopHandle {
        StopHandle {
            pid: self.pid,
            job: self.job.clone(),
            exited: Arc::clone(&self.exited),
        }
    }
}

/// 宽限期后仍未退出的进程强制杀树（供 stop 使用）。
pub fn terminate_after_grace(handles: Vec<StopHandle>, grace: Duration) {
    std::thread::spawn(move || {
        std::thread::sleep(grace);
        for handle in handles {
            if handle.is_running() {
                handle.terminate();
            }
        }
    });
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::Arc;

    #[test]
    fn terminate_job_kills_child_quickly() {
        let spec = SpawnSpec {
            program: "cmd".into(),
            args: vec!["/C".into(), "ping -n 60 127.0.0.1 >nul".into()],
            cwd: std::env::temp_dir(),
            env: Vec::new(),
            label: "test".into(),
        };
        let sink: LineSink = Arc::new(|_, _| {});
        let process = spawn(spec, sink).expect("spawn failed");
        std::thread::sleep(Duration::from_millis(500));

        process.terminate();
        let code = process.wait(Duration::from_secs(5));
        assert!(code.is_ok(), "job termination did not stop the child");
    }

    #[test]
    fn strip_ansi_removes_color_codes() {
        assert_eq!(
            strip_ansi("\u{1b}[31m[2026-11-15][ERR] boom\u{1b}[0m"),
            "[2026-11-15][ERR] boom"
        );
        assert_eq!(strip_ansi("plain line"), "plain line");
        assert_eq!(strip_ansi("\u{1b}[1;32mgreen\u{1b}[0m tail"), "green tail");
    }
}
