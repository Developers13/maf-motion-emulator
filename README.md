# MAF Motion Emulator

西工大体育课外锻炼模拟助手：[Motion Emulator](https://github.com/zhufucdev/MotionEmulator) 路线模拟 + 西工大体育课外锻炼打卡，跑在 MuMu 等 Android 模拟器上。由 **[MaaFramework](https://github.com/MaaXYZ/MaaFramework)** 强力驱动！

> 前身是基于 PowerShell/adb 的 `motion-emulate-automation` skill；本项目为 MaaFramework 重写版——图像识别驱动、Pipeline 低代码、自带通用 UI 与 CI/CD。

## 功能

- **登录西工大体育**：隐私协议 → 引导页 → CAS 统一身份认证（账号 = 学号，密码仅内存传递）
- **启动运动模拟**：Motion Emulator 滚动到模拟卡 → 校验路线 → 设置重复次数（2.4km→6 / 3.2km→8）→ 开始模拟
- **开始课外锻炼**：课外锻炼 → 场地选择（长安校区星天苑田径场）→ 确认运动中
- **等待锻炼结束**：通知心跳轮询 + 公里数冻结检测（防「模拟已取消」静默死亡）

## 项目结构

```tree
maf-motion-emulator/
├── .github/workflows/       # CI/CD（check 校验 / install 打包发版）
├── agent/                   # Python Agent：自定义识别/动作
├── assets/
│   ├── interface.json       # ProjectInterface V2（通用 UI 入口）
│   └── resource/
│       ├── image/           # 模板匹配图像素材（720p 缩放后裁剪）
│       ├── model/ocr/       # OCR 模型（不入库，CI 自动配置）
│       └── pipeline/        # 任务流水线（低代码逻辑）
├── deps/tools/              # JSON Schema（校验用）
└── tools/                   # install.py 打包 / validate_schema.py 校验
```

## 本地开发

1. 安装 Python 绑定：`pip install maafw`（调试器：`pip install MaaDebugger`）
2. 准备 OCR 模型（二选一）：
   - 下载 [ppocr_v6-small.zip](https://download.maafw.xyz/MaaCommonAssets/OCR/ppocr_v6/ppocr_v6-small.zip) 解压到 `assets/resource/model/ocr/`
   - 或添加子模块：`git submodule update --init assets/MaaCommonAssets`
3. VSCode 安装 [maa-support 插件](https://marketplace.visualstudio.com/items?itemName=nekosu.maa-support)，基于 `interface.json` 断点调试 pipeline
4. 用 MaaDebugger / 通用 UI 连接模拟器 adb（MuMu 默认 `127.0.0.1:16384 + 32×实例序号`）实测截图，裁剪素材到 `assets/resource/image/`

## CI/CD

- **check**（push/PR）：`@nekosu/maa-tools check` + JSON Schema 校验（pipeline / interface）
- **install**（push/PR → CI 产物；`v*` tag → 自动 GitHub Release）：
  下载 MaaFramework + MFAAvalonia release → 组装 4 OS × 2 arch 安装包 → git-cliff 生成 changelog

发版流程：

```bash
git tag v0.1.0
git push origin v0.1.0
```

> 首次使用需在仓库设置中开启 `Settings → Actions → General → Read and write permissions`。

## License

待定（MaaFramework 为 LGPL-3.0，对其的修改需遵循该许可证；本项目代码许可证另定）。
