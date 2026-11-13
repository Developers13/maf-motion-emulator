/**
 * 用户列表（user-list.json）的解析、序列化与单行归一化。
 *
 * 移植旧 C# GUI 的学号表：学号、密码、距离。序列化字段顺序为
 * student_id / password / distance_km，距离为 null 时省略该字段，
 * 2 空格缩进、无 BOM。
 */

export interface UserRow {
  student_id: string;
  password: string;
  distance_km: number | null;
}

// IEEE754 下 |3.199 - 3.2| 略大于 0.001（约 0.0010000000000003），
// 这里加 1e-9 的浮点余量，保证 2.4 / 3.2 附近的输入按“容差 0.001”通过。
const DIST_TOL = 0.001 + 1e-9;

export function distanceIsValid(text: string): boolean {
  const t = (text ?? "").trim();
  if (t === "") {
    return false;
  }
  const n = Number(t);
  if (!Number.isFinite(n)) {
    return false;
  }
  return Math.abs(n - 2.4) <= DIST_TOL || Math.abs(n - 3.2) <= DIST_TOL;
}

/**
 * 从 JSON 数组文本恢复用户列表。字段缺失时宽容处理：
 * student_id / password 缺失或非字符串 → ""；
 * distance_km 缺失 / null → null；数值则原样保留。
 * 顶层不是数组时抛 Error。
 */
export function parseUserList(json: string): UserRow[] {
  let raw: unknown;
  try {
    raw = JSON.parse(json);
  } catch {
    throw new Error("user-list.json 不是合法 JSON");
  }
  if (!Array.isArray(raw)) {
    throw new Error("user-list.json 必须是 JSON 数组");
  }
  return raw.map((item) => {
    const obj =
      item && typeof item === "object" && !Array.isArray(item)
        ? (item as Record<string, unknown>)
        : {};
    const student_id = typeof obj.student_id === "string" ? obj.student_id : "";
    const password = typeof obj.password === "string" ? obj.password : "";
    const d = obj.distance_km;
    const distance_km: number | null =
      typeof d === "number" && Number.isFinite(d) ? d : null;
    return { student_id, password, distance_km };
  });
}

export function serializeUserList(rows: UserRow[]): string {
  const ordered: Record<string, unknown>[] = rows.map((row) => {
    const obj: Record<string, unknown> = {
      student_id: row.student_id,
      password: row.password,
    };
    if (row.distance_km !== null) {
      obj["distance_km"] = row.distance_km;
    }
    return obj;
  });
  return JSON.stringify(ordered, null, 2);
}

/**
 * 归一化单个输入行（设置面板的学号/密码/距离）。
 *
 * - 整行全空（学号、密码、距离均无内容）→ {row:null,error:null}，保存时丢弃该占位行。
 * - 学号为空但其它字段有内容 → 报错“学号不能为空”。
 * - 学号非空时：距离为空 → distance_km=null；否则距离须在 0.001 容差内等于 2.4 或 3.2，
 *   否则报错“距离只能填 2.4 或 3.2”。
 * - 学号结果取 trim；密码保持原样（不 trim）。
 */
export function normalizeRow(
  studentId: string,
  password: string,
  distanceText: string,
): { row: UserRow | null; error: string | null } {
  const sid = (studentId ?? "").trim();
  const dist = (distanceText ?? "").trim();
  const pwd = (password ?? "").trim();

  if (sid === "" && pwd === "" && dist === "") {
    return { row: null, error: null };
  }
  if (sid === "") {
    return { row: null, error: "学号不能为空" };
  }
  let distance_km: number | null;
  if (dist === "") {
    distance_km = null;
  } else {
    const n = Number(dist);
    if (!Number.isFinite(n) || !(Math.abs(n - 2.4) <= DIST_TOL || Math.abs(n - 3.2) <= DIST_TOL)) {
      return { row: null, error: "距离只能填 2.4 或 3.2" };
    }
    distance_km = n;
  }
  return {
    row: { student_id: sid, password, distance_km },
    error: null,
  };
}
