import { listen } from "@tauri-apps/api/event";
import {
  isPermissionGranted,
  requestPermission,
  sendNotification,
} from "@tauri-apps/plugin-notification";
import {
  api,
  runningInTauri,
  useBackend,
  type Bootstrap,
  type CountsEvent,
  type LogEvent,
  type RunStateEvent,
  type ToastEvent,
} from "./api";
import { applyBootstrap, isBusy, selectedInOrder, state } from "./state";
import { renderStudentList, updateSelectionChrome } from "./ui/students";
import { appendLog, clearLog } from "./ui/log";
import { confirmDialog, showToast, type Tone } from "./ui/toast";
import { initSettings, openSettings } from "./ui/settings";

let notificationsAllowed = false;

function el<T extends HTMLElement>(id: string): T | null {
  return document.getElementById(id) as T | null;
}

function nowTime(): string {
  const now = new Date();
  const pad = (n: number) => String(n).padStart(2, "0");
  return `${pad(now.getHours())}:${pad(now.getMinutes())}:${pad(now.getSeconds())}`;
}

function logLine(line: string, isError = false): void {
  appendLog({ time: nowTime(), line, is_error: isError, source: "gui" });
}

async function ensureNotifications(): Promise<void> {
  try {
    notificationsAllowed = await isPermissionGranted();
    if (!notificationsAllowed) {
      notificationsAllowed = (await requestPermission()) === "granted";
    }
  } catch {
    notificationsAllowed = false;
  }
}

function notify(tone: Tone, title: string, body: string): void {
  if (!runningInTauri() || !notificationsAllowed || document.hasFocus() || tone === "neutral") {
    return;
  }
  try {
    sendNotification({ title, body });
  } catch {
    // 通知失败不影响主流程。
  }
}

function renderStatus(): void {
  const pill = el<HTMLDivElement>("status-pill");
  const text = el<HTMLSpanElement>("status-text");
  if (pill && text) {
    pill.dataset.tone = state.run.tone;
    text.textContent = state.run.message;
  }

  const progress = el<HTMLDivElement>("work-progress");
  const progressText = el<HTMLSpanElement>("progress-text");
  const bar = el<HTMLSpanElement>("progress-bar");
  if (progress && progressText && bar) {
    const showProgress =
      (state.run.phase === "batch" || state.run.phase === "running") &&
      (state.run.total ?? 0) > 1 &&
      (state.run.index ?? 0) > 0;
    progress.hidden = !showProgress;
    if (showProgress) {
      progressText.textContent = `${state.run.index}/${state.run.total} · ${state.run.student ?? ""}`;
      const percent = Math.round(((state.run.index ?? 0) / (state.run.total ?? 1)) * 100);
      bar.style.width = `${percent}%`;
    }
  }

  updateButtons();
}

function updateButtons(): void {
  const start = el<HTMLButtonElement>("btn-start");
  if (start) {
    start.disabled = isBusy() || state.selected.size === 0;
    start.textContent = isBusy() ? "运行中…" : "开始运行";
  }
  updateSelectionChrome();
}

function renderAll(): void {
  const path = el<HTMLDivElement>("project-path");
  if (path) {
    path.textContent = state.projectRoot;
    path.title = state.projectRoot;
  }

  const footLeft = el<HTMLSpanElement>("foot-left");
  if (footLeft) {
    footLeft.textContent = state.isElevated ? "管理员权限" : "未提权 · MuMu/adb 可能失败";
  }
  const footRight = el<HTMLSpanElement>("foot-right");
  if (footRight) {
    footRight.textContent = `v${state.version} · ${state.projectRoot}`;
  }

  const autoscroll = el<HTMLInputElement>("autoscroll");
  if (autoscroll && state.settings) {
    autoscroll.checked = state.settings.AutoScroll;
  }

  renderStudentList(toggleStudent);
  renderStatus();
}

function toggleStudent(id: string, checked: boolean): void {
  if (checked) {
    state.selected.add(id);
  } else {
    state.selected.delete(id);
  }
  void persistSelection();
  renderStudentList(toggleStudent);
  renderStatus();
}

async function persistSelection(): Promise<void> {
  if (!state.settings) {
    return;
  }
  state.settings.SelectedStudents = [...state.selected];
  try {
    await api.saveSettings(state.settings);
  } catch (e) {
    logLine(`[settings] WARN 保存勾选失败：${String(e)}`, false);
  }
}

async function startRun(): Promise<void> {
  if (isBusy()) {
    return;
  }
  const ids = selectedInOrder();
  if (ids.length === 0) {
    return;
  }

  const missing = ids.filter((id) => {
    const row = state.rows.find((item) => item.student_id === id);
    return !row || row.password.length === 0;
  });

  if (ids.length > 1) {
    const lines = [
      `将对选中的 ${ids.length} 个学号逐个运行：`,
      "每个学号：关闭其他实例 → 自动准备（启动实例 + 环境检查）→ 跑完整流程 → 关闭该实例；全部完成后关闭所有实例。",
      "",
      missing.length > 0
        ? `注意：${missing.length} 个学号尚未设置密码，将无法登录：${missing.join(", ")}`
        : "",
      "逐个运行约需 20–40 分钟/人（含模拟锻炼时长），合计较久。确认开始吗？",
    ];
    const ok = await confirmDialog({
      title: "开始运行（批量）",
      body: lines.filter((line) => line !== "").join("\n"),
      okText: "开始",
    });
    if (!ok) {
      return;
    }
  } else if (missing.length > 0) {
    const ok = await confirmDialog({
      title: "缺少密码",
      body: `学号 ${ids[0]} 尚未设置密码（user-list.json 中为空）。\n没有密码将无法登录体育 App，确认要继续吗？\n\n建议先在「设置 → 用户列表」补全密码与公里数。`,
      okText: "仍然运行",
    });
    if (!ok) {
      return;
    }
  }

  try {
    await api.startRun(ids);
  } catch (e) {
    showToast("fail", "无法开始", String(e));
  }
}

