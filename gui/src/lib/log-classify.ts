/**
 * 日志行分类：把后端 / 本地打印的一行文本映射成日志着色类型。
 *
 * 移植旧 C# GUI 的日志着色规则，优先级从高到低：
 *   1. 以 "===" 开头            → banner（分隔条）
 *   2. 含 "-> OK"              → ok（成功）
 *   3. 含 "FAILED"、isError、
 *      含 "[mumu] ERROR" 或 "[adb-prep] FAIL"  → error
 *   4. 以 "[" 开头             → info（带前缀的普通信息）
 *   5. 其余                    → default
 */

export type LogKind = "banner" | "ok" | "error" | "info" | "default";

export function classifyLogLine(line: string, isError = false): LogKind {
  const l = line ?? "";
  if (l.startsWith("===")) {
    return "banner";
  }
  if (l.includes("-> OK")) {
    return "ok";
  }
  if (
    l.includes("FAILED") ||
    isError ||
    l.includes("[mumu] ERROR") ||
    l.includes("[adb-prep] FAIL")
  ) {
    return "error";
  }
  if (l.startsWith("[")) {
    return "info";
  }
  return "default";
}
