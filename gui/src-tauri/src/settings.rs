use serde::{Deserialize, Serialize};
use serde_json::Value;
use std::collections::BTreeMap;
use std::fs;
use std::io;
use std::path::Path;

pub const DEFAULT_MUMU_MANAGER: &str =
    r"C:\Program Files\Netease\MuMu\nx_main\MuMuManager.exe";
pub const DEFAULT_TEMPLATE: &str = "模板";

fn default_python_launcher() -> String {
    "py".into()
}

fn default_environment() -> BTreeMap<String, String> {
    let mut map = BTreeMap::new();
    map.insert("PYTHONUTF8".into(), "1".into());
    map
}

fn default_mumu_manager() -> String {
    DEFAULT_MUMU_MANAGER.into()
}

fn default_template() -> String {
    DEFAULT_TEMPLATE.into()
}

fn default_week_limit() -> u32 {
    5
}

fn default_total_limit() -> u32 {
    32
}

fn default_auto_scroll() -> bool {
    true
}

fn default_skip_on_confirmed_counts() -> bool {
    true
}

fn default_execution_mode() -> String { "serial".into() }
fn default_parallel_vm_count() -> u32 { 2 }

/// 与旧 `gui/gui-settings.json` 完全同构：PascalCase 字段名，不能改动，
/// 否则用户已有配置会被当成默认值。
#[derive(Serialize, Deserialize, Clone, Debug)]
pub struct AppSettings {
    #[serde(rename = "PythonLauncher", default = "default_python_launcher")]
    pub python_launcher: String,
    #[serde(rename = "ProjectRoot", default)]
    pub project_root: String,
    #[serde(rename = "Environment", default = "default_environment")]
    pub environment: BTreeMap<String, String>,
    #[serde(rename = "MuMuManagerPath", default = "default_mumu_manager")]
    pub mumu_manager_path: String,
    #[serde(rename = "TemplateVmName", default = "default_template")]
    pub template_vm_name: String,
    #[serde(rename = "ScriptHostPath", default)]
    pub script_host_path: String,
    #[serde(rename = "LastStudentId", default)]
    pub last_student_id: String,
    #[serde(rename = "SelectedStudents", default)]
    pub selected_students: Vec<String>,
    #[serde(rename = "WeekLimit", default = "default_week_limit")]
    pub week_limit: u32,
    #[serde(rename = "TotalLimit", default = "default_total_limit")]
    pub total_limit: u32,
    /// 批量运行时，若次数已经 App 实读确认且达上限，直接跳过、不开模拟器。
    /// +1 估算（Confirmed=false）永远不触发预跳过。
    #[serde(rename = "SkipOnConfirmedCounts", default = "default_skip_on_confirmed_counts")]
    pub skip_on_confirmed_counts: bool,
    /// "serial" 或 "parallel"；缺省串行（保持旧配置行为）。
    #[serde(rename = "ExecutionMode", default = "default_execution_mode")]
    pub execution_mode: String,
    /// 并行时同时开启的虚拟机台数；缺省 2，保存时夹取 1..=8。
    #[serde(rename = "ParallelVmCount", default = "default_parallel_vm_count")]
    pub parallel_vm_count: u32,
    #[serde(rename = "AutoScroll", default = "default_auto_scroll")]
    pub auto_scroll: bool,
}

impl Default for AppSettings {
    fn default() -> Self {
        Self {
            python_launcher: default_python_launcher(),
            project_root: String::new(),
            environment: default_environment(),
            mumu_manager_path: default_mumu_manager(),
            template_vm_name: default_template(),
            script_host_path: String::new(),
            last_student_id: String::new(),
            selected_students: Vec::new(),
            week_limit: default_week_limit(),
            total_limit: default_total_limit(),
            skip_on_confirmed_counts: default_skip_on_confirmed_counts(),
            execution_mode: default_execution_mode(),
            parallel_vm_count: default_parallel_vm_count(),
            auto_scroll: default_auto_scroll(),
        }
    }
}

impl AppSettings {
    /// 归一化规则与旧 `AppSettings.Normalize` 一致（空启动器回退 py、上限 ≤0 回退等）。
    pub fn normalize(mut self) -> Self {
        if self.python_launcher.trim().is_empty() {
            self.python_launcher = default_python_launcher();
        }
        if self.mumu_manager_path.trim().is_empty() {
            self.mumu_manager_path = default_mumu_manager();
        }
        if self.template_vm_name.trim().is_empty() {
            self.template_vm_name = default_template();
        }
        if self.week_limit == 0 {
            self.week_limit = default_week_limit();
        }
        if self.total_limit == 0 {
            self.total_limit = default_total_limit();
        }
        self.selected_students.retain(|id| !id.trim().is_empty());
        if self.execution_mode != "parallel" {
            self.execution_mode = default_execution_mode();
        }
        if self.parallel_vm_count == 0 {
            self.parallel_vm_count = default_parallel_vm_count();
        }
        if self.parallel_vm_count > 8 {
            self.parallel_vm_count = 8;
        }
        self
    }
}

