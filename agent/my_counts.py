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
# 注意 MaaFW 的 roi 是 [x, y, w, h]（不是 x1,y1,x2,y2）：
_OCR_TOTAL = [0, 230, 340, 190]   # 课外锻炼页：评分次数大数字（含下方标签）
_OCR_WEEK = [0, 690, 340, 150]    # 锻炼记录周 tab：本周有效锻炼数大数字+标签
_WEEK_TAB = (120, 201)            # 周 tab 中心（720p）

_WEEK_LABEL = "本周有效锻炼数"
_TERM_LABEL = "本学期有效锻炼数"
_TOTAL_LABEL = "评分次数"
_HOME_MARK = "我的"

_DIGITS = re.compile(r"^(\d+)\s*次?$")

# 课外锻炼首次进入会先弹整页「跑步说明 / 安卓后台白名单设置方法」说明（实测 2026-11-17）。
# 该页只有大段文字，没有数字；探针需要先退出它才能读数。
_OVERLAY_MARKERS = ("后台运行", "白名单", "锁屏", "查杀")
_CONTINUE_TEXTS = (
    "我知道了",
    "我已知晓",
    "知道了",
    "已阅读",
    "开始锻炼",
    "开始跑步",
    "下一步",
    "同意",
    "确定",
)

# 上一次锻炼被中断（停止/异常退出）后，App 冷启动会弹「检测到有异常记录」+
# 「继续运动 / 去保存」。探针是只读的：两个选项都会改变业务状态，必须放弃读数，
# 留给随后的 FullRun 管道按原逻辑处理。
_ORPHAN_MARKERS = ("检测到有异常记录", "继续运动", "去保存")

# 运动中页面：App 已自动恢复上一次未结束的锻炼。探针绝不在这上面做任何点击。
_RUNNING_MARKERS = ("运动中", "总公里数", "时长")


def _orphan_prompt(results) -> bool:
    joined = _texts(results)
    return sum(1 for marker in _ORPHAN_MARKERS if marker in joined) >= 2


def _running_session(results) -> bool:
    joined = _texts(results)
    return sum(1 for marker in _RUNNING_MARKERS if marker in joined) >= 2


def _overlay_visible(results) -> bool:
    """说明页特征：同时出现多个后台保活相关词（≥2 个才算，避免误判正常页面）。"""
    joined = _texts(results)
    return sum(1 for marker in _OVERLAY_MARKERS if marker in joined) >= 2


def _find_any_box(results, needles):
    for needle in needles:
        for r in results:
            if needle in (getattr(r, "text", "") or ""):
                return r.box
    return None


# 导航点击绝不能命中的动作按钮字样：宁可读不到次数，也不能误点「开始锻炼」。
_UNSAFE_TAP_TOKENS = ("开始", "继续", "保存", "停止", "放弃", "确定", "同意")


def _safe_nav_box(results, needle: str):
    """找导航目标框：优先精确文本，其次排除动作按钮后的包含匹配。

    实测踩坑（2026-11-17 19:35）：`"课外锻炼" in text` 命中了「开始课外锻炼」
    大按钮，误开了一次锻炼。导航必须只点入口，不点启动按钮。
    """
    for r in results:
        if (getattr(r, "text", "") or "").strip() == needle:
            return r.box
    for r in results:
        text = (getattr(r, "text", "") or "").strip()
        if needle in text and not any(token in text for token in _UNSAFE_TAP_TOKENS):
            return r.box
    return None


def _ocr(context: Context, image, roi, only_rec: bool = False) -> list:
    """用 run_recognition + pipeline_override 做一次临时 OCR（同 my_wait._scan_km 手法）。"""
    node = {"recognition": "OCR", "roi": roi}
    if only_rec:
        # 只识别、不做检测：检测模型偶尔漏掉大号数字（实测「本周有效锻炼数」卡片），
        # 对紧裁剪区用 only_rec 可以绕过检测阶段。
        node["only_rec"] = True
    override = {"CountsOcr": node}
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


