using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Munin;

/// <summary>
/// Drops transcribed text at the cursor by setting the clipboard and sending Ctrl+V,
/// then restoring the previous clipboard. Clipboard access is marshaled to the STA UI thread.
/// </summary>
internal sealed class TextInjector
{
    private readonly Control _uiMarshal;

    public TextInjector(Control uiMarshal) => _uiMarshal = uiMarshal;

    public void Inject(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        string? saved = null;
        _uiMarshal.Invoke(() =>
        {
            if (Clipboard.ContainsText()) saved = Clipboard.GetText();
            Clipboard.SetText(text);
        });

        SendCtrlV();

        if (saved != null)
        {
            string prev = saved;
            Task.Delay(400).ContinueWith(_ =>
            {
                try { _uiMarshal.Invoke(() => Clipboard.SetText(prev)); }
                catch { /* foreground app grabbed the clipboard; leave ours in place */ }
            });
        }
    }

    private static void SendCtrlV()
    {
        const ushort VK_CONTROL = 0x11;
        const ushort VK_V = 0x56;
        var inputs = new[]
        {
            KeyInput(VK_CONTROL, false),
            KeyInput(VK_V, false),
            KeyInput(VK_V, true),
            KeyInput(VK_CONTROL, true),
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private static INPUT KeyInput(ushort vk, bool keyUp) => new()
    {
        type = 1, // INPUT_KEYBOARD
        u = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wVk = vk,
                dwFlags = keyUp ? 0x0002u : 0u, // KEYEVENTF_KEYUP
            }
        }
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
}
