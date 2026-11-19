use crate::settings::write_atomic;
use chrono::{Datelike, Duration, Local, Timelike};
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
    /// 次数是否经 App 实读确认：读数探针写入 true；成功后本地 +1 置 false
    /// （+1 是"待审核"估算，不能作为批量预跳过的依据）。旧文件缺字段按 false，
    /// 因此首次批量仍会开模拟器实读一次，之后才进入免读跳过。
    #[serde(rename = "Confirmed", default)]
    pub confirmed: bool,
    /// 最后一次成功运行（产生待审核记录）的本地日期 `yyyy-MM-dd`。
    /// App 的次数统计每日凌晨审核后才更新：当天的读数不包含当天的待审核记录，
    /// 所以"同日读数"不能确认估算、"隔日读数"才能。旧文件缺字段按空。
    #[serde(rename = "LastRunDay", default)]
    pub last_run_day: String,
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

/// 本地日期键 `yyyy-MM-dd`（与 WeekKey 不同：这是"哪一天"，不是"哪一周"）。
fn local_day_key(now: &chrono::DateTime<Local>) -> String {
    now.format("%Y-%m-%d").to_string()
}

/// 审核每日凌晨进行；跨日后该小时之前，前一天记录可能尚未入账。
/// 取保守值：宁可多保留一轮估算、等日间读数再确认，也不提前误确认。
const REVIEW_CUTOFF_HOUR: u32 = 6;

/// 上次运行产生的待审核记录是否已经过了当天的凌晨审核。
/// last_run_day 为空（从未跑过 / 旧数据）按"已过"处理，维持旧的确认路径。
fn review_has_passed_since(last_run_day: &str, now: &chrono::DateTime<Local>) -> bool {
    let Ok(run_date) = chrono::NaiveDate::parse_from_str(last_run_day, "%Y-%m-%d") else {
        return true;
    };
    let today = now.date_naive();
    if today <= run_date {
        return false; // 同日读数一律视为审核前（保守）
    }
    // 次日审核时刻之前：前一天记录可能还没入账
    !(today == run_date.succ_opt().unwrap_or(today) && now.hour() < REVIEW_CUTOFF_HOUR)
}

/// 写入一次 App 读数（覆盖本地缓存，含 +1 产生的估算值）。
///
/// 审核每日凌晨进行：若上次运行产生的待审核记录还没经过凌晨审核（同日，或
/// 次日清晨 06:00 前），读数必然不含这些记录——此时保留本地估算（取读数与
/// 估算的较大者）并维持待审核状态；审核已过的读数才视为已确认（Confirmed=true）。
pub fn set_from_read(map: &mut StatusMap, id: &str, week: u32, total: u32) {
    set_from_read_at(map, id, week, total, Local::now());
}

fn set_from_read_at(
    map: &mut StatusMap,
    id: &str,
    week: u32,
    total: u32,
    now: chrono::DateTime<Local>,
) {
    let prev = map.get(id).cloned();
    let reads_are_stale = prev
        .as_ref()
        .is_some_and(|e| !review_has_passed_since(&e.last_run_day, &now));
    let entry = if reads_are_stale {
        let p = prev.unwrap_or_default();
        let week_key = week_key_for(&now);
        // 跨周（如周一清晨）时上周的周次数不能带进新周，周次数以读数为准。
        let week = if p.week_key == week_key {
            p.week.max(week)
        } else {
            week
        };
        ExerciseStatus {
            week,
            total: p.total.max(total),
            week_key,
            read_at: now.to_rfc3339(),
            confirmed: false,
            last_run_day: p.last_run_day,
        }
    } else {
        ExerciseStatus {
            week,
            total,
            week_key: week_key_for(&now),
            read_at: now.to_rfc3339(),
            confirmed: true,
            last_run_day: prev.map(|e| e.last_run_day).unwrap_or_default(),
        }
    };
    map.insert(id.to_string(), entry);
}

/// 用户手动填写次数：视为已确认（参与批量预跳过），WeekKey 记为当前自然周。
///
/// 按用户选择「实读可覆盖」：清空 `last_run_day`，让下一次 App 实读按"读数可信"
/// 路径覆盖手动值，而不是被同日合并规则挡住。
pub fn set_manual(map: &mut StatusMap, id: &str, week: u32, total: u32) {
    let now = Local::now();
    map.insert(
        id.to_string(),
        ExerciseStatus {
            week,
            total,
            week_key: week_key_for(&now),
            read_at: now.to_rfc3339(),
            confirmed: true,
            last_run_day: String::new(),
        },
    );
}

