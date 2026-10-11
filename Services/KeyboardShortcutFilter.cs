using System.Runtime.InteropServices;

namespace ExamKiosk.Services;

/// <summary>
/// Suppresses common shell and task-switching shortcuts on the exam desktop.
/// Ctrl+Alt+Delete is handled by Winlogon and cannot be intercepted by this hook.
/// </summary>
internal sealed class KeyboardShortcutFilter : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int HcAction = 0;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const uint LlkhfAltDown = 0x20;
    private const int VkTab = 0x09;
    private const int VkEscape = 0x1B;
    private const int VkSpace = 0x20;
    private const int VkF4 = 0x73;
    private const int VkF10 = 0x79;
    private const int VkApps = 0x5D;
    private const int VkLeftWindows = 0x5B;
    private const int VkRightWindows = 0x5C;
    private const int VkControl = 0x11;
    private const int VkShift = 0x10;
    private const int VkMenu = 0x12;

    private readonly KeyboardHookProcedure callback;
    private IntPtr hook;

    public KeyboardShortcutFilter()
    {
        callback = HandleKeyboardEvent;
    }

    public void Start()
    {
        if (hook != IntPtr.Zero)
        {
            return;
        }

        hook = SetWindowsHookEx(WhKeyboardLl, callback, GetModuleHandle(null), 0);
        if (hook == IntPtr.Zero)
        {
            throw new InvalidOperationException($"The keyboard shortcut filter could not be installed (Windows error {Marshal.GetLastWin32Error()}).");
        }
    }

    public void Dispose()
    {
        if (hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(hook);
            hook = IntPtr.Zero;
        }
    }

    private IntPtr HandleKeyboardEvent(int code, IntPtr message, IntPtr data)
    {
        if (code == HcAction && IsKeyMessage(message))
        {
            var key = Marshal.PtrToStructure<KeyboardHookData>(data);
            if (ShouldBlock((int)key.VirtualKey, key.Flags))
            {
                return new IntPtr(1);
            }
        }

        return CallNextHookEx(hook, code, message, data);
    }

    private static bool IsKeyMessage(IntPtr message)
    {
        var value = message.ToInt32();
        return value is WmKeyDown or WmKeyUp or WmSysKeyDown or WmSysKeyUp;
    }

    private static bool ShouldBlock(int key, uint flags)
    {
        // Suppress shell and application-switching keys while this app-level
        // hook is active on the exam desktop.
        if (key is VkApps or VkLeftWindows or VkRightWindows)
        {
            return true;
        }

        var alt = (flags & LlkhfAltDown) != 0 || IsPressed(VkMenu);
        var control = IsPressed(VkControl);
        var shift = IsPressed(VkShift);

        if (alt && key is VkTab or VkEscape or VkF4 or VkSpace or VkF10)
        {
            return true;
        }

        if (control && key == VkEscape)
        {
            return true;
        }

        if (control && shift && key == VkEscape)
        {
            return true;
        }

        if (shift && key == VkF10)
        {
            return true;
        }

        return false;
    }

    private static bool IsPressed(int virtualKey)
    {
        return (GetAsyncKeyState(virtualKey) & 0x8000) != 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardHookData
    {
        public uint VirtualKey;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr KeyboardHookProcedure(int code, IntPtr message, IntPtr data);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int hookType, KeyboardHookProcedure callback, IntPtr module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}
