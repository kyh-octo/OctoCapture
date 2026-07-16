using System.Windows.Input;
using System.Windows.Interop;

namespace OctoCapture.Services
{
    /// <summary>
    /// 전역 단축키 관리자. 메시지 전용 창(HWND_MESSAGE)을 사용해 RegisterHotKey를 처리한다.
    /// Dispose 시 모든 단축키를 해제한다.
    /// </summary>
    public sealed class HotkeyManager : IDisposable
    {
        private readonly HwndSource _source;
        private readonly Dictionary<int, Action> _actions = new();
        private int _nextId = 1;
        private bool _disposed;

        public HotkeyManager()
        {
            var p = new HwndSourceParameters("OctoCaptureHotkeys")
            {
                Width = 0,
                Height = 0,
                WindowStyle = 0,
                ParentWindow = new IntPtr(-3), // HWND_MESSAGE
            };
            _source = new HwndSource(p);
            _source.AddHook(WndProc);
        }

        /// <summary>"Ctrl+Shift+A" 형식 문자열을 등록. 성공 시 id 반환, 실패 시 -1.</summary>
        public int Register(string hotkey, Action action)
        {
            if (!TryParse(hotkey, out uint mods, out uint vk)) return -1;
            int id = _nextId++;
            if (!NativeMethods.RegisterHotKey(_source.Handle, id, mods | NativeMethods.MOD_NOREPEAT, vk))
                return -1;
            _actions[id] = action;
            return id;
        }

        public void UnregisterAll()
        {
            foreach (var id in _actions.Keys)
                NativeMethods.UnregisterHotKey(_source.Handle, id);
            _actions.Clear();
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == NativeMethods.WM_HOTKEY && _actions.TryGetValue(wParam.ToInt32(), out var action))
            {
                handled = true;
                try { action(); } catch { /* 캡쳐 도중 예외로 앱이 죽지 않도록 */ }
            }
            return IntPtr.Zero;
        }

        /// <summary>"Ctrl+Shift+A" → (modifiers, virtual key)</summary>
        public static bool TryParse(string hotkey, out uint mods, out uint vk)
        {
            mods = 0; vk = 0;
            if (string.IsNullOrWhiteSpace(hotkey)) return false;
            foreach (var raw in hotkey.Split('+'))
            {
                var part = raw.Trim();
                switch (part.ToLowerInvariant())
                {
                    case "ctrl": case "control": mods |= NativeMethods.MOD_CONTROL; break;
                    case "shift": mods |= NativeMethods.MOD_SHIFT; break;
                    case "alt": mods |= NativeMethods.MOD_ALT; break;
                    case "win": mods |= NativeMethods.MOD_WIN; break;
                    default:
                        if (Enum.TryParse<Key>(part, true, out var key))
                            vk = (uint)KeyInterop.VirtualKeyFromKey(key);
                        break;
                }
            }
            return vk != 0;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            UnregisterAll();
            _source.RemoveHook(WndProc);
            _source.Dispose();
        }
    }
}
