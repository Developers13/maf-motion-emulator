import os
import re

from maa.custom_recognition import CustomRecognition
from maa.context import Context

# 装饰器仅 agent 子进程执行（见 my_wait.py 顶部说明）
_IN_AGENT = os.environ.get("MAF_AGENT_MODE") == "1"
if _IN_AGENT:
    from maa.agent.agent_server import AgentServer

_STATS_ROI = [0, 90, 720, 250]

_RUNNING_OVR = {
    "ResumedRunning": {"recognition": "OCR", "roi": [0, 0, 720, 1280], "expected": ["运动中", "暂停"]}
}
_STATS_OVR = {
    "ResumedStats": {"recognition": "OCR", "roi": _STATS_ROI}
}

# 时长 00:15:26（H:MM:SS）或 15:26（MM:SS）；公里 2.22
_TIMER_RE = re.compile(r"(\d{1,2}):([0-5]\d)(?::([0-5]\d))?")
_KM_RE = re.compile(r"(\d+\.\d+)")

# 新开锻炼的判定阈值：刚点完场地确认时 时长 <1min、公里 <0.1。
# 时长 >=4 min 或 公里 >=0.3 必然是残留锻炼（含 强停后 app 恢复 的孤儿）
_MIN_MINUTES = 4
_MIN_KM = 0.3


class ResumedOrphan(CustomRecognition):
    """识别「续跑孤儿」：运动中页面来自被中断残留的锻炼，而非本次新开。

    背景（实测）：强停体育 app 会留下进行中锻炼；重新开始锻炼时 app 可能弹
    「继续运动」把残留续上（计时保留、含空档），产生被污染的记录。
    在 运动确认 之前调用本识别：命中即转 停止保存 重新开始 链路。
    """

    def analyze(
        self,
        context: Context,
        argv: CustomRecognition.AnalyzeArg,
    ) -> CustomRecognition.AnalyzeResult | None:

        running = context.run_recognition("ResumedRunning", argv.image, pipeline_override=_RUNNING_OVR)
        if not running or not running.hit:
            return None

        stats = context.run_recognition("ResumedStats", argv.image, pipeline_override=_STATS_OVR)
        if not stats or not stats.all_results:
            return None

        joined = " ".join((getattr(r, "text", "") or "") for r in stats.all_results)

        minutes = 0.0
        for m in _TIMER_RE.finditer(joined):
            if m.group(3) is not None:  # H:MM:SS
                minutes = max(minutes, int(m.group(1)) * 60 + int(m.group(2)) + int(m.group(3)) / 60)
            else:  # MM:SS
                minutes = max(minutes, int(m.group(1)) + int(m.group(2)) / 60)

        km = 0.0
        for m in _KM_RE.finditer(joined):
            km = max(km, float(m.group(1)))

        # _TIMER_RE 只认冒号分隔（时长 00:15:26），配速 06'56''（撇号）不会误命中
        if minutes >= _MIN_MINUTES or km >= _MIN_KM:
            return CustomRecognition.AnalyzeResult(
                box=(0, 0, 720, 1280),
                detail=f"resumed orphan: minutes={minutes:.1f} km={km}",
            )
        return None


if _IN_AGENT:
    ResumedOrphan = AgentServer.custom_recognition("resumed_orphan")(ResumedOrphan)
