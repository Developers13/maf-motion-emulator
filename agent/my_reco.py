import os

from maa.custom_recognition import CustomRecognition
from maa.context import Context

# 装饰器仅 agent 子进程执行（见 my_wait.py 顶部说明）
_IN_AGENT = os.environ.get("MAF_AGENT_MODE") == "1"
if _IN_AGENT:
    from maa.agent.agent_server import AgentServer


class ExerciseRunning(CustomRecognition):
    """识别 课外锻炼 是否进行中：锻炼计时 MM:SS:XX 文本 + 总公里数 可读。

    用于 WaitExerciseFinish 轮询与 模拟已取消(公里数冻结) 检测。
    TODO: 实测后填充 OCR 区域与判定逻辑。
    """

    def analyze(
        self,
        context: Context,
        argv: CustomRecognition.AnalyzeArg,
    ) -> CustomRecognition.AnalyzeResult | None:

        # TODO: run_reco "exercise_running"（OCR/TemplateMatch）实现后返回命中框
        return None


if _IN_AGENT:
    ExerciseRunning = AgentServer.custom_recognition("exercise_running")(ExerciseRunning)
