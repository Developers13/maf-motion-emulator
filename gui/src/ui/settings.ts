import { api, type Bootstrap, type ScriptHost } from "../api";
import type { AppSettings } from "../lib/settings";
import type { UserRow } from "../lib/userlist";
import { normalizeRow } from "../lib/userlist";
import { weekIsCurrent, weekKeyFor } from "../lib/status";
import { state as appState } from "../state";

type Tab = "users" | "env" | "rules";

interface DraftRow {
  student_id: string;
  password: string;
  distance: string;
  /** 手动填写的次数（字符串便于处理空值；都为空=清除该学号缓存）。 */
  week: string;
  total: string;
  /** 打开设置时的原值，用于判断是否有改动。 */
  orig_student_id: string;
  orig_week: string;
  orig_total: string;
}

interface CountUpdate {
  student: string;
  week: number | null;
  total: number | null;
}

interface SettingsHooks {
  onSaved: () => void;
  onBootstrap: (data: Bootstrap) => void;
}

let hooks: SettingsHooks | null = null;
let draft: { settings: AppSettings; rows: DraftRow[]; tab: Tab; hosts: ScriptHost[] } | null = null;

function el<T extends HTMLElement>(id: string): T | null {
  return document.getElementById(id) as T | null;
}

function error(message: string): void {
  const node = el<HTMLSpanElement>("settings-error");
  if (node) {
    node.textContent = message;
  }
}

function clearError(): void {
  error("");
}

function escapeHtml(value: string): string {
  return value
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;");
}

export function initSettings(hooksValue: SettingsHooks): void {
  hooks = hooksValue;

  el<HTMLButtonElement>("settings-close")?.addEventListener("click", closeSettings);
  el<HTMLButtonElement>("settings-cancel")?.addEventListener("click", closeSettings);
  el<HTMLButtonElement>("settings-save")?.addEventListener("click", () => {
    void save();
  });
  document.querySelectorAll<HTMLButtonElement>(".tab").forEach((tab) => {
    tab.addEventListener("click", () => {
      if (!draft) {
        return;
      }
      draft.tab = (tab.dataset.tab as Tab) ?? "users";
      document.querySelectorAll<HTMLButtonElement>(".tab").forEach((node) => {
        const active = node === tab;
        node.classList.toggle("is-active", active);
        node.setAttribute("aria-selected", active ? "true" : "false");
      });
      renderBody();
    });
  });

  const backdrop = el<HTMLDivElement>("settings-backdrop");
  backdrop?.addEventListener("click", (event) => {
    if (event.target === backdrop) {
      closeSettings();
    }
  });
  document.addEventListener("keydown", (event) => {
    if (event.key === "Escape" && backdrop && !backdrop.hidden) {
      closeSettings();
    }
  });
}

export function openSettings(): void {
  if (!appState.settings) {
    return;
  }

  draft = {
    settings: structuredClone(appState.settings),
    rows: appState.rows.map((row) => draftRowFrom(row)),
    tab: "users",
    hosts: appState.scriptHosts,
  };

  const sub = el<HTMLParagraphElement>("settings-sub");
  if (sub) {
    sub.textContent = `${appState.projectRoot}\\user-list.json`;
  }
  clearError();
  renderBody();
  const backdrop = el<HTMLDivElement>("settings-backdrop");
  if (backdrop) {
    backdrop.hidden = false;
  }
  document.querySelector<HTMLDivElement>(".modal-settings")?.focus();
}

function draftRowFrom(row: UserRow): DraftRow {
  const id = row.student_id.trim();
  const status = appState.statuses[id];
  // 周次数跨周视为未知（WeekKey 不是本周则留空），总次数无周概念直接展示。
  const week =
    status !== undefined && weekIsCurrent(status) ? String(status.Week) : "";
  const total = status !== undefined ? String(status.Total) : "";
  return {
    student_id: row.student_id,
    password: row.password,
    distance: row.distance_km === null ? "" : String(row.distance_km),
    week,
    total,
    orig_student_id: id,
    orig_week: week,
    orig_total: total,
  };
}

