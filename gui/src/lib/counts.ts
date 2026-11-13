/**
 * 后端计数 / 跳过行的解析。
 *
 * 对应后端发来的 `[e2e] COUNTS` / `[e2e] SKIP` 日志行。格式固定为：
 *   [e2e] COUNTS sid=<非空格> week=<int> total=<int>
 *   [e2e] SKIP sid=<非空格>
 * 行首空白允许；解析失败、周/总次数为负都返回 null。
 */

export interface CountsEvent {
  sid: string;
  week: number;
  total: number;
}

const COUNTS_RE =
  /^\s*\[e2e\]\s+COUNTS\s+sid=(\S+)\s+week=(-?\d+)\s+total=(-?\d+)\s*$/;

const SKIP_RE = /^\s*\[e2e\]\s+SKIP\s+sid=(\S+)\s*$/;

/**
 * 解析一行计数。匹配失败、或 week / total 为负 → null；否则返回事件。
 */
export function parseCountsLine(line: string): CountsEvent | null {
  const m = COUNTS_RE.exec(line ?? "");
  if (!m) {
    return null;
  }
  const week = Number(m[2]);
  const total = Number(m[3]);
  if (week < 0 || total < 0) {
    return null;
  }
  return { sid: m[1], week, total };
}

/**
 * 解析一行跳过。匹配成功返回 sid，否则 null。
 */
export function parseSkipLine(line: string): string | null {
  const m = SKIP_RE.exec(line ?? "");
  if (!m) {
    return null;
  }
  return m[1];
}
