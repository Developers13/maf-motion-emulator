import os
import re
import time
import traceback

from maa.custom_action import CustomAction
from maa.context import Context

# 装饰器仅 agent 子进程执行（见 my_wait.py 顶部说明）
_IN_AGENT = os.environ.get("MAF_AGENT_MODE") == "1"
if _IN_AGENT:
    from maa.agent.agent_server import AgentServer

PKG = "net.crigh.xbgysport"

# 实测布局（1440x2560 物理屏 -> MaaFW 720p 逻辑坐标，2026-11-12 截图确认）：
#   课外锻炼页：大字「33 次 / 评分次数」（左列）、「0 次 / 素质课次数」（右列）
#   锻炼记录页：周 / 月 / 学期 三个 tab；周 tab 的统计卡左列是「本周有效锻炼数」
# 两处数字都直接给出汇总，不需要数记录条目。
_OCR_FULL = [0, 0, 720, 1280]
_OCR_TOTAL = [0, 230, 340, 400]   # 课外锻炼页：评分次数大数字（含下方标签）
_OCR_WEEK = [0, 690, 340, 810]    # 锻炼记录周 tab：本周有效锻炼数大数字
_WEEK_TAB = (120, 201)            # 周 tab 中心（720p）

_WEEK_LABEL = "本周有效锻炼数"
_TERM_LABEL = "本学期有效锻炼数"
_TOTAL_LABEL = "评分次数"
_HOME_MARK = "我的"

_DIGITS = re.compile(r"^(\d+)\s*次?$")


def _ocr(context: Context, image, roi) -> list:
    """用 run_recognition + pipeline_override 做一次临时 OCR（同 my_wait._scan_km 手法）。"""
    override = {"CountsOcr": {"recognition": "OCR", "roi": roi}}
    detail = context.run_recognition("CountsOcr", image, pipeline_override=override)
    if not detail or not detail.all_results:
        return []
    return list(detail.all_results)


def _texts(results) -> str:
    return " ".join((getattr(r, "text", "") or "") for r in results)


def _find_box(results, needle: str):
    for r in results:
        if needle in (getattr(r, "text", "") or ""):
            return r.box
    return None


def _biggest_number(results) -> int | None:
    """取纯数字（可带「次」）中框面积最大的一个——大号统计数字。"""
    best = None
    for r in results:
        text = (getattr(r, "text", "") or "").strip()
        m = _DIGITS.match(text)
        if not m:
            continue
        box = r.box
        area = box[2] * box[3]
        if best is None or area > best[0]:
            best = (area, int(m.group(1)))
    return best[1] if best else None


class ReadCountsAction(CustomAction):
    """读取「本周有效锻炼数 / 本学期评分次数」。

    独立于 FullRun 的一次性探针：启动体育 app → 首页进课外锻炼 → 读评分次数 →
    进锻炼记录（必要时切到周 tab）→ 读本周有效锻炼数 → 打印 `[counts] week=.. total=..`。

    **fail-open**：任何一步失败只打印 `[counts] failed=<原因>` 并返回 True——
    读数只是优化（跳过已达上限的学号），读不到时绝不能阻断正式运行。
    绝不点击「开始课外锻炼」，不改变任何业务状态。
    """

    def run(
        self,
        context: Context,
        argv: CustomAction.RunArg,
    ) -> bool:
        try:
            self._read(context)
        except Exception as e:  # noqa: BLE001 - 探针失败一律放行
            print(f"[counts] failed={type(e).__name__}: {e}", flush=True)
            traceback.print_exc()
        return True

    # ---- 内部步骤 ----
    def _read(self, context: Context) -> None:
        self._launch(context)
        self._to_home(context)

        # 首页 -> 课外锻炼
        if not self._tap_text(context, "课外锻炼", timeout_sec=20):
            print("[counts] failed=home_entry_not_found", flush=True)
            return

        total = None
        deadline = time.monotonic() + 40
        while time.monotonic() < deadline:
            results = _ocr(context, self._shot(context), _OCR_FULL)
            if _find_box(results, _TOTAL_LABEL):
                total = _biggest_number(_ocr(context, self._shot(context), _OCR_TOTAL))
                break
            if _find_box(results, "安全验证") or _find_box(results, "获取验证码"):
                print("[counts] failed=login_required", flush=True)
                return
            time.sleep(2)
        if total is None:
            print("[counts] failed=total_not_found", flush=True)

        # 课外锻炼 -> 锻炼记录
        if not self._tap_text(context, "锻炼记录", timeout_sec=20):
            print(f"[counts] failed=records_entry_not_found total={total}", flush=True)
            return

        # 锻炼记录页：等统计卡出现；默认 tab 可能是学期，需切到周 tab
        week = None
        deadline = time.monotonic() + 40
        tapped_week_tab = False
        while time.monotonic() < deadline:
            results = _ocr(context, self._shot(context), _OCR_FULL)
            if _find_box(results, _WEEK_LABEL):
                week = _biggest_number(_ocr(context, self._shot(context), _OCR_WEEK))
                break
            if _find_box(results, _TERM_LABEL) and not tapped_week_tab:
                self._click(context, *_WEEK_TAB)
                tapped_week_tab = True
            time.sleep(2)

        if total is None and week is None:
            print("[counts] failed=no_counts_found", flush=True)
            return
        print(f"[counts] week={week if week is not None else -1} total={total if total is not None else -1}", flush=True)

    def _shot(self, context: Context):
        return context.tasker.controller.post_screencap().wait().get()

    def _click(self, context: Context, x: int, y: int) -> None:
        context.tasker.controller.post_click(int(x), int(y)).wait()
        time.sleep(1.2)

    def _launch(self, context: Context) -> None:
        context.tasker.controller.post_start_app(PKG).wait()
        time.sleep(3)

    def _tap_text(self, context: Context, text: str, timeout_sec: int) -> bool:
        """在整屏 OCR 里找文本并点它的中心；超时返回 False。"""
        deadline = time.monotonic() + timeout_sec
        while time.monotonic() < deadline:
            box = _find_box(_ocr(context, self._shot(context), _OCR_FULL), text)
            if box:
                self._click(context, box[0] + box[2] / 2, box[1] + box[3] / 2)
                return True
            time.sleep(2)
        return False

    def _to_home(self, context: Context) -> None:
        """冷启动可能落在记录详情页/发现页：按 BACK 逐步退回首页（首页锚点=底部「我的」）。"""
        deadline = time.monotonic() + 90
        last = ""
        while time.monotonic() < deadline:
            results = _ocr(context, self._shot(context), _OCR_FULL)
            joined = _texts(results)
            if _HOME_MARK in joined and "课外锻炼" in joined:
                return
            if "隐私协议" in joined or ("登录" in joined and _HOME_MARK not in joined):
                print("[counts] failed=not_logged_in", flush=True)
                return
            # 页面已渲染但不是首页：退回一层（记录详情页 / 发现页 / 课外锻炼页均适用）
            if joined and joined != last:
                # 用 click_key(4=BACK) 而不是 post_shell：本版本 python 绑定没给
                # MaaControllerPostShell 设 argtypes，64 位 handle 会 OverflowError。
                context.tasker.controller.post_click_key(4).wait()
                last = joined
                time.sleep(2)
                continue
            time.sleep(2)
        print("[counts] failed=home_not_reached", flush=True)


if _IN_AGENT:
    ReadCountsAction = AgentServer.custom_action("read_counts")(ReadCountsAction)