async function hotReload(): Promise<void> {
  if (isBusy()) {
    return;
  }
  try {
    const data = await api.hotReload();
    applyBootstrap(data);
    renderAll();
    logLine(`=== Hot reload: ${state.selected.size} student(s) selected + settings refreshed ===`);
  } catch (e) {
    logLine(`[gui] 热重载失败：${String(e)}`, true);
  }
}

function bindEvents(): void {
  el<HTMLButtonElement>("btn-start")?.addEventListener("click", () => {
    void startRun();
  });
  el<HTMLButtonElement>("btn-stop")?.addEventListener("click", () => {
    void api.stopRun();
  });
  el<HTMLButtonElement>("btn-reload")?.addEventListener("click", () => {
    void hotReload();
  });
  el<HTMLButtonElement>("btn-settings")?.addEventListener("click", openSettings);
  el<HTMLButtonElement>("btn-clear")?.addEventListener("click", clearLog);

  el<HTMLButtonElement>("select-all")?.addEventListener("click", () => {
    for (const row of state.rows) {
      const id = row.student_id.trim();
      if (id) {
        state.selected.add(id);
      }
    }
    void persistSelection();
    renderAll();
  });

  el<HTMLButtonElement>("select-none")?.addEventListener("click", () => {
    state.selected.clear();
    void persistSelection();
    renderAll();
  });

  el<HTMLInputElement>("autoscroll")?.addEventListener("change", (event) => {
    const checked = (event.target as HTMLInputElement).checked;
    if (state.settings) {
      state.settings.AutoScroll = checked;
      void api.saveSettings(state.settings).catch(() => undefined);
    }
  });

  initSettings({
    onSaved: () => {
      renderAll();
      logLine("=== Settings saved ===");
    },
    onBootstrap: (data: Bootstrap) => applyBootstrap(data),
  });
}

function onLog(event: LogEvent): void {
  appendLog(event);
}

function onRunState(event: RunStateEvent): void {
  state.run = event;
  renderStatus();
}

function onCounts(counts: CountsEvent): void {
  state.statuses[counts.student] = {
    Week: counts.week,
    Total: counts.total,
    WeekKey: counts.week_key,
    ReadAt: counts.read_at,
    Confirmed: counts.confirmed,
  };
  renderStudentList(toggleStudent);
}

function onToast(event: ToastEvent): void {
  const { tone, title, body } = event;
  const safeTone: Tone = tone === "ok" || tone === "fail" ? tone : "neutral";
  showToast(safeTone, title, body);
  notify(safeTone, title, body);
}

async function bindBackendEvents(): Promise<void> {
  await listen<LogEvent>("log", (event) => onLog(event.payload));
  await listen<RunStateEvent>("run-state", (event) => onRunState(event.payload));
  await listen<CountsEvent>("student-counts", (event) => onCounts(event.payload));
  await listen<ToastEvent>("toast", (event) => onToast(event.payload));
}

async function boot(): Promise<void> {
  let seedLogs: (() => void) | null = null;
  if (!runningInTauri()) {
    // 浏览器预览：用内存后端渲染真实布局，不触碰文件/进程。
    const { createMockBackend } = await import("./mock");
    const mock = createMockBackend();
    useBackend(mock.backend);
    mock.attach({ log: onLog, runState: onRunState, counts: onCounts, toast: onToast });
    seedLogs = mock.seed;
  }

  const data = await api.bootstrap();
  applyBootstrap(data);
  bindEvents();
  if (runningInTauri()) {
    await bindBackendEvents();
    await ensureNotifications();
  }
  renderAll();
  logLine(`=== Ready. ${state.version} · ${state.isElevated ? "elevated" : "not elevated"} ===`);
  seedLogs?.();

  // 浏览器预览的深链接入口，便于截图检查：/#settings 打开设置、/#running 模拟运行。
  if (!runningInTauri()) {
    if (location.hash.startsWith("#settings")) {
      openSettings();
      // 深链接默认停在用户列表；#settings-env 才切到运行环境（截图/联调用）。
      if (location.hash === "#settings-env") {
        document.querySelector<HTMLButtonElement>("#settings-backdrop .tab[data-tab='env']")?.click();
      }
    } else if (location.hash === "#running") {
      await api.startRun(selectedInOrder().slice(0, 3));
    }
  }
}

void boot().catch((error: unknown) => {
  const message = error instanceof Error ? error.message : String(error);
  document.body.innerHTML = `<pre style="padding:24px;font-family:monospace;color:#9f2f2d">启动失败：${message}</pre>`;
});
