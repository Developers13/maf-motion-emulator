fn main() {
    // 把 requireAdministrator 清单嵌进 exe：MuMu / adb 脚本依赖管理员令牌，
    // 旧 WinForms 版是启动时 self-elevate，这里改为清单强制（决策见重写计划）。
    let attributes = tauri_build::Attributes::new().windows_attributes(
        tauri_build::WindowsAttributes::new().app_manifest(include_str!("app.manifest")),
    );
    tauri_build::try_build(attributes).expect("failed to run tauri-build");
}
