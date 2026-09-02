from maa.agent.agent_server import AgentServer
from maa.custom_action import CustomAction
from maa.context import Context


@AgentServer.custom_action("stop_run")
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

        print(f"[stop_run] task failed at node: {argv.node_name}")
        # TODO: 清理模拟器状态（停止模拟/结束锻炼），保持幂等
        return True