function closeSettings(): void {
  const backdrop = el<HTMLDivElement>("settings-backdrop");
  if (backdrop) {
    backdrop.hidden = true;
  }
  draft = null;
}

function renderBody(): void {
  const body = el<HTMLDivElement>("settings-body");
  if (!body || !draft) {
    return;
  }
  clearError();
  if (draft.tab === "users") {
    body.innerHTML = usersTab(draft.rows);
    bindUsersTab();
  } else if (draft.tab === "env") {
    body.innerHTML = envTab(draft.settings, draft.hosts);
    bindEnvTab();
  } else {
    body.innerHTML = rulesTab(draft.settings);
    bindRulesTab();
  }
}

function usersTab(rows: DraftRow[]): string {
  const body = rows
    .map(
      (row, index) => `
      <tr data-index="${index}">
        <td><input class="input" data-col="student_id" value="${escapeHtml(row.student_id)}" spellcheck="false" /></td>
        <td><input class="input" data-col="password" type="password" value="${escapeHtml(row.password)}" autocomplete="off" /></td>
        <td><input class="input" data-col="distance" value="${escapeHtml(row.distance)}" inputmode="decimal" placeholder="3.2" /></td>
        <td><input class="input" data-col="week" type="number" min="0" step="1" value="${escapeHtml(row.week)}" placeholder="—" /></td>
        <td><input class="input" data-col="total" type="number" min="0" step="1" value="${escapeHtml(row.total)}" placeholder="—" /></td>
        <td><button type="button" class="tiny" data-remove="${index}">删除</button></td>
      </tr>`,
    )
    .join("");

  return `
    <table class="table" aria-label="用户列表">
      <thead>
        <tr><th style="width:26%">学号</th><th style="width:24%">密码</th><th style="width:13%">距离 km</th><th style="width:11%">本周</th><th style="width:11%">总次数</th><th></th></tr>
      </thead>
      <tbody id="user-rows">${body}</tbody>
    </table>
    <div class="table-actions">
      <button type="button" class="btn btn-ghost" id="user-add">添加行</button>
      <button type="button" class="btn btn-ghost" id="user-reload">重新加载</button>
    </div>
    <p class="hint" style="margin-top:10px">距离只接受 2.4 或 3.2（留空按 3.2 处理）。密码只写入 user-list.json，不进日志、不进设置缓存。</p>
    <p class="hint">本周/总次数可手动填写：填写后视为已确认并参与批量跳过；留空即清除该学号缓存；下一次 App 实读成功会覆盖手填值。</p>
  `;
}

function bindUsersTab(): void {
  const body = el<HTMLDivElement>("settings-body");
  body?.querySelectorAll<HTMLInputElement>("input[data-col]").forEach((input) => {
    input.addEventListener("input", () => {
      const row = input.closest("tr");
      const index = Number(row?.getAttribute("data-index") ?? "-1");
      const col = input.dataset.col;
      if (!draft || index < 0 || !col) {
        return;
      }
      const target = draft.rows[index];
      if (!target) {
        return;
      }
      if (col === "student_id") {
        target.student_id = input.value;
      } else if (col === "password") {
        target.password = input.value;
      } else if (col === "week") {
        target.week = input.value;
      } else if (col === "total") {
        target.total = input.value;
      } else {
        target.distance = input.value;
      }
    });
  });

  body?.querySelectorAll<HTMLButtonElement>("button[data-remove]").forEach((button) => {
    button.addEventListener("click", () => {
      const index = Number(button.dataset.remove ?? "-1");
      if (!draft || index < 0) {
        return;
      }
      draft.rows.splice(index, 1);
      renderBody();
    });
  });

  el<HTMLButtonElement>("user-add")?.addEventListener("click", () => {
    if (!draft) {
      return;
    }
    draft.rows.push({
      student_id: "",
      password: "",
      distance: "3.2",
      week: "",
      total: "",
      orig_student_id: "",
      orig_week: "",
      orig_total: "",
    });
    renderBody();
    const inputs = document.querySelectorAll<HTMLInputElement>("#user-rows input[data-col='student_id']");
    inputs[inputs.length - 1]?.focus();
  });

  el<HTMLButtonElement>("user-reload")?.addEventListener("click", () => {
    void (async () => {
      clearError();
      try {
        const fresh = await api.hotReload();
        hooks?.onBootstrap(fresh);
        if (!draft) {
          return;
        }
        draft.settings = structuredClone(fresh.settings);
        draft.hosts = fresh.script_hosts;
        draft.rows = fresh.user_rows.map((row) => draftRowFrom(row));
        renderBody();
      } catch (e) {
        error(`重新加载失败：${String(e)}`);
      }
    })();
  });
}

