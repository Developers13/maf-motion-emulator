import { studentIds, state, totalLimit, weekLimit } from "../state";
import { weekIsCurrent } from "../lib/status";

function tooltipFor(id: string): string {
  const status = state.statuses[id];
  if (!status) {
    return `本周 —/${weekLimit()} · 总 —/${totalLimit()}\n未读取过（开始运行时会先读 App 次数）`;
  }
  const weekText = weekIsCurrent(status) ? String(status.Week) : "—";
  let text = `本周 ${weekText}/${weekLimit()} · 总 ${status.Total}/${totalLimit()}`;
  if (!weekIsCurrent(status)) {
    text += "\n（本周次数待重读：上次读数属于上一自然周）";
  }
  const readAt = new Date(status.ReadAt);
  if (!Number.isNaN(readAt.getTime())) {
    const pad = (n: number) => String(n).padStart(2, "0");
    text += `\n读取于 ${pad(readAt.getMonth() + 1)}-${pad(readAt.getDate())} ${pad(
      readAt.getHours(),
    )}:${pad(readAt.getMinutes())}`;
  }
  return text;
}

/** 重画学号列表；只显示 user-list.json 里的全部学号，勾选态来自 state.selected。 */
export function renderStudentList(onToggle: (id: string, checked: boolean) => void): void {
  const list = document.getElementById("student-list");
  const empty = document.getElementById("student-empty");
  if (!list || !empty) {
    return;
  }

  const ids = studentIds();
  empty.hidden = ids.length > 0;
  list.replaceChildren();

  for (const id of ids) {
    const row = document.createElement("label");
    row.className = "student-row";
    row.title = tooltipFor(id);
    row.dataset.id = id;

    const checkbox = document.createElement("input");
    checkbox.type = "checkbox";
    checkbox.checked = state.selected.has(id);
    checkbox.addEventListener("change", () => {
      onToggle(id, checkbox.checked);
    });

    const label = document.createElement("span");
    label.className = "student-id";
    label.textContent = id;

    const dots = document.createElement("span");
    dots.className = "dots";
    const weekDot = document.createElement("i");
    weekDot.className = "dot week";
    const totalDot = document.createElement("i");
    totalDot.className = "dot total";
    const status = state.statuses[id];
    const weekDone =
      status !== undefined && weekIsCurrent(status) && status.Week >= weekLimit();
    const totalDone = status !== undefined && status.Total >= totalLimit();
    if (weekDone) {
      weekDot.classList.add("on");
    }
    if (totalDone) {
      totalDot.classList.add("on");
    }
    dots.append(weekDot, totalDot);

    if (checkbox.checked) {
      row.classList.add("is-selected");
    }
    if (weekDone) {
      row.classList.add("at-week");
    }
    if (totalDone) {
      row.classList.add("at-total");
    }

    row.append(checkbox, label, dots);
    list.append(row);
  }

  updateSelectionChrome();
}

export function updateSelectionChrome(): void {
  const total = studentIds().length;
  const count = document.getElementById("selection-count");
  if (count) {
    count.textContent = `${state.selected.size} / ${total}`;
  }
}
