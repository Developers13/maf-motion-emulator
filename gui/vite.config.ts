import { defineConfig } from "vite";

// Tauri 需要固定端口（strictPort），否则 devUrl 与 Vite 实际端口不一致会白屏。
export default defineConfig({
  clearScreen: false,
  server: {
    port: 5173,
    strictPort: true,
    watch: {
      // Chrome 截图 profile 与产物目录会被外部进程锁住，Vite 监听它们会 EBUSY 崩掉。
      ignored: ["**/.taste-skill/**", "**/shots/**", "**/src-tauri/target/**"],
    },
  },
  build: {
    target: "chrome105",
    outDir: "dist",
  },
});