function hostOptions(settings: AppSettings, hosts: ScriptHost[]): string {
  const options = [
    `<option value="">自动（推荐）</option>`,
    ...hosts.map(
      (host) =>
        `<option value="${escapeHtml(host.path)}">${escapeHtml(host.label)} · ${escapeHtml(host.path)}</option>`,
    ),
  ];
  if (settings.ScriptHostPath && !hosts.some((host) => host.path === settings.ScriptHostPath)) {
    options.push(
      `<option value="${escapeHtml(settings.ScriptHostPath)}">${escapeHtml(settings.ScriptHostPath)}（未找到）</option>`,
    );
  }
  return options.join("");
}

function envTab(settings: AppSettings, hosts: ScriptHost[]): string {
  const envRows = Object.entries(settings.Environment)
    .map(
      ([key, value], index) => `
      <tr data-index="${index}">
        <td><input class="input" data-env="key" value="${escapeHtml(key)}" spellcheck="false" /></td>
        <td><input class="input" data-env="value" value="${escapeHtml(value)}" spellcheck="false" /></td>
        <td><button type="button" class="tiny" data-env-remove="${index}">删除</button></td>
      </tr>`,
    )
    .join("");

  return `
    <div class="field-grid">
      <label for="s-launcher">Python 启动器</label>
      <input class="input" id="s-launcher" data-field="PythonLauncher" value="${escapeHtml(settings.PythonLauncher)}" spellcheck="false" />
      <label>项目根目录</label>
      <input class="input" value="${escapeHtml(appState.projectRoot)}" readonly />
      <span class="hint">运行时从程序目录向上找 tools/e2e.py 自动定位，此路径只读。</span>
      <label for="s-mumu">MuMu 管理器</label>
      <input class="input" id="s-mumu" data-field="MuMuManagerPath" value="${escapeHtml(settings.MuMuManagerPath)}" spellcheck="false" />
      <label for="s-template">模板虚拟机</label>
      <input class="input" id="s-template" data-field="TemplateVmName" value="${escapeHtml(settings.TemplateVmName)}" />
      <label for="s-host">脚本执行器</label>
      <select class="select" id="s-host" data-field="ScriptHostPath">${hostOptions(settings, hosts)}</select>
    </div>
    <div class="divider"></div>
    <p class="subhead">环境变量</p>
    <table class="table" aria-label="环境变量">
      <thead><tr><th style="width:40%">键</th><th>值</th><th></th></tr></thead>
      <tbody id="env-rows">${envRows}</tbody>
    </table>
    <div class="table-actions">
      <button type="button" class="btn btn-ghost" id="env-add">添加变量</button>
    </div>
    <p class="hint" style="margin-top:10px">MAF_USER / MAF_PASS / MAF_LAPS / MAF_TARGET_KM 由脚本依据 user-list.json 自动注入，不需要手填。</p>
  `;
}

