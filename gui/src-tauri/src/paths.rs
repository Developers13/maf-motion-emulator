use std::path::{Path, PathBuf};

/// 旧 GUI 的项目根定位方式：从可执行文件目录向上找含 `tools/e2e.py` 的目录。
/// 开发时 exe 在 `gui/src-tauri/target/debug`，向上能走到仓库根；发布版装在
/// Program Files 时找不到，回退到当前工作目录（设置页仍可覆盖）。
pub fn find_project_root() -> PathBuf {
    if let Ok(explicit) = std::env::var("MAF_PROJECT_ROOT") {
        let path = PathBuf::from(explicit);
        if path.join("tools").join("e2e.py").is_file() {
            return path;
        }
    }

    let mut starts: Vec<PathBuf> = Vec::new();
    if let Ok(exe) = std::env::current_exe() {
        if let Some(dir) = exe.parent() {
            starts.push(dir.to_path_buf());
        }
    }
    if let Ok(cwd) = std::env::current_dir() {
        starts.push(cwd);
    }
    if let Some(manifest) = option_env!("CARGO_MANIFEST_DIR") {
        starts.push(PathBuf::from(manifest));
    }

    for start in starts {
        let mut current: Option<&Path> = Some(start.as_path());
        for _ in 0..10 {
            let Some(dir) = current else { break };
            if dir.join("tools").join("e2e.py").is_file() {
                return dir.to_path_buf();
            }
            current = dir.parent();
        }
    }

    std::env::current_dir().unwrap_or_else(|_| PathBuf::from("."))
}

pub fn gui_dir(root: &Path) -> PathBuf {
    root.join("gui")
}

pub fn scripts_dir(root: &Path) -> PathBuf {
    gui_dir(root).join("scripts")
}

pub fn nemu_path(root: &Path) -> PathBuf {
    gui_dir(root).join("tools").join("nemu").join("nemu.exe")
}

pub fn settings_path(root: &Path) -> PathBuf {
    gui_dir(root).join("gui-settings.json")
}

pub fn status_path(root: &Path) -> PathBuf {
    gui_dir(root).join("exercise-status.json")
}

pub fn user_list_path(root: &Path) -> PathBuf {
    root.join("user-list.json")
}
