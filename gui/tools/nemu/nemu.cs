// nemu.exe - thin CLI over the MuMu external_renderer_ipc SDK (nemu_*).
// Bypasses adb entirely: taps/text/screenshots are injected directly into a
// MuMu instance and auto-target the display the given app package lives on
// (nemu_get_display_id) - this solves the "secondary display unreachable via
// adb input" problem.
//
// Interface modeled after the public MuMu SDK (as used by MaaFramework's
// MuMuPlayerExtras) but implemented standalone from the documented exports.
//
// Usage:
//   nemu.exe <instance_index> display <package>
//   nemu.exe <instance_index> tap     <package> <x> <y>
//   nemu.exe <instance_index> text    <package> <utf8_text>
//   nemu.exe <instance_index> swipe   <package> <x1> <y1> <x2> <y2> <ms>
//   nemu.exe <instance_index> key     <package> <android_keycode>
//   nemu.exe <instance_index> shot    <package> <out.png>
//
// <package> = the Android package whose display to target, or "default" for
// the main display. <instance_index> = MuMu instance index (mumu.ps1 info).
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace NemuCli
{
    class Program
    {
        // Load the SDK DLL by absolute path and bind exports via GetProcAddress
        // (avoids DllImport search-path and string-marshalling pitfalls).

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr LoadLibrary(string path);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr GetProcAddress(IntPtr hModule, string procName);
        [DllImport("kernel32.dll")]
        static extern bool FreeLibrary(IntPtr hModule);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        delegate int ConnectFn(string path, int index);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int DisconnectFn(int handle);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int GetDisplayIdFn(int handle, IntPtr packageAnsi, int clonedIndex);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int CaptureDisplayFn(int handle, int displayId, int size, ref int width, ref int height, IntPtr buffer);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int InputTextFn(int handle, int len, IntPtr utf8Text);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int TouchDownFn(int handle, int displayId, int contact, int x, int y);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int TouchUpFn(int handle, int displayId, int contact);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int KeyDownFn(int handle, int displayId, int key);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int KeyUpFn(int handle, int displayId, int key);

        static ConnectFn nemu_connect;
        static DisconnectFn nemu_disconnect;
        static GetDisplayIdFn nemu_get_display_id;
        static CaptureDisplayFn nemu_capture_display;
        static InputTextFn nemu_input_text;
        static TouchDownFn nemu_input_event_finger_touch_down;
        static TouchUpFn nemu_input_event_finger_touch_up;
        static KeyDownFn nemu_input_event_key_down;
        static KeyUpFn nemu_input_event_key_up;
        static IntPtr _lib;

        static IntPtr Ansi(string s)
        {
            byte[] b = Encoding.ASCII.GetBytes(s + "\0");
            IntPtr p = Marshal.AllocHGlobal(b.Length);
            Marshal.Copy(b, 0, p, b.Length);
            return p;
        }

        static bool Bind(string libPath, out string err)
        {
            err = null;
            _lib = LoadLibrary(libPath);
            if (_lib == IntPtr.Zero)
            {
                err = "LoadLibrary failed: " + Marshal.GetLastWin32Error();
                return false;
            }
            nemu_connect = Get<ConnectFn>("nemu_connect");
            nemu_disconnect = Get<DisconnectFn>("nemu_disconnect");
            nemu_get_display_id = Get<GetDisplayIdFn>("nemu_get_display_id");
            nemu_capture_display = Get<CaptureDisplayFn>("nemu_capture_display");
            nemu_input_text = Get<InputTextFn>("nemu_input_text");
            nemu_input_event_finger_touch_down = Get<TouchDownFn>("nemu_input_event_finger_touch_down");
            nemu_input_event_finger_touch_up = Get<TouchUpFn>("nemu_input_event_finger_touch_up");
            nemu_input_event_key_down = Get<KeyDownFn>("nemu_input_event_key_down");
            nemu_input_event_key_up = Get<KeyUpFn>("nemu_input_event_key_up");
            if (nemu_connect == null || nemu_disconnect == null || nemu_get_display_id == null ||
                nemu_capture_display == null || nemu_input_text == null ||
                nemu_input_event_finger_touch_down == null || nemu_input_event_finger_touch_up == null ||
                nemu_input_event_key_down == null || nemu_input_event_key_up == null)
            {
                err = "not all nemu_* exports found";
                return false;
            }
            return true;
        }

        static T Get<T>(string name) where T : class
        {
            IntPtr p = GetProcAddress(_lib, name);
            return p == IntPtr.Zero ? null : (T)(object)Marshal.GetDelegateForFunctionPointer(p, typeof(T));
        }

        static int ExitErr(string msg) { Console.Error.WriteLine("nemu: " + msg); return 1; }

        static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            if (args.Length < 2) return ExitErr("usage: nemu.exe <index> <display|tap|text|swipe|key|shot> ...");

            int index;
            if (!int.TryParse(args[0], out index)) return ExitErr("bad instance index: " + args[0]);
            string cmd = args[1];

            // Locate the MuMu SDK DLL. installDir is the MuMu install ROOT
            // (the dir containing nx_main) - nemu_connect takes this root.
            string installDir = @"C:\Program Files\Netease\MuMu";
            string libPath = Path.Combine(installDir, "nx_main", "sdk", "external_renderer_ipc.dll");
            if (!File.Exists(libPath)) libPath = Path.Combine(installDir, "shell", "sdk", "external_renderer_ipc.dll");
            if (!File.Exists(libPath)) return ExitErr("external_renderer_ipc.dll not found under " + installDir);

            string e;
            if (!Bind(libPath, out e)) return ExitErr("bind failed: " + e);

            // connect (path is the MuMu installation root)
            int handle = nemu_connect(installDir, index);
            if (handle == 0) return ExitErr("nemu_connect failed for index " + index);
            try
            {
                // pkg = args[2]; ops needing a package use it (display/shot use it too)
                string pkg = args.Length > 2 ? args[2] : "default";
                switch (cmd)
                {
                    case "display":
                    {
                        int d = GetDisplay(handle, pkg);
                        if (d < 0) return ExitErr("nemu_get_display_id failed");
                        Console.WriteLine(d);
                        return 0;
                    }
                    case "tap":
                    {
                        if (args.Length < 5) return ExitErr("tap needs x y");
                        int d = GetDisplay(handle, pkg);
                        if (d < 0) return ExitErr("nemu_get_display_id failed");
                        int x = int.Parse(args[3]), y = int.Parse(args[4]);
                        return Tap(handle, d, x, y) ? 0 : ExitErr("tap failed");
                    }
                    case "text":
                    {
                        if (args.Length < 4) return ExitErr("text needs a string");
                        int d = GetDisplay(handle, pkg);
                        if (d < 0) return ExitErr("nemu_get_display_id failed");
                        return InputText(handle, args[3]) ? 0 : ExitErr("input_text failed");
                    }
                    case "swipe":
                    {
                        if (args.Length < 8) return ExitErr("swipe needs x1 y1 x2 y2 ms");
                        int d = GetDisplay(handle, pkg);
                        if (d < 0) return ExitErr("nemu_get_display_id failed");
                        int x1 = int.Parse(args[3]), y1 = int.Parse(args[4]);
                        int x2 = int.Parse(args[5]), y2 = int.Parse(args[6]);
                        int ms = int.Parse(args[7]);
                        return Swipe(handle, d, x1, y1, x2, y2, ms) ? 0 : ExitErr("swipe failed");
                    }
                    case "key":
                    {
                        if (args.Length < 4) return ExitErr("key needs android keycode");
                        int d = GetDisplay(handle, pkg);
                        if (d < 0) return ExitErr("nemu_get_display_id failed");
                        int kc = int.Parse(args[3]);
                        int lk = AndroidToLinuxKey(kc);
                        int r1 = nemu_input_event_key_down(handle, d, lk);
                        Thread.Sleep(30);
                        int r2 = nemu_input_event_key_up(handle, d, lk);
                        return (r1 == 0 && r2 == 0) ? 0 : ExitErr("key failed");
                    }
                    case "shot":
                    {
                        if (args.Length < 4) return ExitErr("shot needs output path");
                        int d = GetDisplay(handle, pkg);
                        if (d < 0) return ExitErr("nemu_get_display_id failed");
                        return Screenshot(handle, d, args[3]) ? 0 : ExitErr("screenshot failed");
                    }
                    default:
                        return ExitErr("unknown command " + cmd);
                }
            }
            finally
            {
                nemu_disconnect(handle);
                if (_lib != IntPtr.Zero) FreeLibrary(_lib);
            }
        }

        static int GetDisplay(int handle, string package)
        {
            IntPtr pkg = Ansi(string.IsNullOrEmpty(package) || package == "default" ? "default" : package);
            try { return nemu_get_display_id(handle, pkg, 0); }
            finally { Marshal.FreeHGlobal(pkg); }
        }

        static bool Tap(int handle, int display, int x, int y)
        {
            // contact starts at 0 in our API, MuMu expects 1-based.
            if (nemu_input_event_finger_touch_down(handle, display, 1, x, y) != 0) return false;
            Thread.Sleep(60);
            return nemu_input_event_finger_touch_up(handle, display, 1) == 0;
        }

        static bool InputText(int handle, string text)
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(text);
            IntPtr buf = Marshal.AllocHGlobal(utf8.Length);
            try
            {
                Marshal.Copy(utf8, 0, buf, utf8.Length);
                return nemu_input_text(handle, utf8.Length, buf) == 0;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        static bool Swipe(int handle, int display, int x1, int y1, int x2, int y2, int ms)
        {
            if (nemu_input_event_finger_touch_down(handle, display, 1, x1, y1) != 0) return false;
            int steps = Math.Max(1, ms / 10);
            for (int i = 1; i <= steps; i++)
            {
                int x = x1 + (x2 - x1) * i / steps;
                int y = y1 + (y2 - y1) * i / steps;
                // MuMu has no separate move; down == move.
                if (nemu_input_event_finger_touch_down(handle, display, 1, x, y) != 0) return false;
                Thread.Sleep(10);
            }
            return nemu_input_event_finger_touch_up(handle, display, 1) == 0;
        }

        static bool Screenshot(int handle, int display, string outPath)
        {
            int w = 0, h = 0;
            // Query size first (buffer null).
            int ret = nemu_capture_display(handle, display, 0, ref w, ref h, IntPtr.Zero);
            if (ret != 0) return false;
            if (w <= 0 || h <= 0) return false;

            byte[] buf = new byte[w * h * 4];
            IntPtr ptr = Marshal.AllocHGlobal(buf.Length);
            try
            {
                ret = nemu_capture_display(handle, display, buf.Length, ref w, ref h, ptr);
                if (ret != 0) return false;
                Marshal.Copy(ptr, buf, 0, buf.Length);
            }
            finally { Marshal.FreeHGlobal(ptr); }

            // Buffer is RGBA top-down per SDK docs? MaaFramework flips vertically
            // (cv::flip 0) and converts RGBA->BGR. Emulate: swap R/B then flip Y.
            using (Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            {
                BitmapData bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                try
                {
                    unsafe
                    {
                        byte* dst = (byte*)bd.Scan0;
                        int stride = bd.Stride;
                        for (int y = 0; y < h; y++)
                        {
                            byte* row = dst + (long)(h - 1 - y) * stride; // flip Y
                            for (int x = 0; x < w; x++)
                            {
                                int src = (y * w + x) * 4;
                                byte r = buf[src], g = buf[src + 1], b = buf[src + 2], a = buf[src + 3];
                                row[x * 4 + 0] = b; // BGRA
                                row[x * 4 + 1] = g;
                                row[x * 4 + 2] = r;
                                row[x * 4 + 3] = a;
                            }
                        }
                    }
                }
                finally { bmp.UnlockBits(bd); }
                bmp.Save(outPath, ImageFormat.Png);
            }
            return true;
        }

        // Android keycode -> Linux keycode (subset used: letters/numbers/back/enter/backspace/tab).
        static int AndroidToLinuxKey(int kc)
        {
            // Letters A-Z: 29..54 -> 30..54-ish linux; use the common map
            if (kc >= 29 && kc <= 54)
            {
                int[] lk = { 30, 48, 46, 32, 18, 33, 34, 35, 23, 36, 37, 38, 50, 49, 24, 25, 16, 19, 31, 20, 22, 47, 17, 45, 21, 44 };
                return lk[kc - 29];
            }
            // Numbers 0-9: 7..16 -> 11,2..10
            if (kc >= 7 && kc <= 16)
            {
                int[] lk = { 11, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
                return lk[kc - 7];
            }
            switch (kc)
            {
                case 4: return 158;  // BACK -> KEY_BACK
                case 66: return 28;  // ENTER -> KEY_ENTER
                case 67: return 14;  // DEL -> KEY_BACKSPACE
                case 111: return 1;  // ESCAPE -> KEY_ESC
                case 61: return 15;  // TAB -> KEY_TAB
                case 123: return 107; // MOVE_END -> KEY_END
                default: return kc;   // fallback: pass through
            }
        }
    }
}