function bindEnvTab(): void {
  if (!draft) {
    return;
  }
  const select = el<HTMLSelectElement>("s-host");
  if (select) {
    select.value = draft.settings.ScriptHostPath;
  }

  const body = el<HTMLDivElement>("settings-body");
  body?.querySelectorAll<HTMLInputElement>("input[data-env]").forEach((input) => {
    input.addEventListener("input", () => {
      const row = input.closest("tr");
      const index = Number(row?.getAttribute("data-index") ?? "-1");
      const current = Object.keys(draft?.settings.Environment ?? {});
      const key = current[index];
      if (!draft || index < 0 || key === undefined) {
        return;
      }
      if (input.dataset.env === "key") {
        const value = draft.settings.Environment[key] ?? "";
        delete draft.settings.Environment[key];
        draft.settings.Environment[input.value] = value;
      } else {
        draft.settings.Environment[key] = input.value;
      }
    });
  });

  body?.querySelectorAll<HTMLButtonElement>("button[data-env-remove]").forEach((button) => {
    button.addEventListener("click", () => {
      if (!draft) {
        return;
      }
      const index = Number(button.dataset.envRemove ?? "-1");
      const key = Object.keys(draft.settings.Environment)[index];
      if (key !== undefined) {
        delete draft.settings.Environment[key];
        renderBody();
      }
    });
  });

  el<HTMLButtonElement>("env-add")?.addEventListener("click", () => {
    if (!draft) {
      return;
    }
    let index = 1;
    while (draft.settings.Environment[`MAF_VAR_${index}`] !== undefined) {
      index += 1;
    }
    draft.settings.Environment[`MAF_VAR_${index}`] = "";
    renderBody();
  });
}

function rulesTab(settings: AppSettings): string {
  return `
    <div class="field-grid">
      <label for="s-week">每周次数上限</label>
      <input class="input" id="s-week" data-number="WeekLimit" type="number" min="1" step="1" value="${settings.WeekLimit}" />
      <span class="hint">达到后批量运行会跳过该学号（单个手动运行仍会强制跑）。</span>
      <label for="s-total">总次数上限</label>
      <input class="input" id="s-total" data-number="TotalLimit" type="number" min="1" step="1" value="${settings.TotalLimit}" />
      <span class="hint">一学期计入成绩的总次数上限，默认 32。</span>
      <label for="s-confirmskip">缓存预跳过</label>
      <label class="switch"><input type="checkbox" id="s-confirmskip" ${settings.SkipOnConfirmedCounts ? "checked" : ""} /><span>达上限直接跳过</span></label>
      <span class="hint">批量运行时两类学号不开模拟器：① 次数已实读确认且达上限；② 待审核估算（+1）达上限且读数尚不可信（当天，或次日清晨 06:00 前——审核每日凌晨进行）。审核时点过后由探针实读确认。</span>
      <label for="s-autoscroll">日志自动滚动</label>
      <label class="switch"><input type="checkbox" id="s-autoscroll" ${settings.AutoScroll ? "checked" : ""} /><span>跟随最新日志</span></label>
    </div>
  `;
}

function bindRulesTab(): void {
  const autoscroll = el<HTMLInputElement>("s-autoscroll");
  autoscroll?.addEventListener("change", () => {
    const dock = el<HTMLInputElement>("autoscroll");
    if (dock) {
      dock.checked = autoscroll.checked;
    }
  });
}

