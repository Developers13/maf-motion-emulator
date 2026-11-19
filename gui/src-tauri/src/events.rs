use serde::Serialize;
use tauri::{AppHandle, Emitter};

/// 日志行事件：`kind` 由前端按旧规则（banner/ok/error/info/default）再细分，
/// 这里只带上 is_error 与来源，避免后端重复实现配色。
#[derive(Serialize, Clone)]
pub struct LogEvent {
    pub time: String,
    pub line: String,
    pub is_error: bool,
    pub source: String,
}

#[derive(Serialize, Clone)]
pub struct RunStateEvent {
    /// idle | preparing | running | batch | stopping | stopped | done | failed
    pub phase: String,
    pub student: Option<String>,
    pub index: Option<usize>,
    pub total: Option<usize>,
    pub message: String,
    /// neutral | info | ok | error
    pub tone: String,
}

#[derive(Serialize, Clone)]
pub struct CountsEvent {
    pub student: String,
    pub week: u32,
    pub total: u32,
    pub week_key: String,
    pub read_at: String,
    /// true = App 实读确认；false = 本地 +1 估算（待审核）。
    pub confirmed: bool,
}

#[derive(Serialize, Clone)]
pub struct ToastEvent {
    /// ok | fail | neutral
    pub tone: String,
    pub title: String,
    pub body: String,
}

pub fn log(app: &AppHandle, source: &str, line: &str, is_error: bool) {
    let event = LogEvent {
        time: chrono::Local::now().format("%H:%M:%S").to_string(),
        line: line.to_string(),
        is_error,
        source: source.to_string(),
    };
    let _ = app.emit("log", event);
}

pub fn run_state(
    app: &AppHandle,
    phase: &str,
    student: Option<&str>,
    index: Option<usize>,
    total: Option<usize>,
    message: &str,
    tone: &str,
) {
    let _ = app.emit(
        "run-state",
        RunStateEvent {
            phase: phase.to_string(),
            student: student.map(|s| s.to_string()),
            index,
            total,
            message: message.to_string(),
            tone: tone.to_string(),
        },
    );
}

pub fn toast(app: &AppHandle, tone: &str, title: &str, body: &str) {
    let _ = app.emit(
        "toast",
        ToastEvent {
            tone: tone.to_string(),
            title: title.to_string(),
            body: body.to_string(),
        },
    );
}

pub fn counts(app: &AppHandle, event: CountsEvent) {
    let _ = app.emit("student-counts", event);
}