pub fn load(path: &Path) -> AppSettings {
    let Ok(text) = fs::read_to_string(path) else {
        return AppSettings::default();
    };
    match serde_json::from_str::<Value>(&text) {
        Ok(value) => serde_json::from_value::<AppSettings>(value)
            .map(AppSettings::normalize)
            .unwrap_or_default(),
        // 坏文件按旧实现静默降级为默认值，而不是让 GUI 起不来。
        Err(_) => AppSettings::default(),
    }
}

pub fn save(path: &Path, settings: &AppSettings) -> io::Result<()> {
    let json = serde_json::to_string_pretty(settings)?;
    write_atomic(path, json.as_bytes())
}

/// 临时文件 + rename：避免写一半把用户配置写坏。
pub fn write_atomic(path: &Path, bytes: &[u8]) -> io::Result<()> {
    if let Some(dir) = path.parent() {
        fs::create_dir_all(dir)?;
    }
    let tmp = path.with_extension("json.tmp");
    fs::write(&tmp, bytes)?;
    fs::rename(&tmp, path)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn default_matches_legacy_gui() {
        let settings = AppSettings::default();
        assert_eq!(settings.python_launcher, "py");
        assert_eq!(settings.week_limit, 5);
        assert_eq!(settings.total_limit, 32);
        assert!(settings.auto_scroll);
        assert_eq!(settings.environment.get("PYTHONUTF8").map(String::as_str), Some("1"));
        assert_eq!(settings.template_vm_name, "模板");
        assert_eq!(settings.execution_mode, "serial");
        assert_eq!(settings.parallel_vm_count, 2);
    }

    #[test]
    fn normalize_fills_blank_fields() {
        let settings = AppSettings {
            python_launcher: "  ".into(),
            mumu_manager_path: String::new(),
            template_vm_name: String::new(),
            week_limit: 0,
            total_limit: 0,
            selected_students: vec!["2025302319".into(), "  ".into()],
            ..AppSettings::default()
        }
        .normalize();

        assert_eq!(settings.python_launcher, "py");
        assert_eq!(settings.mumu_manager_path, DEFAULT_MUMU_MANAGER);
        assert_eq!(settings.week_limit, 5);
        assert_eq!(settings.total_limit, 32);
        assert_eq!(settings.selected_students, vec!["2025302319".to_string()]);
    }

    #[test]
    fn serializes_with_pascal_case_keys() {
        let json = serde_json::to_string_pretty(&AppSettings::default()).unwrap();
        for key in [
            "\"PythonLauncher\"",
            "\"Environment\"",
            "\"MuMuManagerPath\"",
            "\"TemplateVmName\"",
            "\"SelectedStudents\"",
            "\"WeekLimit\"",
            "\"TotalLimit\"",
            "\"SkipOnConfirmedCounts\"",
            "\"ExecutionMode\"",
            "\"ParallelVmCount\"",
            "\"AutoScroll\"",
        ] {
            assert!(json.contains(key), "missing {key}");
        }
    }

    #[test]
    fn loads_legacy_file_shape() {
        let json = r#"{
            "PythonLauncher": "python",
            "Environment": {"PYTHONUTF8": "1"},
            "WeekLimit": 4,
            "SelectedStudents": ["2025302319"]
        }"#;
        let parsed: AppSettings = serde_json::from_str(json).unwrap();
        let settings = parsed.normalize();
        assert_eq!(settings.python_launcher, "python");
        assert_eq!(settings.week_limit, 4);
        assert_eq!(settings.total_limit, 32); // 缺字段回退默认
        assert!(settings.skip_on_confirmed_counts); // 缺字段回退默认（开）
        assert_eq!(settings.execution_mode, "serial"); // 缺字段回退默认
        assert_eq!(settings.parallel_vm_count, 2); // 缺字段回退默认
        assert_eq!(settings.selected_students, vec!["2025302319".to_string()]);
    }

    #[test]
    fn normalize_clamps_execution_fields() {
        let settings = AppSettings {
            execution_mode: "weird".into(),
            parallel_vm_count: 0,
            ..Default::default()
        }
        .normalize();
        assert_eq!(settings.execution_mode, "serial");
        assert_eq!(settings.parallel_vm_count, 2);

        let settings = AppSettings {
            execution_mode: "parallel".into(),
            parallel_vm_count: 99,
            ..Default::default()
        }
        .normalize();
        assert_eq!(settings.execution_mode, "parallel");
        assert_eq!(settings.parallel_vm_count, 8);

        let settings = AppSettings {
            parallel_vm_count: 5,
            ..Default::default()
        }
        .normalize();
        assert_eq!(settings.parallel_vm_count, 5);
    }
}