function collect():
  | { settings: AppSettings; rows: UserRow[]; countUpdates: CountUpdate[] }
  | { error: string } {
  if (!draft) {
    return { error: "设置未初始化" };
  }
  const body = el<HTMLDivElement>("settings-body");
  const settings = structuredClone(draft.settings);

  body?.querySelectorAll<HTMLInputElement | HTMLSelectElement>("[data-field]").forEach((input) => {
    const field = input.dataset.field as keyof AppSettings | undefined;
    if (!field) {
      return;
    }
    if (field === "WeekLimit" || field === "TotalLimit") {
      return;
    }
    (settings[field] as unknown as string) = input.value;
  });

  body?.querySelectorAll<HTMLInputElement>("[data-number]").forEach((input) => {
    const field = input.dataset.number as "WeekLimit" | "TotalLimit" | undefined;
    const value = Number.parseInt(input.value, 10);
    if (field && Number.isFinite(value) && value > 0) {
      settings[field] = value;
    }
  });

  const autoscroll = el<HTMLInputElement>("s-autoscroll");
  if (autoscroll) {
    settings.AutoScroll = autoscroll.checked;
  }

  const confirmskip = el<HTMLInputElement>("s-confirmskip");
  if (confirmskip) {
    settings.SkipOnConfirmedCounts = confirmskip.checked;
  }

  // 环境变量表以 DOM 为准（键可能刚被改名）。
  const env: Record<string, string> = {};
  body?.querySelectorAll<HTMLTableRowElement>("#env-rows tr").forEach((row) => {
    const key = row.querySelector<HTMLInputElement>("input[data-env='key']")?.value.trim() ?? "";
    const value = row.querySelector<HTMLInputElement>("input[data-env='value']")?.value ?? "";
    if (key) {
      env[key] = value;
    }
  });
  if (body?.querySelector("#env-rows")) {
    settings.Environment = env;
  }

  const rows: UserRow[] = [];
  const countUpdates: CountUpdate[] = [];
  for (const raw of draft.rows) {
    const result = normalizeRow(raw.student_id, raw.password, raw.distance);
    if (result.error) {
      return { error: result.error };
    }
    if (!result.row) {
      continue; // 空学号行跳过
    }

    const weekText = raw.week.trim();
    const totalText = raw.total.trim();
    const bothEmpty = weekText === "" && totalText === "";
    let week: number | null = null;
    let total: number | null = null;
    if (!bothEmpty) {
      if (weekText === "" || totalText === "") {
        return {
          error: `学号 ${result.row.student_id}：本周与总次数需同时填写，或都留空以清除。`,
        };
      }
      week = Number.parseInt(weekText, 10);
      total = Number.parseInt(totalText, 10);
      if (!Number.isFinite(week) || week < 0 || !Number.isFinite(total) || total < 0) {
        return { error: `学号 ${result.row.student_id}：次数必须是非负整数。` };
      }
    }

    rows.push(result.row);
    const changed =
      result.row.student_id !== raw.orig_student_id ||
      weekText !== raw.orig_week ||
      totalText !== raw.orig_total;
    if (changed) {
      countUpdates.push({ student: result.row.student_id, week, total });
    }
  }

  settings.SelectedStudents = [...appState.selected];
  return { settings, rows, countUpdates };
}

async function save(): Promise<void> {
  clearError();
  const collected = collect();
  if ("error" in collected) {
    error(collected.error);
    return;
  }

  try {
    await api.saveSettings(collected.settings);
    await api.saveUserRows(collected.rows);
    for (const update of collected.countUpdates) {
      await api.saveStudentCounts(update.student, update.week, update.total);
    }
  } catch (e) {
    error(`保存失败：${String(e)}`);
    return;
  }

  // 本地镜像与后端保持一致（事件只覆盖"设置"，清除不会发事件）。
  const now = new Date();
  for (const update of collected.countUpdates) {
    if (update.week === null || update.total === null) {
      delete appState.statuses[update.student];
    } else {
      appState.statuses[update.student] = {
        Week: update.week,
        Total: update.total,
        WeekKey: weekKeyFor(now),
        ReadAt: now.toISOString(),
        Confirmed: true,
        LastRunDay: "",
      };
    }
  }

  appState.settings = collected.settings;
  appState.rows = collected.rows;
  closeSettings();
  hooks?.onSaved();
}
