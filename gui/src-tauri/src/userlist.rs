use crate::settings::write_atomic;
use serde::{Deserialize, Serialize};
use std::fs;
use std::io;
use std::path::Path;

/// 与根目录 `user-list.json` 同构（snake_case）。密码明文落盘是既有设计，
/// 只允许存在于这个文件里，绝不进入设置、状态缓存或日志。
#[derive(Serialize, Deserialize, Clone, Debug, Default)]
pub struct UserRow {
    #[serde(default)]
    pub student_id: String,
    #[serde(default)]
    pub password: String,
    /// 旧 GUI 写回时 null 会省略该键，脚本侧按 3.2 处理。
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub distance_km: Option<f64>,
}

pub fn load(path: &Path) -> io::Result<Vec<UserRow>> {
    let text = fs::read_to_string(path)?;
    serde_json::from_str(&text).map_err(|e| io::Error::new(io::ErrorKind::InvalidData, e))
}

pub fn save(path: &Path, rows: &[UserRow]) -> io::Result<()> {
    let json = serde_json::to_string_pretty(rows)?;
    write_atomic(path, json.as_bytes())
}
