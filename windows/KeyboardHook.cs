using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Huginn;

/// <summary>
/// Low-level keyboard hook for push-to-talk. Supports an optional modifier: recording is "engaged"
/// while the main key is held together with the modifier (or just the main key if no modifier is
/// set). <see cref="KeyDown"/> fires when the chord engages, <see cref="KeyUp"/> when it releases.
/// Both run on the UI thread, so handlers must be quick.
/// </summary>
internal sealed class KeyboardHook : IHotkey
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;
    private const int LLKHF_INJECTED = 0x10;

    private readonly int _mainVk;
    private readonly int _modVk;    // 0 = no modifier
    private readonly bool _swallow;
    private readonly int _cancelVk;
    private volatile bool _cancelArmed;
    private readonly LowLevelKeyboardProc _proc;
    private IntPtr _hookId = IntPtr.Zero;
    private bool _mainDown, _modDown, _engaged;

    public event Action? KeyDown;
    public event Action? KeyUp;

    /// <summary>Raised when the cancel key is pressed while a dictation flow is active (see <see cref="CancelArmed"/>).</summary>
    public event Action? CancelRequested;

    /// <summary>Set true while a dictation flow is active — the cancel key is then intercepted (from ANY state,
    /// even when injected by mouse/remap software) and fires <see cref="CancelRequested"/>; otherwise it passes
    /// through untouched, so the cancel key works normally everywhere else.</summary>
    public bool CancelArmed { get => _cancelArmed; set => _cancelArmed = value; }

    /// <summary>The chord as the menu shows it, e.g. "Left Ctrl + Space".</summary>
    public string Name => _modVk == 0 ? KeyName(_mainVk) : $"{KeyName(_modVk)} + {KeyName(_mainVk)}";

    private static string KeyName(int vk) => vk switch
    {
        0xA3 => "Right Ctrl",
        0xA2 => "Left Ctrl",
        0x20 => "Space",
        0x77 => "F8",
        _ => $"key 0x{vk:X2}",
    };

    public KeyboardHook(int mainVk, int modifierVk, bool swallow, int cancelVk)
    {
        _mainVk = mainVk;
        _modVk = modifierVk;
        _swallow = swallow;
        _cancelVk = cancelVk;
        _proc = HookCallback; // keep the delegate alive for the hook's lifetime
    }

    public void Install()
    {
        if (_hookId != IntPtr.Zero) return;
        using var process = Process.GetCurrentProcess();
        using var module = process.MainModule!;
        _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(module.ModuleName), 0);
    }

    public void Uninstall()
    {
        if (_hookId == IntPtr.Zero) return;
        UnhookWindowsHookEx(_hookId);
        _hookId = IntPtr.Zero;
        _mainDown = _modDown = _engaged = false;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int vk = Marshal.ReadInt32(lParam);          // KBDLLHOOKSTRUCT.vkCode
            int flags = Marshal.ReadInt32(lParam, 8);    // KBDLLHOOKSTRUCT.flags
            int msg = (int)wParam;
            bool isDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;

            // Cancel key (default Escape) — fires whenever a dictation flow is active (CancelArmed), from ANY
            // state, and even when INJECTED (so a mouse button remapped to this key via Logitech Options+ works).
            // Checked BEFORE the injected filter for that reason; swallowed so it never reaches the focused app.
            if (_cancelArmed && isDown && vk == _cancelVk)
            {
                CancelRequested?.Invoke();
                return (IntPtr)1;
            }

            if ((flags & LLKHF_INJECTED) == 0)
            {
                bool isUp = msg == WM_KEYUP || msg == WM_SYSKEYUP;
                bool swallowThis = false;

                if (_modVk != 0 && vk == _modVk)
                {
                    if (isDown) _modDown = true;
                    else if (isUp) _modDown = false;
                }

                if (vk == _mainVk && (isDown || isUp))
                {
                    if (isDown) _mainDown = true;
                    else _mainDown = false;

                    // Swallow the main key only while the modifier is involved (or in single-key
                    // mode), so a normal press of the key still works when the modifier isn't held.
                    if (_swallow && (_modVk == 0 || _modDown || _engaged))
                        swallowThis = true;
                }

                UpdateEngaged();

                if (swallowThis) return (IntPtr)1;
            }
        }
        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private void UpdateEngaged()
    {
        bool now = (_modVk == 0 || _modDown) && _mainDown;
        if (now && !_engaged)
        {
            _engaged = true;
            KeyDown?.Invoke();
        }
        else if (!now && _engaged)
        {
            _engaged = false;
            KeyUp?.Invoke();
        }
    }

    public void Dispose() => Uninstall();

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);
}
