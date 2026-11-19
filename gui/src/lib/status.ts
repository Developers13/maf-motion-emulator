/**
 * 锻炼进度（exercise-status.json）的读写与按次累加。
 *
 * 键为学号，值为 ExerciseStatus{Week,Total,WeekKey,ReadAt}（PascalCase，
 * 与旧 GUI / gui-settings.json 系列保持一致）。WeekKey 是「本地时间、周一为
 * 周首」的 YYYY-MM-DD；跨自然周时本周计数归零重计。
 */

export interface ExerciseStatus {
  Week: number;
  Total: number;
  WeekKey: string;
  ReadAt: string;
  /**
   * 次数是否经 App 实读确认：探针读数=true，成功后本地 +1=false（"待审核"
   * 估算）。只有 Confirmed=true 的达上限状态才能触发批量预跳过。
   */
  Confirmed: boolean;
  /**
   * 最后一次成功运行的本地日期 yyyy-MM-dd（审核每日凌晨进行，当天读数不含
   * 当天待审核记录）。仅后端写入与读数合并时使用；COUNTS 事件不带此字段，
   * 故为可选。
   */
  LastRunDay?: string;
}

export type ExerciseStatusMap = Record<string, ExerciseStatus>;

function pad(n: number): string {
  return String(n).padStart(2, "0");
}

/**
 * 本地时间取「周一为周首」的那一周的周一日期，格式化为 YYYY-MM-DD。
 * 使用本地分量（getFullYear/getMonth/getDate），故 WeekKey 随系统时区变化。
 */
export function weekKeyFor(now: Date): string {
  const d = new Date(now.getTime());
  const dayOfWeek = d.getDay(); // 0=周日 … 6=周六
  const daysFromMonday = (dayOfWeek + 6) % 7;
  const monday = new Date(
    d.getFullYear(),
    d.getMonth(),
    d.getDate() - daysFromMonday,
  );
  const y = monday.getFullYear();
  const m = pad(monday.getMonth() + 1);
  const dd = pad(monday.getDate());
  return `${y}-${m}-${dd}`;
}

/** 本地日期键 yyyy-MM-dd（"哪一天"，与 WeekKey 的"哪一周"相对）。 */
export function dayKeyFor(now: Date): string {
  const d = new Date(now.getTime());
  const pad = (n: number) => String(n).padStart(2, "0");
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
}

/** 某状态是否为「当前自然周」（默认以系统当前时间为参照）。 */
export function weekIsCurrent(status: ExerciseStatus, now?: Date): boolean {
  return status.WeekKey === weekKeyFor(now ?? new Date());
}

/**
 * 已确认（App 实读）且达到任一上限 → 批量可直接跳过、不开模拟器。
 * 与后端 status.rs 的 confirmed_at_cap 同一套规则：
 * - 必须Confirmed=true（+1 估算不算）；
 * - 周上限要求 WeekKey 属于当前自然周；
 * - 总次数单调递增，无自然周限制。
 */
export function isConfirmedAtCap(
  status: ExerciseStatus,
  weekLimit: number,
  totalLimit: number,
  now?: Date,
): boolean {
  if (!status.Confirmed) {
    return false;
  }
  return (
    (weekIsCurrent(status, now) && status.Week >= weekLimit) ||
    status.Total >= totalLimit
  );
}

/**
 * 由后端读数（week/total）重建状态：值取 max(0,·)，WeekKey 用当前周、
 * ReadAt 用当前时间的 ISO 串；返回新对象，不改入参。
 */
export function setFromRead(
  week: number,
  total: number,
  now?: Date,
): ExerciseStatus {
  const d = now ?? new Date();
  return {
    Week: Math.max(0, week),
    Total: Math.max(0, total),
    WeekKey: weekKeyFor(d),
    ReadAt: d.toISOString(),
    Confirmed: true,
    // TS 层只做镜像展示；同日/隔日合并由后端 set_from_read 决定。
    LastRunDay: "",
  };
}

/**
 * 一次成功练习后累加该学号计数（不可变：返回新 map）。
 *
 * - 学号无记录 → 新建 {Week:1,Total:1}。
 * - 已有且跨周（WeekKey≠当前周）→ Week 归零、WeekKey 更新为当前周，
 *   再 Week+1 / Total+1。
 * - 同周 → Week+1 / Total+1，WeekKey 用当前周。
 * - ReadAt 总是刷为当前时间 ISO。
 */
export function incrementAfterSuccess(
  map: ExerciseStatusMap,
  sid: string,
  now?: Date,
): ExerciseStatusMap {
  const d = now ?? new Date();
  const currentKey = weekKeyFor(d);
  const nextMap: ExerciseStatusMap = { ...map };
  const prev = nextMap[sid];
  if (!prev) {
    nextMap[sid] = {
      Week: 1,
      Total: 1,
      WeekKey: currentKey,
      ReadAt: d.toISOString(),
      Confirmed: false,
      LastRunDay: dayKeyFor(d),
    };
  } else {
    const weekBase = prev.WeekKey === currentKey ? prev.Week : 0;
    nextMap[sid] = {
      Week: weekBase + 1,
      Total: prev.Total + 1,
      WeekKey: currentKey,
      ReadAt: d.toISOString(),
      // +1 估算置为待审核：隔日实读确认后才能免读跳过。
      Confirmed: false,
      LastRunDay: dayKeyFor(d),
    };
  }
  return nextMap;
}

export function parseStatusMap(json: string): ExerciseStatusMap {
  let raw: unknown;
  try {
    raw = JSON.parse(json);
  } catch {
    throw new Error("exercise-status.json 不是合法 JSON");
  }
  if (!raw || typeof raw !== "object" || Array.isArray(raw)) {
    throw new Error("exercise-status.json 必须是 JSON 对象");
  }
  const out: ExerciseStatusMap = {};
  for (const key of Object.keys(raw as Record<string, unknown>)) {
    out[key] = toStatusEntry((raw as Record<string, unknown>)[key]);
  }
  return out;
}

function toStatusEntry(value: unknown): ExerciseStatus {
  const obj =
    value && typeof value === "object" && !Array.isArray(value)
      ? (value as Record<string, unknown>)
      : {};
  return {
    Week: typeof obj.Week === "number" && Number.isFinite(obj.Week) ? obj.Week : 0,
    Total:
      typeof obj.Total === "number" && Number.isFinite(obj.Total) ? obj.Total : 0,
    WeekKey: typeof obj.WeekKey === "string" ? obj.WeekKey : "",
    ReadAt: typeof obj.ReadAt === "string" ? obj.ReadAt : "",
    // 旧文件缺字段按 false：首次批量仍实读确认一次，之后才免读跳过。
    Confirmed: obj.Confirmed === true,
    LastRunDay: typeof obj.LastRunDay === "string" ? obj.LastRunDay : "",
  };
}

export function serializeStatusMap(map: ExerciseStatusMap): string {
  return JSON.stringify(map, null, 2);
}
