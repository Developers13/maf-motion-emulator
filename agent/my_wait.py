import json
import re
import time

from maa.agent.agent_server import AgentServer
from maa.custom_action import CustomAction
from maa.context import Context


# 运动中页面底部数据卡（720p 坐标）：时长 / 总公里数 / 配速 都在此区域
BOTTOM_CARD_ROI = [0, 900, 720, 380]

# 运动中锚点：暂停 / 配速 同时 OCR 不到才认为页面消失
RUNNING_KEYWORDS = ["暂停", "配速"]

# 结束页锚点：OCR 关键词。先保守猜测，采到结束页素材后可在 custom_action_param 覆盖
DEFAULT_END_KEYWORDS = ["运动结束", "本次运动", "锻炼结束", "运动成绩"]

# 总公里数读数：带小数点的数字。配速 4'58'' 与时长 12:34 不含点，以此区分
_KM_RE = re.compile(r"(\d+\.\d+)")

_RUNNING_OVR = {
    "WaitRunning": {"recognition": "OCR", "roi": BOTTOM_CARD_ROI, "expected": RUNNING_KEYWORDS}
}
_KM_OVR = {
    "WaitKm": {"recognition": "OCR", "roi": BOTTOM_CARD_ROI}
}


def _scan_km(context: Context, image) -> float | None:
    """读总公里数：OCR 底部数据卡，取带小数点的数字中框面积最大的（大号数字）。"""
    detail = context.run_recognition("WaitKm", image, pipeline_override=_KM_OVR)
    if not detail or not detail.all_results:
        return None
    candidates = []
    for r in detail.all_results:
        m = _KM_RE.search(getattr(r, "text", "") or "")
        if not m:
            continue
        box = r.box
        candidates.append((box[2] * box[3], float(m.group(1))))
    if not candidates:
        return None
    candidates.sort(reverse=True)
    return candidates[0][1]


@AgentServer.custom_action("wait_exercise_finish")
class WaitExerciseFinish(CustomAction):
    """轮询锻炼直到结束，同时做距离停滞检测。

    每 poll_sec 秒截屏一次（纯被动，不点击，不惊动前台应用）：
    1. 结束页锚点（OCR 结束关键词）命中 -> 返回 True，pipeline 的 next 接保存流程
    2. 运动中锚点（暂停/配速）连续 blank_limit 次不到 -> 页面消失，返回 False
    3. 总公里数在 stall_sec 内没有任何增长 -> 模拟疑似已死（模拟已取消/GPS 冻结），返回 False
    4. 总等待超过 max_wait_sec -> 超时，返回 False

    返回 False 时 pipeline 节点的 on_error 应保持为空，让任务以失败终止。
    """

    def run(
        self,
        context: Context,
        argv: CustomAction.RunArg,
    ) -> bool:
        param = json.loads(argv.custom_action_param) if argv.custom_action_param else {}
        poll_sec = int(param.get("poll_sec", 60))
        max_wait_sec = int(param.get("max_wait_sec", 2700))
        # 距离停滞判定窗：12km/h ≈ 0.21km/min，180s 应至少 +0.1km（读数精度 0.01 或 0.1 均覆盖）
        stall_sec = int(param.get("stall_sec", 180))
        blank_limit = int(param.get("blank_limit", 5))
        end_keywords = param.get("end_keywords", DEFAULT_END_KEYWORDS)

        end_ovr = {
            "WaitEnd": {"recognition": "OCR", "roi": [0, 0, 720, 1280], "expected": end_keywords}
        }

        started = time.monotonic()
        last_km = None
        last_increase = None
        blank = 0

        print(
            f"[wait_exercise_finish] start poll_sec={poll_sec} max_wait_sec={max_wait_sec} "
            f"stall_sec={stall_sec} blank_limit={blank_limit}"
        )

        while True:
            if context.tasker.stopping:
                print("[wait_exercise_finish] stopping requested")
                return False

            now = time.monotonic()
            elapsed = int(now - started)
            if elapsed > max_wait_sec:
                print(f"[wait_exercise_finish] TIMEOUT after {elapsed}s last_km={last_km}")
                return False

            image = context.tasker.controller.post_screencap().wait().get()

            end = context.run_recognition("WaitEnd", image, pipeline_override=end_ovr)
            if end and end.hit:
                end_text = end.best_result.text if end.best_result else "?"
                print(f"[wait_exercise_finish] END page hit (text={end_text!r}) elapsed={elapsed}s")
                return True

            running = context.run_recognition("WaitRunning", image, pipeline_override=_RUNNING_OVR)
            if running and running.hit:
                blank = 0
            else:
                blank += 1
                print(f"[wait_exercise_finish] running page not seen x{blank} elapsed={elapsed}s")
                if blank >= blank_limit:
                    print("[wait_exercise_finish] exercise page gone, giving up")
                    return False

            km = _scan_km(context, image)
            if km is not None:
                # 距离单调递增：只有增长才算活性信号，读数抖动（不变/变小）一律忽略
                if last_km is None or km > last_km:
                    last_km = km
                    last_increase = now
                    print(f"[wait_exercise_finish] km={km} elapsed={elapsed}s")
            elif last_increase is None:
                # 首次没读到数字时从现在起算，避免把 OCR 预热期误判成停滞
                last_increase = now

            if last_increase is not None and now - last_increase > stall_sec:
                print(
                    f"[wait_exercise_finish] DISTANCE STALLED {int(now - last_increase)}s "
                    f"at km={last_km} — 模拟疑似已死（模拟已取消/GPS 冻结）"
                )
                return False

            time.sleep(poll_sec)