/// 删除某学号的次数缓存（界面上清空两列即视为清除）。
pub fn clear(map: &mut StatusMap, id: &str) {
    map.remove(id);
}

/// 成功跑完一单后本地 +1：只做即时显示，审核过后的读数会覆盖。
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
    entry.last_run_day = local_day_key(&now);
    // +1 是本地估算（"待审核"）：实读确认前不能作为免读跳过的依据。
    entry.confirmed = false;
}

/// 达到任一上限：周上限要求 WeekKey 属于当前自然周；总次数单调递增、无周概念。
fn reaches_cap(
    entry: &ExerciseStatus,
    week_limit: u32,
    total_limit: u32,
    now: &chrono::DateTime<Local>,
) -> bool {
    (entry.week_key == week_key_for(now) && entry.week >= week_limit)
        || entry.total >= total_limit
}

/// 已确认（App 实读）且达到任一上限 → 批量可直接跳过、不开模拟器。
pub fn confirmed_at_cap(
    entry: &ExerciseStatus,
    week_limit: u32,
    total_limit: u32,
    now: &chrono::DateTime<Local>,
) -> bool {
    entry.confirmed && reaches_cap(entry, week_limit, total_limit, now)
}

/// 待审核估算达上限、且读数尚不可信（同日，或次日清晨审核时刻前）→ 批量
/// 直接跳过、不开模拟器：开也读不出真值，按估算跳过即可；过了审核时点再由
/// 探针实读确认。
pub fn pending_estimate_at_cap(
    entry: &ExerciseStatus,
    week_limit: u32,
    total_limit: u32,
    now: &chrono::DateTime<Local>,
) -> bool {
    !entry.confirmed
        && !review_has_passed_since(&entry.last_run_day, now)
        && reaches_cap(entry, week_limit, total_limit, now)
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
                confirmed: true,
                last_run_day: "2000-01-03".into(),
            },
        );
        increment_after_success(&mut map, "a");
        let entry = map.get("a").unwrap();
        assert_eq!(entry.week, 1);
        assert_eq!(entry.total, 21);
        assert_eq!(entry.week_key, week_key_for(&Local::now()));
        // +1 之后变成待审核估算，不能再作为免读跳过依据。
        assert!(!entry.confirmed);
        assert_eq!(entry.last_run_day, local_day_key(&Local::now()));
    }

    #[test]
    fn increment_same_week_adds_one() {
        let mut map = StatusMap::new();
        set_from_read(&mut map, "a", 3, 17);
        increment_after_success(&mut map, "a");
        let entry = map.get("a").unwrap();
        assert_eq!(entry.week, 4);
        assert_eq!(entry.total, 18);
        assert!(!entry.confirmed);
    }

    #[test]
    fn confirmed_flag_tracks_source_of_truth() {
        let mut map = StatusMap::new();
        set_from_read(&mut map, "a", 3, 17);
        assert!(map["a"].confirmed);
        increment_after_success(&mut map, "a");
        assert!(!map["a"].confirmed);
        assert_eq!(map["a"].week, 4);
    }

    #[test]
    fn confirmed_at_cap_requires_confirmation_and_current_week() {
        let now = Local.with_ymd_and_hms(2026, 11, 13, 12, 0, 0).unwrap(); // 周五
        let week_key = week_key_for(&now);

        // 估算值（+1 后）即使数值达上限也不能按"已确认"预跳过。
        let estimated = ExerciseStatus {
            week: 5,
            total: 40,
            week_key: week_key.clone(),
            read_at: String::new(),
            confirmed: false,
            last_run_day: String::new(),
        };
        assert!(!confirmed_at_cap(&estimated, 5, 32, &now));

        // 本周内实读确认的周上限 → 可跳过。
        let confirmed_week = ExerciseStatus {
            week: 5,
            total: 20,
            week_key,
            read_at: String::new(),
            confirmed: true,
            last_run_day: String::new(),
        };
        assert!(confirmed_at_cap(&confirmed_week, 5, 32, &now));

        // 周次数是旧自然周的 → 周上限不生效。
        let stale_week = ExerciseStatus {
            week: 5,
            total: 10,
            week_key: "2000-01-03".into(),
            read_at: String::new(),
            confirmed: true,
            last_run_day: String::new(),
        };
        assert!(!confirmed_at_cap(&stale_week, 5, 32, &now));

        // 总次数无自然周概念，实读确认即可跳过。
        let confirmed_total = ExerciseStatus {
            week: 0,
            total: 32,
            week_key: "2000-01-03".into(),
            read_at: String::new(),
            confirmed: true,
            last_run_day: String::new(),
        };
        assert!(confirmed_at_cap(&confirmed_total, 5, 32, &now));
    }

    #[test]
    fn set_from_read_same_day_keeps_pending_estimate() {
        let mut map = StatusMap::new();
        map.insert(
            "a".into(),
            ExerciseStatus {
                week: 4,
                total: 31,
                week_key: week_key_for(&Local::now()),
                read_at: String::new(),
                confirmed: false,
                last_run_day: local_day_key(&Local::now()),
            },
        );
        // 当天 App 读数滞后（不含当天待审核记录）：保留估算 31，不升级为已确认。
        set_from_read(&mut map, "a", 3, 30);
        let entry = map.get("a").unwrap();
        assert_eq!((entry.week, entry.total), (4, 31));
        assert!(!entry.confirmed);
        assert_eq!(entry.last_run_day, local_day_key(&Local::now()));
    }

    #[test]
    fn set_from_read_next_day_confirms_the_read() {
        let mut map = StatusMap::new();
        map.insert(
            "a".into(),
            ExerciseStatus {
                week: 4,
                total: 31,
                week_key: week_key_for(&Local::now()),
                read_at: String::new(),
                confirmed: false,
                last_run_day: "2000-01-01".into(),
            },
        );
        set_from_read(&mut map, "a", 4, 32);
        let entry = map.get("a").unwrap();
        assert_eq!((entry.week, entry.total), (4, 32));
        assert!(entry.confirmed);
        // last_run_day 保留：后续同日判断仍需要。
        assert_eq!(entry.last_run_day, "2000-01-01");
    }

    #[test]
    fn set_manual_is_confirmed_and_readable_override() {
        let mut map = StatusMap::new();
        set_manual(&mut map, "a", 5, 31);
        let entry = map.get("a").unwrap();
        assert_eq!((entry.week, entry.total), (5, 31));
        assert!(entry.confirmed);
        assert_eq!(entry.week_key, week_key_for(&Local::now()));
        // 手动值不设 LastRunDay：下一次实读按可信读数覆盖。
        assert!(entry.last_run_day.is_empty());
        // 手动周次数达上限 → 批量可预跳过。
        assert!(confirmed_at_cap(entry, 5, 32, &Local::now()));

        set_manual(&mut map, "a", 4, 31);
        assert!(!confirmed_at_cap(map.get("a").unwrap(), 5, 32, &Local::now()));
    }

    #[test]
    fn clear_removes_entry() {
        let mut map = StatusMap::new();
        set_manual(&mut map, "a", 1, 2);
        clear(&mut map, "a");
        assert!(map.get("a").is_none());
    }

    #[test]
    fn pending_estimate_at_cap_covers_overnight_predawn_window() {
        // 2026-11-15（周日）跑的，11-16（周一）凌晨批量
        let mon_0559 = Local.with_ymd_and_hms(2026, 11, 16, 5, 59, 0).unwrap();
        let mon_0600 = Local.with_ymd_and_hms(2026, 11, 16, 6, 0, 0).unwrap();
        let entry = ExerciseStatus {
            week: 5,
            total: 32,
            week_key: "2026-11-09".into(),
            read_at: String::new(),
            confirmed: false,
            last_run_day: "2026-11-15".into(),
        };
        // 次日 06:00 前：读数仍不含前一天待审核记录 → 按估算跳过
        assert!(pending_estimate_at_cap(&entry, 5, 32, &mon_0559));
        // 次日 06:00 起：读数可信 → 应走"开模拟器实读确认"，不再按估算跳过
        assert!(!pending_estimate_at_cap(&entry, 5, 32, &mon_0600));

        // 当天刚跑过：整个白天都按估算跳过
        let day = Local.with_ymd_and_hms(2026, 11, 15, 21, 0, 0).unwrap();
        let mut today_entry = entry.clone();
        today_entry.last_run_day = "2026-11-15".into();
        assert!(pending_estimate_at_cap(&today_entry, 5, 32, &day));

        // 隔两日（哪怕凌晨）→ 读数已可信，应实读确认
        let wed_0030 = Local.with_ymd_and_hms(2026, 11, 17, 0, 30, 0).unwrap();
        assert!(!pending_estimate_at_cap(&entry, 5, 32, &wed_0030));

        // 已确认的走 confirmed_at_cap，不属于本规则
        let mut confirmed = entry.clone();
        confirmed.confirmed = true;
        assert!(!pending_estimate_at_cap(&confirmed, 5, 32, &mon_0559));
    }

    #[test]
    fn review_has_passed_since_respects_predawn_cutoff() {
        let sun_night = Local.with_ymd_and_hms(2026, 11, 15, 23, 50, 0).unwrap();
        let mon_0559 = Local.with_ymd_and_hms(2026, 11, 16, 5, 59, 0).unwrap();
        let mon_0600 = Local.with_ymd_and_hms(2026, 11, 16, 6, 0, 0).unwrap();
        let tue_0030 = Local.with_ymd_and_hms(2026, 11, 17, 0, 30, 0).unwrap();

        assert!(!review_has_passed_since("2026-11-15", &sun_night)); // 同日
        assert!(!review_has_passed_since("2026-11-15", &mon_0559)); // 次日清晨
        assert!(review_has_passed_since("2026-11-15", &mon_0600)); // 次日 06:00 起
        assert!(review_has_passed_since("2026-11-15", &tue_0030)); // 隔两日（含凌晨）
        assert!(review_has_passed_since("", &mon_0600)); // 无锚点按已过
        assert!(review_has_passed_since("bad-date", &mon_0600)); // 解析失败按已过
    }

    #[test]
    fn set_from_read_predawn_monday_does_not_carry_last_week() {
        // 周日 23:50 跑完（week=5 属上周）；周一 00:30 读数 week=0（新周）。
        // 修复前：max(5, 0)=5 + 新周 WeekKey → 上周次数污染新周。
        let mon_0030 = Local.with_ymd_and_hms(2026, 11, 16, 0, 30, 0).unwrap();
        let mut map = StatusMap::new();
        map.insert(
            "a".into(),
            ExerciseStatus {
                week: 5,
                total: 31,
                week_key: "2026-11-09".into(),
                read_at: String::new(),
                confirmed: false,
                last_run_day: "2026-11-15".into(),
            },
        );
        set_from_read_at(&mut map, "a", 0, 31, mon_0030);
        let entry = map.get("a").unwrap();
        assert_eq!(entry.week, 0); // 新周以读数为准，不带上周的 5
        assert_eq!(entry.total, 31); // 总次数保留估算
        assert_eq!(entry.week_key, "2026-11-16");
        assert!(!entry.confirmed);
        assert_eq!(entry.last_run_day, "2026-11-15");
    }

    #[test]
    fn set_from_read_predawn_same_week_keeps_estimate() {
        // 周内同日/次日清晨：读数滞后，周、总都保留估算（取较大值）。
        let mon_0030 = Local.with_ymd_and_hms(2026, 11, 16, 0, 30, 0).unwrap();
        let mut map = StatusMap::new();
        map.insert(
            "a".into(),
            ExerciseStatus {
                week: 4,
                total: 31,
                week_key: "2026-11-16".into(),
                read_at: String::new(),
                confirmed: false,
                last_run_day: "2026-11-16".into(),
            },
        );
        set_from_read_at(&mut map, "a", 3, 30, mon_0030);
        let entry = map.get("a").unwrap();
        assert_eq!((entry.week, entry.total), (4, 31));
        assert!(!entry.confirmed);
    }

    #[test]
    fn set_from_read_after_cutoff_confirms_the_read() {
        // 审核时点已过（06:30）：读数可信 → 确认，并保留 last_run_day。
        let mon_0630 = Local.with_ymd_and_hms(2026, 11, 16, 6, 30, 0).unwrap();
        let mut map = StatusMap::new();
        map.insert(
            "a".into(),
            ExerciseStatus {
                week: 4,
                total: 31,
                week_key: "2026-11-09".into(),
                read_at: String::new(),
                confirmed: false,
                last_run_day: "2026-11-15".into(),
            },
        );
        set_from_read_at(&mut map, "a", 4, 32, mon_0630);
        let entry = map.get("a").unwrap();
        assert_eq!((entry.week, entry.total), (4, 32));
        assert!(entry.confirmed);
        assert_eq!(entry.last_run_day, "2026-11-15");
    }
}
