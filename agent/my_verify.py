import json
import os
import time

from maa.custom_action import CustomAction
from maa.context import Context

# 装饰器仅 agent 子进程执行（见 my_wait.py 顶部说明）
_IN_AGENT = os.environ.get("MAF_AGENT_MODE") == "1"
if _IN_AGENT:
    from maa.agent.agent_server import AgentServer

MANUAL_VERIFY = "wait_manual_verify"

# 风控弹窗锚点（720p 全屏 OR）：标题「安全验证」+ 正文关键词。
# 实测文案：安全验证 / 当前登录环境异常，需通过安全验证确认是本人操作... /
#           使用安全手机验证 / 获取验证码
VERIFY_KEYWORDS = ["安全验证", "当前登录环境异常", "获取验证码", "使用安全手机验证"]

_VERIFY_OVR = {
    "VerifyDialog": {
        "recognition": "OCR",
        "expected": VERIFY_KEYWORDS,
        "roi": [0, 0, 720, 1280],
    }
}

# 同一次运行内的验证轮次上限：弹窗若在提交后「秒现」，登录链会重放——
# 人工处理场景下不会频繁触发，超过上限说明不是人工能解的状态，直接失败退出。
_MAX_ROUNDS = 5
_rounds = 0


def _dialog_text(context: Context, image) -> str | None:
    """返回命中的弹窗文案；未命中返回 None。"""
    detail = context.run_recognition("VerifyDialog", image, pipeline_override=_VERIFY_OVR)
    if not detail or not detail.hit:
        return None
    texts = [getattr(r, "text", "") or "" for r in (detail.all_results or [])]
    joined = " ".join(t for t in texts if t)
    return joined or "hit"


class WaitManualVerify(CustomAction):
    """挂起等待人工完成 CAS 安全验证（短信验证码），完成后续跑，而不是失败退出。

    背景（实测）：同一账号连续登录会触发 CAS 风控，提交后不返回主页，而是弹
    「安全验证 / 当前登录环境异常 / 请选择安全验证方式」。此时后继节点（已登录主页）
    永远匹配不上，节点超时 -> 整链失败退出；而用户其实只需在模拟器上收个验证码点两下。

    本动作检测到弹窗后：
    1. 打印醒目提示，告诉用户去模拟器上完成验证；
    2. 纯被动轮询（只截图 + OCR，**不点击**——绝不代点「获取验证码」触发短信）；
    3. 弹窗消失并连续 confirm_times 次不再出现 -> 返回 True，由 pipeline 的 next
       回到登录分支收尾（已登录则进主页，未登录则重走登录）。

    超时（默认 max_wait_sec）或 GUI 请求停止 -> 返回 False，任务失败退出，不做无限等待。
    """

    def run(
        self,
        context: Context,
        argv: CustomAction.RunArg,
    ) -> bool:
        global _rounds

        param = json.loads(argv.custom_action_param) if argv.custom_action_param else {}
        poll_sec = int(param.get("poll_sec", 5))
        max_wait_sec = int(param.get("max_wait_sec", 1200))
        confirm_times = int(param.get("confirm_times", 2))
        log_every_sec = int(param.get("log_every_sec", 30))

        _rounds += 1
        if _rounds > _MAX_ROUNDS:
            print(
                f"[manual_verify] round {_rounds} exceeds limit {_MAX_ROUNDS} — "
                f"verification keeps reappearing, giving up",
                flush=True,
            )
            return False

        print("=" * 64, flush=True)
        print("[manual_verify] SECURITY VERIFICATION REQUIRED", flush=True)
        print("[manual_verify] The school CAS page is asking to verify your identity", flush=True)
        print("[manual_verify] (usually an SMS code). Complete it on the emulator now.", flush=True)
        print("[manual_verify] The flow is PAUSED and resumes by itself when done.", flush=True)
        print(
            f"[manual_verify] Waiting up to {max_wait_sec}s "
            f"(round {_rounds}/{_MAX_ROUNDS}, node {argv.node_name})",
            flush=True,
        )
        print("=" * 64, flush=True)

        started = time.monotonic()
        next_log = 0.0
        gone = 0
        cap_fail = 0
        last_seen = "?"

        while True:
            if context.tasker.stopping:
                print("[manual_verify] stop requested; aborting wait", flush=True)
                return False

            elapsed = time.monotonic() - started
            if elapsed > max_wait_sec:
                print(
                    f"[manual_verify] TIMEOUT after {int(elapsed)}s, dialog still present "
                    f"(detected: {last_seen})",
                    flush=True,
                )
                return False

            image = context.tasker.controller.post_screencap().wait().get()
            if image is None:
                # 截屏失败（如显示器 id 过期的瞬间）：既不能算检测到，也不能算消失
                # （否则 confirm_times 次误判后会提前认定「用户已完成验证」并恢复流程），
                # 只跳过本轮重试；连续失败仅在首次打一行，避免刷屏。
                cap_fail += 1
                if cap_fail == 1:
                    print("[manual_verify] screencap failed; retrying quietly", flush=True)
                time.sleep(poll_sec)
                continue
            cap_fail = 0
            seen = _dialog_text(context, image)

            if seen is not None:
                gone = 0
                last_seen = seen
                if elapsed >= next_log:
                    print(
                        f"[manual_verify] waiting for user... elapsed={int(elapsed)}s "
                        f"detected=\"{seen}\"",
                        flush=True,
                    )
                    next_log = elapsed + log_every_sec
            else:
                gone += 1
                print(
                    f"[manual_verify] dialog not detected x{gone}/{confirm_times} "
                    f"elapsed={int(elapsed)}s",
                    flush=True,
                )
                if gone >= confirm_times:
                    print(
                        f"[manual_verify] verification done after {int(elapsed)}s; resuming flow",
                        flush=True,
                    )
                    return True
                next_log = 0.0

            time.sleep(poll_sec)


if _IN_AGENT:
    WaitManualVerify = AgentServer.custom_action(MANUAL_VERIFY)(WaitManualVerify)