def _topmost_box(results, needle: str):
    """命中最靠上的文本框：锻炼记录页左列有两张卡都含「本周有效锻炼数」字样
    （上方是次数、下方是时长），次数卡在上。"""
    hits = [r.box for r in results if needle in (getattr(r, "text", "") or "")]
    return min(hits, key=lambda box: box[1]) if hits else None


def _week_tab_point(results) -> tuple[int, int] | None:
    """在锻炼记录页的 OCR 结果里找顶部「周」tab 的中心。

    优先用实时 OCR 坐标而不是写死常量：MuMu 不同版本/分辨率下 tab 行位置会漂移。
    只认单字「周」且位于顶部（y<260），避免命中「本周有效锻炼数」之类文本。
    """
    for r in results:
        text = (getattr(r, "text", "") or "").strip()
        if text == "周" and r.box[1] < 260:
            return (int(r.box[0] + r.box[2] / 2), int(r.box[1] + r.box[3] / 2))
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
    def _escape_notice(self, context: Context) -> bool:
        """退出「跑步说明 / 后台白名单」整页说明。

        先找常见继续按钮；找不到就上滑看页脚；仍没有则按 BACK 返回。
        返回 True 表示点到了按钮（页面可能已进入正常页），False 表示按了 BACK。
        """
        for _ in range(3):
            results = _ocr(context, self._shot(context), _OCR_FULL)
            box = _find_any_box(results, _CONTINUE_TEXTS)
            if box is not None:
                self._click(context, box[0] + box[2] / 2, box[1] + box[3] / 2)
                return True
            context.tasker.controller.post_swipe(360, 1000, 360, 480, 400).wait()
            time.sleep(1.5)
        context.tasker.controller.post_click_key(4).wait()
        time.sleep(2)
        return False

    def _read_total(self, context: Context) -> tuple[int | None, bool]:
        """课外锻炼页读「评分次数」大数字，返回 (total, abort)。

        abort=True 表示遇到登录验证或孤儿会话弹窗，探针必须整体放弃。
        命中标签但大数字 OCR 失败时继续重试——只 break 一次会把可恢复的
        识别抖动变成整轮探针失败。
        """
        deadline = time.monotonic() + 40
        escapes = 0
        while time.monotonic() < deadline:
            results = _ocr(context, self._shot(context), _OCR_FULL)
            if _orphan_prompt(results):
                print("[counts] failed=orphan_session_prompt", flush=True)
                return None, True
            if _running_session(results):
                print("[counts] failed=running_session_active", flush=True)
                return None, True
            if _overlay_visible(results) and escapes < 3:
                escapes += 1
                clicked = self._escape_notice(context)
                if not clicked:
                    # BACK 回到了上一页（主页/课外锻炼 landing），重新进入一次
                    self._tap_text(context, "课外锻炼", timeout_sec=10)
                continue
            if _find_box(results, "安全验证") or _find_box(results, "获取验证码"):
                print("[counts] failed=login_required", flush=True)
                return None, True
            if _find_box(results, _TOTAL_LABEL):
                total = _biggest_number(_ocr(context, self._shot(context), _OCR_TOTAL))
                if total is not None:
                    return total, False
            time.sleep(2)
        print("[counts] failed=total_not_found", flush=True)
        return None, False

    def _read_week(self, context: Context) -> int | None:
        """锻炼记录页读「本周有效锻炼数」，返回周次数。

        默认 tab 可能是月/学期，需要点周 tab 切换。实测（2026-11-16 18:18:50）
        页面切换动画会吞掉单次点击，所以按 ≥4s 间隔重试最多 6 次；
        仅当确认在锻炼记录页（标题「锻炼记录」位于顶部）时才点，避免误点其他页面。

        读数用「标签锚定」：先找到标签框，再对标签正上方的小块做 OCR；检测模型
        漏掉大号数字时改用 only_rec 直接识别。仍然失败则保存一张现场截图便于排查。
        """
        deadline = time.monotonic() + 60
        taps = 0
        last_tap = 0.0
        last_image = None
        saw_label = False
        while time.monotonic() < deadline:
            last_image = self._shot(context)
            results = _ocr(context, last_image, _OCR_FULL)
            label = _topmost_box(results, _WEEK_LABEL)
            if label is not None:
                saw_label = True
                week = self._week_number(context, last_image, label)
                if week is not None:
                    return week
            else:
                on_records = any(
                    "锻炼记录" in (getattr(r, "text", "") or "") and r.box[1] < 140
                    for r in results
                )
                if on_records and taps < 6 and time.monotonic() - last_tap >= 4:
                    point = _week_tab_point(results) or _WEEK_TAB
                    self._click(context, *point)
                    taps += 1
                    last_tap = time.monotonic()
                    continue
            time.sleep(2)
        if saw_label:
            self._dump(last_image, "week")
        print("[counts] failed=week_not_found", flush=True)
        return None

    def _week_number(self, context: Context, image, label) -> int | None:
        """读标签正上方的大数字：先检测 OCR，再 only_rec 兜底。"""
        x, y = label[0], label[1]
        left = max(0, x - 30)
        width = min(300, 720 - left)
        roi = [left, max(0, y - 95), width, 85]
        week = _biggest_number(_ocr(context, image, roi))
        if week is None:
            # 整卡片 ROI 再试一次（标签锚定的裁剪万一偏了）
            week = _biggest_number(_ocr(context, image, _OCR_WEEK))
        if week is not None:
            return week
        return _biggest_number(_ocr(context, image, roi, only_rec=True))

    def _dump(self, image, tag: str) -> None:
        """失败现场留一张截图（已 gitignore 的 debug/ 目录），便于离线排查 OCR。"""
        if image is None:
            return
        try:
            import cv2

            out = os.path.join(
                os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
                "debug",
                "debug",
                f"counts-{tag}-fail.png",
            )
            cv2.imwrite(out, image)
            print(f"[counts] debug screenshot -> {out}", flush=True)
        except Exception:  # noqa: BLE001 - 诊断失败不影响主流程
            pass

    def _read(self, context: Context) -> None:
        self._launch(context)
        if not self._to_home(context):
            return

        # 首页 -> 课外锻炼
        if not self._tap_text(context, "课外锻炼", timeout_sec=20):
            print("[counts] failed=home_entry_not_found", flush=True)
            return

        total, abort = self._read_total(context)
        if abort:
            return

        # 课外锻炼 -> 锻炼记录
        if not self._tap_text(context, "锻炼记录", timeout_sec=20):
            print(f"[counts] failed=records_entry_not_found total={total}", flush=True)
            return

        week = self._read_week(context)

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
        """在整屏 OCR 里找导航文本并点它的中心；超时返回 False。

        只认精确文本或非动作按钮的包含匹配（见 `_safe_nav_box`），避免误点
        「开始课外锻炼」这类会改变业务状态的按钮。
        """
        deadline = time.monotonic() + timeout_sec
        while time.monotonic() < deadline:
            box = _safe_nav_box(_ocr(context, self._shot(context), _OCR_FULL), text)
            if box:
                self._click(context, box[0] + box[2] / 2, box[1] + box[3] / 2)
                return True
            time.sleep(2)
        return False

    def _to_home(self, context: Context) -> bool:
        """冷启动可能落在记录详情页/发现页：按 BACK 逐步退回首页（首页锚点=底部「我的」）。

        返回 False 表示遇到必须放弃读数的状态（登录页 / 孤儿会话弹窗 / 超时）。
        """
        deadline = time.monotonic() + 90
        last = ""
        while time.monotonic() < deadline:
            results = _ocr(context, self._shot(context), _OCR_FULL)
            joined = _texts(results)
            if _orphan_prompt(results):
                print("[counts] failed=orphan_session_prompt", flush=True)
                return False
            if _running_session(results):
                print("[counts] failed=running_session_active", flush=True)
                return False
            if _overlay_visible(results):
                self._escape_notice(context)
                last = ""
                continue
            if _HOME_MARK in joined and "课外锻炼" in joined:
                return True
            if "隐私协议" in joined or ("登录" in joined and _HOME_MARK not in joined):
                print("[counts] failed=not_logged_in", flush=True)
                return False
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
        return False


if _IN_AGENT:
    ReadCountsAction = AgentServer.custom_action("read_counts")(ReadCountsAction)
