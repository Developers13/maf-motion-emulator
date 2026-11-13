import { classifyLogLine } from "../lib/log-classify";
import type { LogEvent } from "../api";

const MAX_LINES = 5000;

export function appendLog(event: LogEvent): void {
  const log = document.getElementById("log");
  if (!log) {
    return;
  }

  const line = document.createElement("div");
  const kind = classifyLogLine(event.line, event.is_error);
  line.className = `log-line kind-${kind}`;

  const ts = document.createElement("span");
  ts.className = "log-ts";
  ts.textContent = event.time;

  const msg = document.createElement("span");
  msg.className = "log-msg";
  msg.textContent = event.line;

  line.append(ts, msg);
  log.append(line);

  while (log.childElementCount > MAX_LINES) {
    log.firstElementChild?.remove();
  }

  if (autoScrollEnabled()) {
    log.scrollTop = log.scrollHeight;
  }
}

export function autoScrollEnabled(): boolean {
  const toggle = document.getElementById("autoscroll") as HTMLInputElement | null;
  return toggle?.checked ?? true;
}

export function clearLog(): void {
  document.getElementById("log")?.replaceChildren();
}
