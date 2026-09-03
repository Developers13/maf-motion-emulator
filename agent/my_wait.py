import json
import os
import re
import time

from maa.custom_action import CustomAction
from maa.context import Context

# AgentServer 装饰器只能在 agent 子进程执行：其注册流程会把进程级 Library 切到
# agent_server 模式，污染同进程后续 framework 调用（argtypes 丢失 -> OverflowError）。
# runner 进程（tools/run_task.py）用 Resource.register_custom_action 进程内注册。
_IN_AGENT = os.environ.get("MAF_AGENT_MODE") == "1"
if _IN_AGENT:
    from maa.agent.agent_server import AgentServer

# 运动中页面顶部统计区（720p）：时长 | 总公里数(大字) | 配速，y 90~340 实测
STATS_ROI = [0, 90, 720, 250]

# 运动中锚点：顶部标题「运动中」或底部「暂停」按钮字样（双保险，全屏 OR）。
# 暂停态/结束单弹出后标题变「运动暂停中」，不含 运动中 连续字样，但结束单弹出是过渡态
RUNNING_KEYWORDS = ["运动中", "暂停"]
HEADER_ROI = [0, 0, 720, 1280]

# 总公里数读数：带小数点的数字。时长 00:57:48（冒号）与配速 09'41''（撇号）都不含点
_KM_RE = re.compile(r"(\d+\.\d+)")

_RUNNING_OVR = {
    "WaitRunning": {"recognition": "OCR", "roi": HEADER_ROI, "expected": RUNNING_KEYWORDS}
}
_KM_OVR = {
    "WaitKm": {"recognition": "OCR", "roi": STATS_ROI}
}


def _scan_km(context: Context, image) -> float | None:
    """读总公里数：OCR 顶部统计区，取带小数点的数字中框面积最大的（大号数字）。"""
    detail = context.run_recognition("WaitKm", image, pipeline_override=_KM_OVR)
    if not detail or not detail.all_results:
        return None
    candidates = []
    for r in detail.all_results:
        text = (getattr(r, "text", "") or "").replace("．", ".").replace("。", ".")
        m = _KM_RE.search(text)
        if not m:
            continue
        box = r.box
        candidates.append((box[2] * box[3], float(m.group(1))))
    if not candidates:
        return None
    candidates.sort(reverse=True)
    return candidates[0][1]


class WaitExerciseFinish(CustomAction):
    """轮询锻炼直到里程达标，同时做距离停滞检测。

    实测结论：锻炼不会自动结束（里程超过目标也一直计时），必须人工点停止键并
    在 60s 倒计时内确认。因此本动作只负责「等到达标」，达标后由 pipeline 的
    next（StopExercise 流程）完成停止+保存。

    每 poll_sec 秒截屏一次（纯被动，不点击，不惊动前台应用）：
    1. 总公里数 >= target_km -> 返回 True，next 进入 StopExercise（停止+保存）
    2. 总公里数在 stall_sec 内无增长且未达标 -> 模拟早死（模拟已取消/GPS 冻结），返回 False
    3. 「运动中」标题连续 blank_limit 次不到 -> 锻炼页面消失，返回 False
    4. 总等待超过 max_wait_sec -> 超时，返回 False

    返回 False 时 pipeline 节点的 on_error 应保持为空，让任务以失败终止。
    """

    def run(
        self,
        context: Context,
        argv: CustomAction.RunArg,
    ) -> bool:
        param = json.loads(argv.custom_action_param) if argv.custom_action_param else {}
        poll_sec = int(param.get("poll_sec", 30))
        max_wait_sec = int(param.get("max_wait_sec", 2700))
        target_km = float(param.get("target_km", 3.3))
        # 距离停滞判定窗：12km/h ≈ 0.2km/min，240s 应至少 +0.1km（读数精度 0.01 均覆盖）
        stall_sec = int(param.get("stall_sec", 240))
        blank_limit = int(param.get("blank_limit", 5))

        started = time.monotonic()
        last_km = None
        last_increase = None
        blank = 0

        print(
            f"[wait_exercise_finish] start target_km={target_km} poll_sec={poll_sec} "
            f"max_wait_sec={max_wait_sec} stall_sec={stall_sec} blank_limit={blank_limit}",
            flush=True,
        )

        while True:
            if context.tasker.stopping:
                print("[wait_exercise_finish] stopping requested", flush=True)
                return False

            now = time.monotonic()
            elapsed = int(now - started)
            if elapsed > max_wait_sec:
                print(f"[wait_exercise_finish] TIMEOUT after {elapsed}s last_km={last_km}", flush=True)
                return False

            image = context.tasker.controller.post_screencap().wait().get()

            running = context.run_recognition("WaitRunning", image, pipeline_override=_RUNNING_OVR)
            if running and running.hit:
                blank = 0
            else:
                blank += 1
                print(f"[wait_exercise_finish] 运动中 header not seen x{blank} elapsed={elapsed}s", flush=True)
                if blank >= blank_limit:
                    print("[wait_exercise_finish] exercise page gone, giving up", flush=True)
                    return False

            km = _scan_km(context, image)
            if km is not None:
                if km >= target_km:
                    print(f"[wait_exercise_finish] TARGET REACHED km={km} >= {target_km} elapsed={elapsed}s", flush=True)
                    return True
                # 未达标时距离必须持续增长：只有增长算活性信号，读数抖动一律忽略
                if last_km is None or km > last_km:
                    last_km = km
                    last_increase = now
                print(f"[wait_exercise_finish] km={km}/{target_km} elapsed={elapsed}s", flush=True)
            if km is None and last_increase is None:
                # 首次没读到数字时从现在起算，避免把 OCR 预热期误判成停滞
                last_increase = now

            if last_increase is not None and now - last_increase > stall_sec:
                print(
                    f"[wait_exercise_finish] DISTANCE STALLED {int(now - last_increase)}s "
                    f"at km={last_km} (<{target_km}) — 模拟疑似早死（模拟已取消/GPS 冻结）",
                    flush=True,
                )
                return False

            time.sleep(poll_sec)


if _IN_AGENT:
    WaitExerciseFinish = AgentServer.custom_action("wait_exercise_finish")(WaitExerciseFinish)
