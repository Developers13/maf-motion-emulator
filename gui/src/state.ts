import type { AppSettings } from "./lib/settings";
import type { UserRow } from "./lib/userlist";
import type { ExerciseStatusMap } from "./lib/status";
import type { ScriptHost } from "./api";

export interface RunView {
  phase: string;
  student: string | null;
  index: number | null;
  total: number | null;
  message: string;
  tone: string;
}

export const BUSY_PHASES = new Set(["preparing", "running", "batch", "stopping"]);

/** 全局界面状态：后端是唯一权威，这里只保存用于渲染的副本。 */
export const state = {
  projectRoot: "",
  settings: null as AppSettings | null,
  rows: [] as UserRow[],
  statuses: {} as ExerciseStatusMap,
  scriptHosts: [] as ScriptHost[],
  isElevated: false,
  version: "",
  selected: new Set<string>(),
  run: {
    phase: "idle",
    student: null,
    index: null,
    total: null,
    message: "待机",
    tone: "neutral",
  } as RunView,
};

export function isBusy(): boolean {
  return BUSY_PHASES.has(state.run.phase);
}

export function studentIds(): string[] {
  return state.rows
    .map((row) => row.student_id.trim())
    .filter((id) => id.length > 0);
}

/** 选中集合按 user-list.json 顺序返回（批量执行顺序由它决定）。 */
export function selectedInOrder(): string[] {
  return studentIds().filter((id) => state.selected.has(id));
}

/**
 * 用后端数据覆盖界面状态。
 * 选择恢复：优先沿用当前勾选与 user-list 的交集；首次启动则读 settings.SelectedStudents；
 * 都没有（或全失效）时回退为全选——修复旧 GUI“保存了却从不恢复”的问题。
 */
export function applyBootstrap(data: {
  project_root: string;
  settings: AppSettings;
  user_rows: UserRow[];
  statuses: ExerciseStatusMap;
  script_hosts: ScriptHost[];
  is_elevated: boolean;
  version: string;
}): void {
  state.projectRoot = data.project_root;
  state.settings = data.settings;
  state.rows = data.user_rows;
  state.statuses = data.statuses;
  state.scriptHosts = data.script_hosts;
  state.isElevated = data.is_elevated;
  state.version = data.version;

  const ids = studentIds();
  const keep = ids.filter((id) => state.selected.has(id));
  if (keep.length > 0) {
    state.selected = new Set(keep);
    return;
  }

  const saved = data.settings.SelectedStudents.filter((id) => ids.includes(id));
  state.selected = new Set(saved.length > 0 ? saved : ids);
}

export function weekLimit(): number {
  return state.settings?.WeekLimit ?? 5;
}

export function totalLimit(): number {
  return state.settings?.TotalLimit ?? 32;
}
