import json
import os
import subprocess
import sys
from pathlib import Path

# 批量/单跑入口：从项目根 user-list.json 读凭证（在内存与环境变量间流转，不落日志），
# 逐行调用 tools/run_task.py。用法：
#   python tools/e2e.py            # 跑全部行
#   python tools/e2e.py 2025302319 # 只跑指定学号
# 行格式：{ "student_id": "...", "password": "...", "distance_km": 3.2 }
# 注意：多开 MuMu 实例时 run_task.py 需要显式 adb 地址，届时再扩展此处。
project_dir = Path(__file__).parent.parent.resolve()
user_list_path = project_dir / "user-list.json"

only = sys.argv[1] if len(sys.argv) > 1 else None
rows = json.loads(user_list_path.read_text(encoding="utf-8"))
print(f"[e2e] {len(rows)} row(s) from {user_list_path.name}")

failed = []
for row in rows:
    sid = str(row["student_id"])
    if only and sid != only:
        continue
    laps = "6" if float(row.get("distance_km", 3.2)) < 3.2 else "8"
    target = "2.5" if laps == "6" else "3.3"
    env = dict(os.environ, MAF_USER=sid, MAF_PASS=row["password"], MAF_LAPS=laps, MAF_TARGET_KM=target)
    print(f"[e2e] === {sid} laps={laps} target_km={target} ===", flush=True)
    r = subprocess.run(
        [sys.executable, "tools/run_task.py", "FullRun"],
        env=env,
        cwd=str(project_dir),
    )
    status = "OK" if r.returncode == 0 else f"FAILED({r.returncode})"
    print(f"[e2e] {sid} -> {status}", flush=True)
    if r.returncode != 0:
        failed.append(sid)

print(f"[e2e] done. failed={failed or 'none'}")
sys.exit(1 if failed else 0)
