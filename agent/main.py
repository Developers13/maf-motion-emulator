import os
import sys

# 必须先于 my_* 导入：AgentServer 装饰器仅应在 agent 子进程执行（见 my_wait.py 顶部说明）
os.environ["MAF_AGENT_MODE"] = "1"

from maa.agent.agent_server import AgentServer
from maa.toolkit import Toolkit

import my_action
import my_counts
import my_reco
import my_verify
import my_wait


def main():
    Toolkit.init_option("./")

    if len(sys.argv) < 2:
        print("Usage: python main.py <socket_id>")
        print("socket_id is provided by AgentIdentifier.")
        sys.exit(1)

    socket_id = sys.argv[-1]

    AgentServer.start_up(socket_id)
    AgentServer.join()
    AgentServer.shut_down()


if __name__ == "__main__":
    main()
