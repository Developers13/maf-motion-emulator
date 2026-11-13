use serde::Serialize;
use std::collections::BTreeSet;
use std::path::{Path, PathBuf};

#[derive(Serialize, Clone, Debug)]
pub struct ScriptHost {
    pub path: String,
    pub label: String,
}

fn push_unique(out: &mut Vec<ScriptHost>, seen: &mut BTreeSet<String>, path: PathBuf, label: &str) {
    if !path.is_file() {
        return;
    }
    let key = path.to_string_lossy().to_lowercase();
    if !seen.insert(key) {
        return;
    }
    out.push(ScriptHost {
        path: path.to_string_lossy().to_string(),
        label: label.to_string(),
    });
}

/// 扫描可用的 PowerShell 宿主，顺序：系统 5.1 → PowerShell 7 安装目录 → Store → PATH。
/// 与旧 ScriptHostScanner 的候选来源一致。
pub fn scan() -> Vec<ScriptHost> {
    let mut out = Vec::new();
    let mut seen = BTreeSet::new();

    let system_root = std::env::var("SystemRoot").unwrap_or_else(|_| r"C:\Windows".into());
    push_unique(
        &mut out,
        &mut seen,
        Path::new(&system_root)
            .join("System32")
            .join("WindowsPowerShell")
            .join("v1.0")
            .join("powershell.exe"),
        "Windows PowerShell 5.1",
    );

    if let Ok(program_files) = std::env::var("ProgramFiles") {
        let base = Path::new(&program_files).join("PowerShell");
        if let Ok(entries) = std::fs::read_dir(&base) {
            let mut dirs: Vec<PathBuf> = entries
                .flatten()
                .map(|e| e.path())
                .filter(|p| p.is_dir())
                .collect();
            dirs.sort();
            for dir in dirs {
                let name = dir
                    .file_name()
                    .map(|n| n.to_string_lossy().to_string())
                    .unwrap_or_default();
                push_unique(
                    &mut out,
                    &mut seen,
                    dir.join("pwsh.exe"),
                    &format!("PowerShell {name}"),
                );
            }
        }
    }

    if let Ok(local_app_data) = std::env::var("LOCALAPPDATA") {
        push_unique(
            &mut out,
            &mut seen,
            Path::new(&local_app_data)
                .join("Microsoft")
                .join("WindowsApps")
                .join("pwsh.exe"),
            "PowerShell (Store)",
        );
    }

    if let Ok(path_var) = std::env::var("PATH") {
        for entry in std::env::split_paths(&path_var) {
            push_unique(
                &mut out,
                &mut seen,
                entry.join("pwsh.exe"),
                "PowerShell (PATH)",
            );
        }
    }

    out
}

/// 运行时选择脚本宿主：配置路径存在就用它，否则回退系统 powershell.exe。
pub fn resolve(configured: &str) -> String {
    if !configured.trim().is_empty() && Path::new(configured).is_file() {
        return configured.to_string();
    }
    let system_root = std::env::var("SystemRoot").unwrap_or_else(|_| r"C:\Windows".into());
    let candidate = Path::new(&system_root)
        .join("System32")
        .join("WindowsPowerShell")
        .join("v1.0")
        .join("powershell.exe");
    if candidate.is_file() {
        return candidate.to_string_lossy().to_string();
    }
    "powershell.exe".to_string()
}
