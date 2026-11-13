use crate::settings::write_atomic;
use chrono::{Datelike, Duration, Local};
use serde::{Deserialize, Serialize};
use std::collections::BTreeMap;
use std::fs;
use std::io;
use std::path::Path;

/// 某学号的运动次数读数（来源：App 页面 OCR；不含任何凭证）。
#[derive(Serialize, Deserialize, Clone, Debug, Default)]
pub struct ExerciseStatus {
    #[serde(rename = "Week", default)]
    pub week: u32,
    #[serde(rename = "Total", default)]
    pub total: u32,
    /// 本周一的本地日期 `yyyy-MM-dd`；跨周后周次数视为未知。
    #[serde(rename = "WeekKey", default)]
    pub week_key: String,
    #[serde(rename = "ReadAt", default)]
    pub read_at: String,
}

pub type StatusMap = BTreeMap<String, ExerciseStatus>;

/// 自然周键：周一 00:00 本地时间为一周起点（与旧 ExerciseStatusStore 一致）。
pub fn week_key_for(now: &chrono::DateTime<Local>) -> String {
    let days_since_monday = now.weekday().num_days_from_monday() as i64;
    (now.date_naive() - Duration::days(days_since_monday))
        .format("%Y-%m-%d")
        .to_string()
}

pub fn load(path: &Path) -> StatusMap {
    let Ok(text) = fs::read_to_string(path) else {
        return StatusMap::new();
    };
    serde_json::from_str(&text).unwrap_or_default()
}

pub fn save(path: &Path, map: &StatusMap) -> io::Result<()> {
    let json = serde_json::to_string_pretty(map)?;
    write_atomic(path, json.as_bytes())
}

/// 写入一次 App 读数（覆盖本地缓存，含 +1 产生的估算值）。
pub fn set_from_read(map: &mut StatusMap, id: &str, week: u32, total: u32) {
    let now = Local::now();
    map.insert(
        id.to_string(),
        ExerciseStatus {
            week,
            total,
            week_key: week_key_for(&now),
            read_at: now.to_rfc3339(),
        },
    );
}

/// 成功跑完一单后本地 +1：只做即时显示，下次跑前读数会覆盖。
pub fn increment_after_success(map: &mut StatusMap, id: &str) {
    let now = Local::now();
    let key = week_key_for(&now);
    let entry = map.entry(id.to_string()).or_default();
    if entry.week_key != key {
        entry.week = 0;
        entry.week_key = key;
    }
    entry.week += 1;
    entry.total += 1;
    entry.read_at = now.to_rfc3339();
}

#[cfg(test)]
mod tests {
    use super::*;
    use chrono::TimeZone;

    #[test]
    fn week_key_is_monday() {
        let friday = Local.with_ymd_and_hms(2026, 11, 13, 12, 0, 0).unwrap();
        assert_eq!(week_key_for(&friday), "2026-11-09");

        let monday = Local.with_ymd_and_hms(2026, 11, 9, 0, 0, 0).unwrap();
        assert_eq!(week_key_for(&monday), "2026-11-09");

        let sunday = Local.with_ymd_and_hms(2026, 11, 15, 23, 59, 0).unwrap();
        assert_eq!(week_key_for(&sunday), "2026-11-09");
    }

    #[test]
    fn increment_cross_week_resets_week_but_not_total() {
        let mut map = StatusMap::new();
        map.insert(
            "a".into(),
            ExerciseStatus {
                week: 5,
                total: 20,
                week_key: "2000-01-03".into(),
                read_at: String::new(),
            },
        );
        increment_after_success(&mut map, "a");
        let entry = map.get("a").unwrap();
        assert_eq!(entry.week, 1);
        assert_eq!(entry.total, 21);
        assert_eq!(entry.week_key, week_key_for(&Local::now()));
    }

    #[test]
    fn increment_same_week_adds_one() {
        let mut map = StatusMap::new();
        set_from_read(&mut map, "a", 3, 17);
        increment_after_success(&mut map, "a");
        let entry = map.get("a").unwrap();
        assert_eq!(entry.week, 4);
        assert_eq!(entry.total, 18);
    }
}
