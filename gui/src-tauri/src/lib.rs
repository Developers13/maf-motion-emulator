mod events;
mod hosts;
mod orchestrator;
mod paths;
mod proc;
mod settings;
mod status;
mod userlist;

use orchestrator::AppState;
use serde::Serialize;
use std::sync::Arc;
use tauri::{AppHandle, State};

#[derive(Serialize)]
pub struct Bootstrap {
    project_root: String,
    settings: settings::AppSettings,
    user_rows: Vec<userlist::UserRow>,
    statuses: status::StatusMap,
    script_hosts: Vec<hosts::ScriptHost>,
    is_elevated: bool,
    version: String,
}

fn build_bootstrap(root: &std::path::Path) -> Bootstrap {
    Bootstrap {
        project_root: root.to_string_lossy().to_string(),
        settings: settings::load(&paths::settings_path(root)),
        user_rows: userlist::load(&paths::user_list_path(root)).unwrap_or_default(),
        statuses: status::load(&paths::status_path(root)),
        script_hosts: hosts::scan(),
        is_elevated: is_elevated(),
        version: env!("CARGO_PKG_VERSION").to_string(),
    }
}

#[tauri::command]
fn bootstrap() -> Bootstrap {
    let root = paths::find_project_root();
    build_bootstrap(&root)
}

#[tauri::command]
fn save_settings(
    state: State<'_, Arc<AppState>>,
    settings: settings::AppSettings,
) -> Result<(), String> {
    let settings = settings.normalize();
    let path = paths::settings_path(&state.root);
    settings::save(&path, &settings).map_err(|e| e.to_string())?;
    *state.settings.lock().unwrap() = settings;
    Ok(())
}

#[tauri::command]
fn save_user_rows(
    state: State<'_, Arc<AppState>>,
    rows: Vec<userlist::UserRow>,
) -> Result<(), String> {
    userlist::save(&paths::user_list_path(&state.root), &rows).map_err(|e| e.to_string())
}

#[tauri::command]
fn list_script_hosts() -> Vec<hosts::ScriptHost> {
    hosts::scan()
}

/// 手动填写/清除某学号的次数缓存。
///
/// `week`/`total` 都为 None → 清除该学号缓存；都为 Some → 记为"已确认"（参与
/// 批量预跳过），下一次 App 实读会覆盖。只填一个视为失败，避免半套数据。
#[tauri::command]
fn save_student_counts(
    app: AppHandle,
    state: State<'_, Arc<AppState>>,
    student: String,
    week: Option<u32>,
    total: Option<u32>,
) -> Result<(), String> {
    if student.trim().is_empty() {
        return Err("学号为空。".into());
    }
    let mut map = state.statuses.lock().unwrap();
    match (week, total) {
        (Some(week), Some(total)) => status::set_manual(&mut map, &student, week, total),
        (None, None) => status::clear(&mut map, &student),
        _ => return Err("本周次数与总次数必须同时填写或同时留空。".into()),
    }
    status::save(&paths::status_path(&state.root), &map).map_err(|e| e.to_string())?;
    if let Some(entry) = map.get(&student) {
        events::counts(
            &app,
            events::CountsEvent {
                student: student.clone(),
                week: entry.week,
                total: entry.total,
                week_key: entry.week_key.clone(),
                read_at: entry.read_at.clone(),
                confirmed: entry.confirmed,
            },
        );
    }
    Ok(())
}

#[tauri::command]
fn start_run(
    app: AppHandle,
    state: State<'_, Arc<AppState>>,
    ids: Vec<String>,
) -> Result<(), String> {
    let ids: Vec<String> = ids
        .into_iter()
        .filter(|id| !id.trim().is_empty())
        .collect();
    if ids.is_empty() {
        return Err("未选中任何学号。".into());
    }
    orchestrator::start(Arc::clone(&state), app, ids);
    Ok(())
}

#[tauri::command]
fn stop_run(app: AppHandle, state: State<'_, Arc<AppState>>) {
    orchestrator::stop(&state, &app);
}

#[tauri::command]
fn hot_reload(state: State<'_, Arc<AppState>>) -> Bootstrap {
    let root = state.root.clone();
    let fresh = build_bootstrap(&root);
    *state.settings.lock().unwrap() = fresh.settings.clone();
    *state.statuses.lock().unwrap() = fresh.statuses.clone();
    fresh
}

#[cfg(windows)]
fn is_elevated() -> bool {
    use windows::Win32::Foundation::{CloseHandle, HANDLE};
    use windows::Win32::Security::{
        GetTokenInformation, TokenElevation, TOKEN_ELEVATION, TOKEN_QUERY,
    };
    use windows::Win32::System::Threading::{GetCurrentProcess, OpenProcessToken};

    unsafe {
        let mut token = HANDLE::default();
        if OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &mut token).is_err() {
            return false;
        }
        let mut elevation = TOKEN_ELEVATION::default();
        let mut returned = 0u32;
        let ok = GetTokenInformation(
            token,
            TokenElevation,
            Some(&mut elevation as *mut _ as *mut core::ffi::c_void),
            std::mem::size_of::<TOKEN_ELEVATION>() as u32,
            &mut returned,
        );
        let _ = CloseHandle(token);
        ok.is_ok() && elevation.TokenIsElevated != 0
    }
}

#[cfg(not(windows))]
fn is_elevated() -> bool {
    false
}

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    let root = paths::find_project_root();
    let settings = settings::load(&paths::settings_path(&root));
    let statuses = status::load(&paths::status_path(&root));
    let state = Arc::new(AppState::new(root, settings, statuses));

    tauri::Builder::default()
        .plugin(tauri_plugin_notification::init())
        .manage(state)
        .invoke_handler(tauri::generate_handler![
            bootstrap,
            save_settings,
            save_user_rows,
            save_student_counts,
            list_script_hosts,
            start_run,
            stop_run,
            hot_reload
        ])
        .run(tauri::generate_context!())
        .expect("error while running tauri application");
}
