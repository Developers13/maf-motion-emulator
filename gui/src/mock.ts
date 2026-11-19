import type {
  Backend,
  Bootstrap,
  CountsEvent,
  LogEvent,
  RunStateEvent,
  ToastEvent,
} from "./api";
import type { AppSettings } from "./lib/settings";

/**
 * 浏览器预览用内存后端：不启动 Tauri、不碰真实文件与进程。
 * `pnpm dev` 打开 http://localhost:5173 即可看界面（用于设计截图与前端联调）。
 */

const settings: AppSettings = {
  PythonLauncher: "py",
  ProjectRoot: "",
  Environment: { PYTHONUTF8: "1" },
  MuMuManagerPath: "C:\\Program Files\\Netease\\MuMu\\nx_main\\MuMuManager.exe",
  TemplateVmName: "模板",
  ScriptHostPath: "",
  LastStudentId: "",
  SelectedStudents: [],
  WeekLimit: 5,
  TotalLimit: 32,
  SkipOnConfirmedCounts: true,
  AutoScroll: true,
};

const now = Date.now();
const iso = (offsetMinutes: number) => new Date(now - offsetMinutes * 60_000).toISOString();

const bootstrap: Bootstrap = {
  project_root: "C:\\Users\\Vincent\\Documents\\dsh-workspace\\maf-motion-emulator",
  settings,
  user_rows: [
    { student_id: "2025302319", password: "demo-pass", distance_km: 3.2 },
    { student_id: "2025301348", password: "demo-pass", distance_km: 3.2 },
    { student_id: "2025302323", password: "demo-pass", distance_km: 2.4 },
    { student_id: "2025301107", password: "demo-pass", distance_km: 3.2 },
    { student_id: "2025300891", password: "demo-pass", distance_km: null },
  ],
  statuses: {
    "2025302319": { Week: 0, Total: 33, WeekKey: weekKey(), ReadAt: iso(12), Confirmed: true, LastRunDay: "2026-11-14" },
    "2025301348": { Week: 5, Total: 21, WeekKey: weekKey(), ReadAt: iso(140), Confirmed: true, LastRunDay: "2026-11-14" },
    "2025302323": { Week: 2, Total: 30, WeekKey: weekKey(), ReadAt: iso(300), Confirmed: false, LastRunDay: dayKey() },
    "2025301107": { Week: 1, Total: 4, WeekKey: "2000-01-03", ReadAt: iso(12000), Confirmed: true, LastRunDay: "2000-01-03" },
  },
  script_hosts: [
    { path: "C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe", label: "Windows PowerShell 5.1" },
  ],
  is_elevated: true,
  version: "0.2.0",
};

function weekKey(): string {
  const date = new Date();
  const sinceMonday = (date.getDay() + 6) % 7;
  date.setDate(date.getDate() - sinceMonday);
  const pad = (n: number) => String(n).padStart(2, "0");
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}`;
}

function dayKey(): string {
  const date = new Date();
  const pad = (n: number) => String(n).padStart(2, "0");
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}`;
}

interface Handlers {
  log: (event: LogEvent) => void;
  runState: (event: RunStateEvent) => void;
  counts: (event: CountsEvent) => void;
  toast: (event: ToastEvent) => void;
}

const seedLines: Array<[string, boolean]> = [
  ["[mumu] READY index=0 port=16384 name=2025302319", false],
  ["[adb-prep] CONNECTED 127.0.0.1:16384", false],
  ["[adb-prep] MODEL MuMu 12", false],
  ["[adb-prep] MAGISK v27.2-kitsune-4", false],
  ["[adb-prep] LSPOSED LOADED", false],
  ["[e2e] === 2025302319 laps=8 target_km=3.3 adb=127.0.0.1:16384 ===", false],
  ["[e2e] COUNTS sid=2025302319 week=0 total=33", false],
  ["[e2e] SKIP sid=2025302319 reason=total week=0/5 total=33/32", false],
  ["=== Batch run (2/3): 2025302323 ===", false],
  ["[wait_exercise_finish] km=1.24/3.3 elapsed=420s", false],
  ["[wait_exercise_finish] km=2.87/3.3 elapsed=880s", false],
  ["[wait_exercise_finish] TARGET REACHED km=3.31 >= 3.3 elapsed=1010s", false],
  ["[batch] 2025302323 -> FAILED (exit code 2)", true],
  ["[mumu] WARN shutdown 2025302323 failed: exit code 1", false],
];

export function createMockBackend(): {
  backend: Backend;
  attach: (handlers: Handlers) => void;
  seed: () => void;
} {
  let handlers: Handlers | null = null;
  let timer: number | null = null;

  const emitLog = (line: string, isError = false) => {
    const time = new Date().toLocaleTimeString("zh-CN", { hour12: false });
    handlers?.log({ time, line, is_error: isError, source: "mock" });
  };

  const backend: Backend = {
    async bootstrap() {
      return structuredClone(bootstrap);
    },
    async saveSettings(next) {
      bootstrap.settings = structuredClone(next);
    },
    async saveUserRows(rows) {
      bootstrap.user_rows = structuredClone(rows);
    },
    async saveStudentCounts(student, week, total) {
      if (week === null || total === null) {
        delete bootstrap.statuses[student];
        return;
      }
      bootstrap.statuses[student] = {
        Week: week,
        Total: total,
        WeekKey: weekKey(),
        ReadAt: new Date().toISOString(),
        Confirmed: true,
        LastRunDay: "",
      };
    },
    async listScriptHosts() {
      return structuredClone(bootstrap.script_hosts);
    },
    async startRun(ids) {
      if (timer !== null) {
        return;
      }
      const total = ids.length;
      let index = 0;
      handlers?.runState({
        phase: total > 1 ? "batch" : "preparing",
        student: ids[0] ?? null,
        index: total > 1 ? 1 : null,
        total: total > 1 ? total : null,
        message: total > 1 ? `批量运行 ${ids[0]}` : "正在准备…",
        tone: "info",
      });
      emitLog(`=== Start run: ${ids[0]} ===`);

      timer = window.setInterval(() => {
        index += 1;
        if (index >= total) {
          if (timer !== null) {
            window.clearInterval(timer);
            timer = null;
          }
          handlers?.runState({ phase: "done", student: null, index: total, total, message: "完成", tone: "ok" });
          emitLog("=== 预览模式：模拟运行结束 ===");
          handlers?.toast({ tone: "ok", title: "预览运行完成", body: "这是浏览器预览的模拟结果。" });
          return;
        }
        handlers?.runState({
          phase: "batch",
          student: ids[index],
          index: index + 1,
          total,
          message: `批量运行 ${ids[index]}`,
          tone: "info",
        });
        emitLog(`=== Batch run (${index + 1}/${total}): ${ids[index]} ===`);
      }, 1600);
    },
    async stopRun() {
      if (timer !== null) {
        window.clearInterval(timer);
        timer = null;
      }
      handlers?.runState({ phase: "stopped", student: null, index: null, total: null, message: "已停止", tone: "neutral" });
      emitLog("=== Run stopped by user ===");
    },
    async hotReload() {
      return structuredClone(bootstrap);
    },
  };

  return {
    backend,
    attach(next) {
      handlers = next;
    },
    seed() {
      for (const [line, isError] of seedLines) {
        emitLog(line, isError);
      }
    },
  };
}
