export type Tone = "ok" | "fail" | "neutral";

export function showToast(tone: Tone, title: string, body: string): void {
  const stack = document.getElementById("toasts");
  if (!stack) {
    return;
  }

  const card = document.createElement("div");
  card.className = "toast";
  card.dataset.tone = tone;

  const heading = document.createElement("div");
  heading.className = "toast-title";
  heading.textContent = title;

  const text = document.createElement("div");
  text.className = "toast-body";
  text.textContent = body;

  card.append(heading, text);
  stack.append(card);

  window.setTimeout(() => {
    card.style.opacity = "0";
    card.style.transform = "translateY(6px)";
    window.setTimeout(() => card.remove(), 200);
  }, 4200);
}

interface ConfirmOptions {
  title: string;
  body: string;
  okText?: string;
  cancelText?: string;
}

/** 自绘确认框：替代旧 WinForms MessageBox，保持同一条确认语义。 */
export function confirmDialog(options: ConfirmOptions): Promise<boolean> {
  const backdrop = document.getElementById("confirm-backdrop");
  const title = document.getElementById("confirm-title");
  const body = document.getElementById("confirm-body");
  const ok = document.getElementById("confirm-ok");
  const cancel = document.getElementById("confirm-cancel");
  if (!backdrop || !title || !body || !ok || !cancel) {
    return Promise.resolve(false);
  }

  title.textContent = options.title;
  body.textContent = options.body;
  ok.textContent = options.okText ?? "确认";
  cancel.textContent = options.cancelText ?? "取消";
  backdrop.hidden = false;
  ok.focus();

  return new Promise<boolean>((resolve) => {
    const finish = (value: boolean) => {
      backdrop.hidden = true;
      ok.removeEventListener("click", onOk);
      cancel.removeEventListener("click", onCancel);
      backdrop.removeEventListener("click", onBackdrop);
      document.removeEventListener("keydown", onKey);
      resolve(value);
    };
    const onOk = () => finish(true);
    const onCancel = () => finish(false);
    const onBackdrop = (event: MouseEvent) => {
      if (event.target === backdrop) {
        finish(false);
      }
    };
    const onKey = (event: KeyboardEvent) => {
      if (event.key === "Escape") {
        finish(false);
      }
    };

    ok.addEventListener("click", onOk);
    cancel.addEventListener("click", onCancel);
    backdrop.addEventListener("click", onBackdrop);
    document.addEventListener("keydown", onKey);
  });
}
