import { invoke } from "@tauri-apps/api/core";
import type { AppSettings } from "./lib/settings";
import type { UserRow } from "./lib/userlist";
import type { ExerciseStatusMap } from "./lib/status";

export interface ScriptHost {
  path: string;
  label: string;
}

/** Rust `Bootstrap` 的序列化结构（字段为 snake_case）。 */
export interface Bootstrap {
  project_root: string;
  settings: AppSettings;
  user_rows: UserRow[];
  statuses: ExerciseStatusMap;
  script_hosts: ScriptHost[];
  is_elevated: boolean;
  version: string;
}

export interface LogEvent {
  time: string;
  line: string;
  is_error: boolean;
  source: string;
}

export interface RunStateEvent {
  phase: string;
  student: string | null;
  index: number | null;
  total: number | null;
  message: string;
  tone: string;
}

export interface CountsEvent {
  student: string;
  week: number;
  total: number;
  week_key: string;
  read_at: string;
  /** true = App 实读确认；false = 本地 +1 估算（待审核）。 */
  confirmed: boolean;
}

export interface ToastEvent {
  tone: string;
  title: string;
  body: string;
}

export interface Backend {
  bootstrap(): Promise<Bootstrap>;
  saveSettings(settings: AppSettings): Promise<void>;
  saveUserRows(rows: UserRow[]): Promise<void>;
  /** 手动填写次数：week/total 都为 null = 清除该学号缓存。 */
  saveStudentCounts(
    student: string,
    week: number | null,
    total: number | null,
  ): Promise<void>;
  listScriptHosts(): Promise<ScriptHost[]>;
  startRun(ids: string[]): Promise<void>;
  stopRun(): Promise<void>;
  hotReload(): Promise<Bootstrap>;
}

const tauriBackend: Backend = {
  bootstrap: () => invoke<Bootstrap>("bootstrap"),
  saveSettings: (settings) => invoke<void>("save_settings", { settings }),
  saveUserRows: (rows) => invoke<void>("save_user_rows", { rows }),
  saveStudentCounts: (student, week, total) =>
    invoke<void>("save_student_counts", { student, week, total }),
  listScriptHosts: () => invoke<ScriptHost[]>("list_script_hosts"),
  startRun: (ids) => invoke<void>("start_run", { ids }),
  stopRun: () => invoke<void>("stop_run"),
  hotReload: () => invoke<Bootstrap>("hot_reload"),
};

/**
 * 当前后端。浏览器里预览设计时由 `mock.ts` 替换为内存实现；
 * 导出为 let 以便 ESM 实时绑定，其他模块始终 import { api } 即可。
 */
export let api: Backend = tauriBackend;

export function useBackend(backend: Backend): void {
  api = backend;
}

export function runningInTauri(): boolean {
  return typeof window !== "undefined" && "__TAURI_INTERNALS__" in window;
}
