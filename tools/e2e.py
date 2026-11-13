import json
import os
import re
import subprocess
import sys
from pathlib import Path

# 批量/单跑入口：从项目根 user-list.json 读凭证（在内存与环境变量间流转，不落日志），
# 逐行调用 tools/run_task.py。用法：
#   python tools/e2e.py                 # 跑全部行
#   python tools/e2e.py 2025302319      # 只跑指定学号
#   python tools/e2e.py --dry-run       # 只解析并打印每个学号的目标 adb 地址，不执行
# 行格式：{ "student_id": "...", "password": "...", "distance_km": 3.2 }
#
# 多开 MuMu 实例时，run_task.py 不带 adb 地址会退化为「第一个设备」（devices[0]），
# 导致第二个学号仍在第一个学号的实例上操作。因此这里按实例名（= 学号）解析该学号
# 对应的 adb 端口并显式传给 run_task.py。实例须已启动（见「运行前准备」/ mumu.ps1 ensure）。
project_dir = Path(__file__).parent.parent.resolve()
user_list_path = project_dir / "user-list.json"

# MuMu 管理 CLI；可用环境变量覆盖（GUI 会把设置页的路径通过该变量传入）。
MUMU_MANAGER = os.environ.get(
    "MAF_MUMU_MANAGER", r"C:\Program Files\Netease\MuMu\nx_main\MuMuManager.exe"
)


def resolve_adb_address(student_id: str):
    """按实例名（= 学号）解析该学生模拟器的 adb 地址。

    返回 (address, error)；address 形如 "127.0.0.1:16384"。
    """
    if not Path(MUMU_MANAGER).exists():
        return None, f"MuMu manager not found: {MUMU_MANAGER}"

    try:
        proc = subprocess.run(
            [MUMU_MANAGER, "info", "-v", "all"],
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",
            timeout=60,
        )
    except Exception as e:  # noqa: BLE001 - 启动/超时失败都归为解析失败
        return None, f"mumu info failed: {e}"

    raw = proc.stdout or ""
    # 容错：MuMuManager 正常只输出 JSON，但若夹带其它行则截取首尾大括号之间部分。
    start, end = raw.find("{"), raw.rfind("}")
    if start < 0 or end <= start:
        return None, "cannot parse mumu info (no JSON found)"

    try:
        info = json.loads(raw[start : end + 1])
    except Exception as e:  # noqa: BLE001
        return None, f"cannot parse mumu info: {e}"

    for index, inst in info.items():
        if str(inst.get("name", "")) != student_id:
            continue
        port = inst.get("adb_port")
        if not port:
            return None, (
                f"instance '{student_id}' (index {index}) is not running (no adb_port); "
                f"run the pre-run prep first"
            )
        return f"127.0.0.1:{port}", None

    names = [str(i.get("name", "")) for i in info.values()]
    return None, f"no MuMu instance named '{student_id}' (have: {names})"


args = sys.argv[1:]
dry_run = "--dry-run" in args
# 只有批量运行（GUI 传 --skip-if-done）才在达到上限时跳过；手动单选始终强制运行。
skip_if_done = "--skip-if-done" in args
only = next((a for a in args if not a.startswith("-")), None)

# 阈值由 GUI 通过环境变量下发（设置页可改），缺省与学校规则一致：每周 5 次 / 一学期 32 次。
WEEK_LIMIT = int(os.environ.get("MAF_WEEK_LIMIT", "5"))
TOTAL_LIMIT = int(os.environ.get("MAF_TOTAL_LIMIT", "32"))

_COUNTS_RE = re.compile(r"\[counts\]\s+week=(-?\d+)\s+total=(-?\d+)")


def probe_counts(address: str, timeout_sec: int = 240):
    """跑一次 ReadCounts 探针，返回 (week, total)；任一读不到返回 None。

    探针自身 fail-open（读不到也退出 0），这里再兜一层超时/异常——读数失败
    绝不能阻断正式运行。
    """
    try:
        r = subprocess.run(
            [sys.executable, "tools/run_task.py", "ReadCounts", address],
            cwd=str(project_dir),
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",
            timeout=timeout_sec,
        )
    except Exception as e:  # noqa: BLE001
        print(f"[e2e] counts probe failed: {type(e).__name__}: {e}", flush=True)
        return None

    for line in (r.stdout or "").splitlines():
        m = _COUNTS_RE.search(line)
        if m:
            week, total = int(m.group(1)), int(m.group(2))
            if week >= 0 and total >= 0:
                return week, total
    tail = ((r.stdout or "").strip().splitlines() or ["<no stdout>"])[-1]
    print(f"[e2e] counts probe gave no result (exit {r.returncode}): {tail}", flush=True)
    return None

rows = json.loads(user_list_path.read_text(encoding="utf-8"))
print(f"[e2e] {len(rows)} row(s) from {user_list_path.name}")

# 指定了学号但 user-list 中没有：明确报错。静默跳过会伪装成「跑完了」。
if only is not None:
    known = [str(r["student_id"]) for r in rows]
    if only not in known:
        print(f"[e2e] FAILED: student '{only}' not in {user_list_path.name} (have: {known})")
        sys.exit(1)

failed = []
skipped = []
for row in rows:
    sid = str(row["student_id"])
    if only and sid != only:
        continue

    laps = "6" if float(row.get("distance_km", 3.2)) < 3.2 else "8"
    target = "2.5" if laps == "6" else "3.3"

    # 关键：每个学号用自己的实例地址，避免多开时操作错机器。
    address, error = resolve_adb_address(sid)
    if error:
        print(f"[e2e] {sid} -> FAILED: {error}", flush=True)
        failed.append(sid)
        continue

    print(f"[e2e] === {sid} laps={laps} target_km={target} adb={address} ===", flush=True)

    if dry_run:
        continue

    # 跑前读 App 端次数：达到上限的学号在批量里直接跳过，省掉一次完整模拟。
    # 读数失败 = 状态未知，按用户的决定放行（宁可多跑一次，不可误跳过）。
    counts = probe_counts(address)
    if counts is None:
        print(f"[e2e] COUNTS sid={sid} unknown", flush=True)
    else:
        week, total = counts
        print(f"[e2e] COUNTS sid={sid} week={week} total={total}", flush=True)
        if skip_if_done and (week >= WEEK_LIMIT or total >= TOTAL_LIMIT):
            reason = "week" if week >= WEEK_LIMIT else "total"
            print(
                f"[e2e] SKIP sid={sid} reason={reason} "
                f"week={week}/{WEEK_LIMIT} total={total}/{TOTAL_LIMIT}",
                flush=True,
            )
            print(f"[e2e] {sid} -> SKIPPED", flush=True)
            skipped.append(sid)
            continue

    env = dict(
        os.environ,
        MAF_USER=sid,
        MAF_PASS=row["password"],
        MAF_LAPS=laps,
        MAF_TARGET_KM=target,
    )
    r = subprocess.run(
        [sys.executable, "tools/run_task.py", "FullRun", address],
        env=env,
        cwd=str(project_dir),
    )
    status = "OK" if r.returncode == 0 else f"FAILED({r.returncode})"
    print(f"[e2e] {sid} -> {status}", flush=True)
    if r.returncode != 0:
        failed.append(sid)

print(f"[e2e] done. failed={failed or 'none'} skipped={skipped or 'none'}")
if failed:
    sys.exit(1)
if skipped:
    print("[e2e] all remaining students already at weekly/total cap", flush=True)
    sys.exit(3)
