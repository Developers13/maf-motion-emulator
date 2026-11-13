import os

from maa.custom_action import CustomAction
from maa.context import Context

# 装饰器仅 agent 子进程执行（见 my_wait.py 顶部说明）
_IN_AGENT = os.environ.get("MAF_AGENT_MODE") == "1"
if _IN_AGENT:
    from maa.agent.agent_server import AgentServer


class StopRun(CustomAction):
    """失败时的统一处置：上报错误并终止当前任务链。

    在 pipeline 中任何节点的 on_error 里引用：
        "on_error": { "action": "Custom", "custom_action": "stop_run" }
    """

    def run(
        self,
        context: Context,
        argv: CustomAction.RunArg,
    ) -> bool:

        print(f"[stop_run] task failed at node: {argv.node_name}", flush=True)
        # TODO: 清理模拟器状态（停止模拟/结束锻炼），保持幂等
        return True


class CasInputPassword(CustomAction):
    """CAS 登录页输入密码。

    密码只从环境变量 MAF_PASS 读取，绝不放进 pipeline_override —— maafw 会把
    post_task 的 override 连明文写进 maafw.log（实测泄漏，2026-11-08）。
    键入走 controller.post_input_text（文本不落日志），输入当前聚焦的密码框。
    对应 pipeline 节点：SportLogin-CAS输入密码（action: Custom + custom_registration）。
    """

    def run(
        self,
        context: Context,
        argv: CustomAction.RunArg,
    ) -> bool:
        password = os.environ.get("MAF_PASS", "")
        if password:
            context.tasker.controller.post_input_text(password).wait()
        else:
            print("[cas_input_password] MAF_PASS not set; typing nothing", flush=True)
        return True


if _IN_AGENT:
    StopRun = AgentServer.custom_action("stop_run")(StopRun)
