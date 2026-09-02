from maa.agent.agent_server import AgentServer
from maa.custom_recognition import CustomRecognition
from maa.context import Context


@AgentServer.custom_recognition("exercise_running")
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
