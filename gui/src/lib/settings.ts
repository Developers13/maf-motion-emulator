/**
 * 应用设置的定义、兜底解析与序列化。
 *
 * 移植旧 C# GUI 的 AppSettings：字段名保持 PascalCase，序列化格式与
 * gui-settings.json 一致（2 空格缩进、无 BOM）。`normalizeSettings` 用于
 * 恢复历史版本 / 手写 JSON 中残缺的字段，保证下游拿到的对象永远完整。
 */

export interface AppSettings {
  PythonLauncher: string;
  ProjectRoot: string;
  Environment: Record<string, string>;
  MuMuManagerPath: string;
  TemplateVmName: string;
  ScriptHostPath: string;
  LastStudentId: string;
  SelectedStudents: string[];
  WeekLimit: number;
  TotalLimit: number;
  AutoScroll: boolean;
}

export const DEFAULT_SETTINGS: AppSettings = {
  PythonLauncher: "py",
  ProjectRoot: "",
  Environment: {
    PYTHONUTF8: "1",
  },
  MuMuManagerPath:
    "C:\\Program Files\\Netease\\MuMu\\nx_main\\MuMuManager.exe",
  TemplateVmName: "模板",
  ScriptHostPath: "",
  LastStudentId: "",
  SelectedStudents: [],
  WeekLimit: 5,
  TotalLimit: 32,
  AutoScroll: true,
};

// 一个"有效"的非空字符串：必须是 string 且去掉两端空白后不是空。
// 空白/null 的字段会回退默认值（见 normalizeSettings）。
function isNonEmptyString(value: unknown): value is string {
  return typeof value === "string" && value.trim() !== "";
}

// 字符串路径字段：只有 null（或根本不是字符串）才回退空串，
// 空白文本按用户输入保留，不改成默认值。
function fallbackEmptyString(value: unknown): string {
  return typeof value === "string" ? value : "";
}

// 正数限制值：<= 0 / NaN / Infinity / 非数都视为缺失。
function positiveLimit(value: unknown, fallback: number): number {
  return (
    typeof value === "number" &&
    Number.isFinite(value) &&
    value > 0
  )
    ? value
    : fallback;
}

export function normalizeSettings(raw: unknown): AppSettings {
  // raw 来自 JSON.parse；非对象（含数组）一律按完全缺失处理，
  // 未知字段自然丢弃。
  const r =
    raw && typeof raw === "object" && !Array.isArray(raw)
      ? (raw as Record<string, unknown>)
      : {};

  const env = r.Environment;
  const environment: Record<string, string> =
    env && typeof env === "object" && !Array.isArray(env)
      ? Object.keys(env as Record<string, unknown>).reduce(
          (acc, key) => {
            // 值强制 string：JSON 里环境值可能是数字（如端口），
            // 运行时会拼进进程环境变量，必须是字符串。
            const rawValue = (env as Record<string, unknown>)[key];
            acc[key] =
              typeof rawValue === "string" ? rawValue : String(rawValue);
            return acc;
          },
          {} as Record<string, string>,
        )
      : { PYTHONUTF8: "1" };

  const scrollValue = r["AutoScroll"];
  return {
    PythonLauncher: isNonEmptyString(r.PythonLauncher)
      ? r.PythonLauncher
      : DEFAULT_SETTINGS.PythonLauncher,
    ProjectRoot: fallbackEmptyString(r.ProjectRoot),
    Environment: environment,
    MuMuManagerPath: isNonEmptyString(r.MuMuManagerPath)
      ? r.MuMuManagerPath
      : DEFAULT_SETTINGS.MuMuManagerPath,
    TemplateVmName: isNonEmptyString(r.TemplateVmName)
      ? r.TemplateVmName
      : DEFAULT_SETTINGS.TemplateVmName,
    ScriptHostPath: fallbackEmptyString(r.ScriptHostPath),
    LastStudentId: isNonEmptyString(r.LastStudentId)
      ? r.LastStudentId
      : DEFAULT_SETTINGS.LastStudentId,
    SelectedStudents: Array.isArray(r.SelectedStudents)
      ? (r.SelectedStudents as unknown[])
          .map((v) => (typeof v === "string" ? v : String(v)))
          .filter((v) => v !== "")
      : [],
    WeekLimit: positiveLimit(r.WeekLimit, DEFAULT_SETTINGS.WeekLimit),
    TotalLimit: positiveLimit(r.TotalLimit, DEFAULT_SETTINGS.TotalLimit),
    AutoScroll:
      typeof scrollValue === "boolean" ? scrollValue : true,
  };
}

export function serializeSettings(s: AppSettings): string {
  // 按接口顺序显式重建对象：JSON.stringify 只保留插入序，
  // 这样无论传入对象如何构造，输出文件中的字段顺序都稳定。
  const ordered: Record<string, unknown> = {
    PythonLauncher: s.PythonLauncher,
    ProjectRoot: s.ProjectRoot,
    Environment: s.Environment,
    MuMuManagerPath: s.MuMuManagerPath,
    TemplateVmName: s.TemplateVmName,
    ScriptHostPath: s.ScriptHostPath,
    LastStudentId: s.LastStudentId,
    SelectedStudents: s.SelectedStudents,
    WeekLimit: s.WeekLimit,
    TotalLimit: s.TotalLimit,
  };
  const lastKey = "AutoScroll";
  ordered[lastKey] = s[lastKey];
  return JSON.stringify(ordered, null, 2);
}
