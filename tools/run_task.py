#!/usr/bin/env python3
"""Run a pipeline task from assets/resource against a connected emulator.

Usage:
    python tools/run_task.py [entry] [adb_address]

    entry       pipeline node to run (default: FullRun)
    adb_address adb serial, e.g. 127.0.0.1:16384 (default: first device found)
"""
import os
import sys
import time
from pathlib import Path

from maa.controller import AdbController
from maa.define import MaaAdbScreencapMethodEnum
from maa.resource import Resource
from maa.tasker import Tasker
from maa.toolkit import Toolkit

# 截图方式：EmulatorExtras（MuMu SDK）优先，另加 adb 兜底。
#
# 背景：MaaToolkit 给 MuMu 设备只配 EmulatorExtras 一种方式，而 MuMu 的 display 是动态
# 分配销毁的（每次启动 app 都可能落到新的 display id 上）。一旦 extras 解析出的 display
# id 失效（实测 ret=4），截图整体失败，速度测试又找不到别的方式，控制器直接报废
# （"No available screencap method"），整轮任务作废。
#
# 补上 adb 系方式后：extras 正常时仍然最快、优先使用；extras 失效时自动降级到 adb 截图，
# 任务继续跑而不是中断。adb 截图在 MuMu 上跟随前台 app 的 display（实测），坐标体系一致。
SCREENCAP_METHODS = (
    int(MaaAdbScreencapMethodEnum.EmulatorExtras)
    | int(MaaAdbScreencapMethodEnum.Encode)
    | int(MaaAdbScreencapMethodEnum.EncodeToFileAndPull)
)


def check_screencap(controller: AdbController, attempts: int = 3) -> bool:
    """连接后先确认截图可用，避免登录/操作到一半才发现控制器是废的。"""
    for i in range(attempts):
        image = controller.post_screencap().wait().get()
        if image is not None:
            print(f"[run] screencap ok {image.shape[1]}x{image.shape[0]}")
            return True
        print(f"[run] screencap failed ({i + 1}/{attempts}), retrying...")
        time.sleep(1.5)
    return False


def main() -> int:
    entry = sys.argv[1] if len(sys.argv) > 1 else "FullRun"
    wanted_address = sys.argv[2] if len(sys.argv) > 2 else None

    project_dir = Path(__file__).parent.parent.resolve()
    Toolkit.init_option(str(project_dir / "debug"))

    devices = Toolkit.find_adb_devices()
    if not devices:
        print("No ADB device found. Is the emulator running?")
        return 1
    device = devices[0]
    if wanted_address:
        matched = [d for d in devices if d.address == wanted_address]
        if not matched:
            print(f"Device {wanted_address} not found. Found: {[d.address for d in devices]}")
            return 1
        device = matched[0]
    print(f"[run] adb={device.address}")

    controller = AdbController(
        adb_path=device.adb_path,
        address=device.address,
        screencap_methods=SCREENCAP_METHODS,
        input_methods=device.input_methods,
        config=device.config,
    )
    controller.post_connection().wait()
    if not check_screencap(controller):
        print("[run] no working screencap method for this device; aborting before touching the account")
        return 1

    resource = Resource()
    resource.post_bundle(str(project_dir / "assets" / "resource")).wait()

    # agent 自定义识别/动作进程内注册（不启 agent 子进程；MFAAvalonia 走 interface.json 的 agent 通道）
    sys.path.insert(0, str(project_dir / "agent"))
    import my_action
    import my_counts
    import my_reco
    import my_verify
    import my_wait

    resource.register_custom_action("wait_exercise_finish", my_wait.WaitExerciseFinish())
    resource.register_custom_action("wait_manual_verify", my_verify.WaitManualVerify())
    resource.register_custom_action("cas_input_password", my_action.CasInputPassword())
    resource.register_custom_action("stop_run", my_action.StopRun())
    resource.register_custom_action("read_counts", my_counts.ReadCountsAction())
    resource.register_custom_recognition("resumed_orphan", my_reco.ResumedOrphan())

    tasker = Tasker()
    tasker.bind(resource, controller)
    if not tasker.inited:
        print("[run] tasker init failed (resource/controller bind error)")
        return 1

    # Credentials are passed via environment only - never written to files/logs.
    # 密码不走 pipeline_override：maafw 会把 override 连明文写进 maafw.log（实测泄漏
    # 2026-11-08），改由 custom action cas_input_password 在设备侧直接读 MAF_PASS 键入。
    pipeline_override = {}
    user = os.environ.get("MAF_USER")
    password = os.environ.get("MAF_PASS")  # 仅被 my_action.CasInputPassword 消费
    if user:
        pipeline_override["SportLogin-CAS输入用户名"] = {"input_text": user}
    laps = os.environ.get("MAF_LAPS")
    if laps:
        pipeline_override["StartEmulation-输入圈数"] = {"input_text": laps}
    target_km = os.environ.get("MAF_TARGET_KM")
    if target_km:
        pipeline_override["WaitExerciseFinish"] = {"custom_action_param": {"target_km": float(target_km)}}
    if pipeline_override:
        print(f"[run] overrides via env (user={bool(user)}, pass={bool(password)}, laps={laps or 'default 8'}, target_km={target_km or 'default 3.3'})")

    detail = tasker.post_task(entry, pipeline_override=pipeline_override).wait().get()
    status = detail and detail.status
    succeeded = bool(getattr(status, "succeeded", status))

    # 清理责任：成功后模拟已完成使命（记录已保存），停掉模拟器 app；
    # 失败时强停两个 app —— 体育 app 不留孤儿锻炼（计时会一直跑），模拟不留假活状态。
    # 失败现场截图由 MaaFW 自动保存在 debug/debug/on_error/。
    if entry == "FullRun":
        try:
            if succeeded:
                controller.post_stop_app("com.zhufucdev.motion_emulator").wait()
                print("[run] cleanup: stopped com.zhufucdev.motion_emulator")
            else:
                controller.post_stop_app("net.crigh.xbgysport").wait()
                controller.post_stop_app("com.zhufucdev.motion_emulator").wait()
                print("[run] cleanup: stopped sport + emulator apps")
        except Exception as e:
            print(f"[run] cleanup failed: {e}")
    elif entry == "ReadCounts":
        # 读数探针停在锻炼记录页；停掉体育 app，让随后的 FullRun 仍从冷启动走原路径。
        try:
            controller.post_stop_app("net.crigh.xbgysport").wait()
            print("[run] cleanup: stopped sport app after counts probe")
        except Exception as e:
            print(f"[run] cleanup failed: {e}")

    print(f"[run] task {entry} succeeded={succeeded}")
    return 0 if succeeded else 2


if __name__ == "__main__":
    sys.exit(main())
