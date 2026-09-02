#!/usr/bin/env python3
"""Run a pipeline task from assets/resource against a connected emulator.

Usage:
    python tools/run_task.py [entry] [adb_address]

    entry       pipeline node to run (default: FullRun)
    adb_address adb serial, e.g. 127.0.0.1:16384 (default: first device found)
"""
import os
import sys
from pathlib import Path

from maa.controller import AdbController
from maa.resource import Resource
from maa.tasker import Tasker
from maa.toolkit import Toolkit


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
        screencap_methods=device.screencap_methods,
        input_methods=device.input_methods,
        config=device.config,
    )
    controller.post_connection().wait()

    resource = Resource()
    resource.post_bundle(str(project_dir / "assets" / "resource")).wait()

    tasker = Tasker()
    tasker.bind(resource, controller)
    if not tasker.inited:
        print("[run] tasker init failed (resource/controller bind error)")
        return 1

    # Credentials are passed via environment only - never written to files/logs.
    pipeline_override = {}
    user = os.environ.get("MAF_USER")
    password = os.environ.get("MAF_PASS")
    if user:
        pipeline_override["SportLogin-CAS输入用户名"] = {"input_text": user}
    if password:
        pipeline_override["SportLogin-CAS输入密码"] = {"input_text": password}
    if pipeline_override:
        print(f"[run] credentials injected via env (user len={len(user or '')}, pass len={len(password or '')})")

    detail = tasker.post_task(entry, pipeline_override=pipeline_override).wait().get()
    status = detail and detail.status
    succeeded = bool(getattr(status, "succeeded", status))
    print(f"[run] task {entry} succeeded={succeeded}")
    return 0 if succeeded else 2


if __name__ == "__main__":
    sys.exit(main())
