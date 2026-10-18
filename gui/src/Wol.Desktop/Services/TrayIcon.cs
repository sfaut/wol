using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Wol.Desktop.Services;

/// <summary>
/// Status shown by the notification area icon.
/// </summary>
public enum TrayStatus
{
    /// <summary>
    /// Every machine online (or none configured).
    /// </summary>
    Online,

    /// <summary>
    /// Some machines are not online.
    /// </summary>
    Offline,

    /// <summary>
    /// The CLI, the configuration or the network has a problem.
    /// </summary>
    Error,
}

/// <summary>
/// Item of the notification area menu; a null item is a separator.
/// </summary>
/// <param name="Text">Text of the item.</param>
/// <param name="Action">What the item does; null for a submenu or a disabled item.</param>
/// <param name="Enabled">Whether the item can be used.</param>
/// <param name="IsDefault">Whether the item is the default one (bold).</param>
/// <param name="Children">Items of the submenu, if any.</param>
public sealed record TrayMenuItem(
    string Text,
    Action? Action = null,
    bool Enabled = true,
    bool IsDefault = false,
    IReadOnlyList<TrayMenuItem?>? Children = null);

/// <summary>
/// Icon in the notification area (<c>Shell_NotifyIcon</c>), with its tooltip, its menu and its
/// balloons. Lives on the UI thread: a hidden window of that thread receives its messages.
/// </summary>
public sealed unsafe partial class TrayIcon : IDisposable
{
    /// <summary>
    /// Message sent by the shell for icon events.
    /// </summary>
    private const uint CallbackMessage = 0x8000 + 1;

    /// <summary>
    /// Identifier of the icon for the hidden window.
    /// </summary>
    private const uint IconId = 1;

    /// <summary>
    /// Name of the window class of the hidden window.
    /// </summary>
    private const string ClassName = "Wol.TrayIcon";

    /// <summary>
    /// The live icon (one per process), which receives the window messages.
    /// </summary>
    private static TrayIcon? s_current;

    /// <summary>
    /// Message broadcast when the taskbar is created again (Explorer restarted).
    /// </summary>
    private static readonly uint TaskbarCreated = RegisterWindowMessageW("TaskbarCreated");

    /// <summary>
    /// Hidden window receiving the icon messages.
    /// </summary>
    private readonly nint _window;

    /// <summary>
    /// Actions of the menu being shown, by command identifier.
    /// </summary>
    private readonly Dictionary<int, Action> _commands = [];

    /// <summary>
    /// Last command identifier given to a menu item.
    /// </summary>
    private int _lastCommand;

    /// <summary>
    /// Icon handle shown, destroyed when replaced.
    /// </summary>
    private nint _icon;

    /// <summary>
    /// Status shown.
    /// </summary>
    private TrayStatus _status;

    /// <summary>
    /// Tooltip shown.
    /// </summary>
    private string _tooltip = string.Empty;

    /// <summary>
    /// Whether the icon was added to the notification area.
    /// </summary>
    private bool _added;

    /// <summary>
    /// Creates the hidden window and adds the icon.
    /// </summary>
    /// <param name="status">Status shown first.</param>
    /// <param name="tooltip">Tooltip shown first.</param>
    public TrayIcon(TrayStatus status, string tooltip)
    {
        s_current = this;
        var instance = GetModuleHandleW(null);
        fixed (char* name = ClassName)
        {
            var windowClass = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = &WindowProc,
                hInstance = instance,
                lpszClassName = name,
            };
            RegisterClassExW(ref windowClass);
        }

        _window = CreateWindowExW(0, ClassName, "Wol", 0, 0, 0, 0, 0, 0, 0, instance, 0);
        _status = status;
        _tooltip = tooltip;
        Add();
    }

    /// <summary>
    /// Raised when the icon is clicked or selected with the keyboard.
    /// </summary>
    public event EventHandler? Invoked;

    /// <summary>
    /// Asks for the items of the menu, just before it opens.
    /// </summary>
    public Func<IReadOnlyList<TrayMenuItem?>>? MenuRequested { get; set; }

    /// <summary>
    /// Shows a status and a tooltip.
    /// </summary>
    /// <param name="status">Status of the icon.</param>
    /// <param name="tooltip">Tooltip, at most 127 characters.</param>
    public void Update(TrayStatus status, string tooltip)
    {
        if (status == _status && tooltip == _tooltip)
        {
            return;
        }

        _status = status;
        _tooltip = tooltip;
        var data = Data(NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        Shell_NotifyIconW(NIM_MODIFY, ref data);
    }

    /// <summary>
    /// Shows a balloon (a Windows notification, from the icon).
    /// </summary>
    /// <param name="title">Title, at most 63 characters.</param>
    /// <param name="text">Text, at most 255 characters.</param>
    /// <param name="warning">Whether it is a warning rather than an information.</param>
    public void ShowBalloon(string title, string text, bool warning = false)
    {
        var data = Data(NIF_INFO);
        Copy(title, data.szInfoTitle, 64);
        Copy(text, data.szInfo, 256);
        data.dwInfoFlags = (warning ? NIIF_WARNING : NIIF_INFO) | NIIF_RESPECT_QUIET_TIME;
        Shell_NotifyIconW(NIM_MODIFY, ref data);
    }

    /// <summary>
    /// Removes the icon and destroys the hidden window.
    /// </summary>
    public void Dispose()
    {
        if (_added)
        {
            var data = Data(0);
            Shell_NotifyIconW(NIM_DELETE, ref data);
            _added = false;
        }
        if (_icon != 0)
        {
            DestroyIcon(_icon);
            _icon = 0;
        }
        DestroyWindow(_window);
        s_current = null;
    }

    /// <summary>
    /// Tells whether the taskbar uses the light theme (the icon follows the taskbar, not the app).
    /// </summary>
    /// <returns>True for a light taskbar.</returns>
    private static bool IsTaskbarLight()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("SystemUsesLightTheme") is int value && value != 0;
    }

    /// <summary>
    /// Copies a text into a fixed character buffer, truncated and null-terminated.
    /// </summary>
    /// <param name="text">Text to copy.</param>
    /// <param name="buffer">Destination buffer.</param>
    /// <param name="capacity">Buffer size, terminator included.</param>
    private static void Copy(string text, char* buffer, int capacity)
    {
        var length = Math.Min(text.Length, capacity - 1);
        text.AsSpan(0, length).CopyTo(new Span<char>(buffer, capacity));
        buffer[length] = '\0';
    }

    /// <summary>
    /// Adds the icon (again, after Explorer restarted) and asks for the version 4 behavior.
    /// </summary>
    private void Add()
    {
        var data = Data(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        _added = Shell_NotifyIconW(NIM_ADD, ref data);
        data.uVersion = NOTIFYICON_VERSION_4;
        Shell_NotifyIconW(NIM_SETVERSION, ref data);
    }

    /// <summary>
    /// Builds the icon data, loading the icon of the current status and taskbar theme.
    /// </summary>
    /// <param name="flags">Members to apply.</param>
    /// <returns>The data.</returns>
    private NOTIFYICONDATAW Data(uint flags)
    {
        var data = new NOTIFYICONDATAW
        {
            cbSize = (uint)sizeof(NOTIFYICONDATAW),
            hWnd = _window,
            uID = IconId,
            uFlags = flags,
            uCallbackMessage = CallbackMessage,
        };

        if ((flags & NIF_ICON) != 0)
        {
            var theme = IsTaskbarLight() ? "light" : "dark";
            var file = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", $"tray-{_status.ToString().ToLowerInvariant()}-{theme}.ico");
            var size = GetSystemMetricsForDpi(SM_CXSMICON, GetDpiForSystem());
            var icon = LoadImageW(0, file, IMAGE_ICON, size, size, LR_LOADFROMFILE);
            if (icon != 0)
            {
                if (_icon != 0)
                {
                    DestroyIcon(_icon);
                }
                _icon = icon;
            }
            data.hIcon = _icon;
        }
        if ((flags & NIF_TIP) != 0)
        {
            Copy(_tooltip, data.szTip, 128);
        }
        return data;
    }

    /// <summary>
    /// Shows the menu at the pointer and runs the item picked.
    /// </summary>
    private void ShowMenu()
    {
        if (MenuRequested?.Invoke() is not { } items)
        {
            return;
        }

        _commands.Clear();
        _lastCommand = 0;
        var menu = BuildMenu(items);
        GetCursorPos(out var point);

        // The window must be in the foreground, or the menu does not close when clicking away.
        SetForegroundWindow(_window);
        var command = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON, point.X, point.Y, _window, 0);
        PostMessageW(_window, 0, 0, 0);
        DestroyMenu(menu);

        if (_commands.TryGetValue(command, out var action))
        {
            action();
        }
    }

    /// <summary>
    /// Builds a native popup menu.
    /// </summary>
    /// <param name="items">Items; null for a separator.</param>
    /// <returns>The menu handle (submenus are destroyed with it).</returns>
    private nint BuildMenu(IReadOnlyList<TrayMenuItem?> items)
    {
        var menu = CreatePopupMenu();
        foreach (var item in items)
        {
            if (item is null)
            {
                AppendMenuW(menu, MF_SEPARATOR, 0, null);
            }
            else if (item.Children is { } children)
            {
                AppendMenuW(menu, MF_POPUP | (item.Enabled ? 0 : MF_GRAYED), (nuint)BuildMenu(children), item.Text);
            }
            else
            {
                var id = ++_lastCommand;
                if (item.Action is { } action)
                {
                    _commands[id] = action;
                }
                AppendMenuW(menu, item.Enabled && item.Action is not null ? MF_STRING : MF_GRAYED, (nuint)id, item.Text);
                if (item.IsDefault)
                {
                    SetMenuDefaultItem(menu, (uint)id, 0);
                }
            }
        }
        return menu;
    }

    /// <summary>
    /// Handles a message of the hidden window.
    /// </summary>
    /// <param name="message">Message.</param>
    /// <param name="wParam">First parameter.</param>
    /// <param name="lParam">Second parameter.</param>
    private void Handle(uint message, nint wParam, nint lParam)
    {
        if (message == CallbackMessage)
        {
            switch ((uint)(lParam & 0xFFFF))
            {
                case NIN_SELECT or NIN_KEYSELECT:
                    Invoked?.Invoke(this, EventArgs.Empty);
                    break;
                case WM_CONTEXTMENU:
                    ShowMenu();
                    break;
            }
        }
        else if (message == TaskbarCreated)
        {
            Add();
        }
        else if (message == WM_SETTINGCHANGE)
        {
            // The taskbar theme may have changed: load the icon again.
            var data = Data(NIF_ICON);
            Shell_NotifyIconW(NIM_MODIFY, ref data);
        }
    }

    /// <summary>
    /// Window procedure of the hidden window.
    /// </summary>
    /// <param name="window">Window handle.</param>
    /// <param name="message">Message.</param>
    /// <param name="wParam">First parameter.</param>
    /// <param name="lParam">Second parameter.</param>
    /// <returns>The result of the message.</returns>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint WindowProc(nint window, uint message, nint wParam, nint lParam)
    {
        if (s_current is { } current && window == current._window)
        {
            current.Handle(message, wParam, lParam);
        }
        return DefWindowProcW(window, message, wParam, lParam);
    }

    /// <summary>
    /// <c>Shell_NotifyIcon</c> messages.
    /// </summary>
    private const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    /// <summary>
    /// <c>NOTIFYICONDATA</c> members to apply.
    /// </summary>
    private const uint NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4, NIF_INFO = 0x10, NIF_SHOWTIP = 0x80;
    /// <summary>
    /// Balloon flags.
    /// </summary>
    private const uint NIIF_INFO = 0x1, NIIF_WARNING = 0x2, NIIF_RESPECT_QUIET_TIME = 0x80;
    /// <summary>
    /// Icon behavior version: modern messages (<c>NIN_SELECT</c>, <c>WM_CONTEXTMENU</c>).
    /// </summary>
    private const uint NOTIFYICON_VERSION_4 = 4;
    /// <summary>
    /// Icon events and window messages handled.
    /// </summary>
    private const uint NIN_SELECT = 0x400, NIN_KEYSELECT = 0x401, WM_CONTEXTMENU = 0x7B, WM_SETTINGCHANGE = 0x1A;
    /// <summary>
    /// Menu item flags.
    /// </summary>
    private const uint MF_STRING = 0x0, MF_GRAYED = 0x1, MF_POPUP = 0x10, MF_SEPARATOR = 0x800;
    /// <summary>
    /// <c>TrackPopupMenuEx</c> flags.
    /// </summary>
    private const uint TPM_RIGHTBUTTON = 0x2, TPM_RETURNCMD = 0x100;
    /// <summary>
    /// <c>LoadImage</c> type and flags.
    /// </summary>
    private const uint IMAGE_ICON = 1, LR_LOADFROMFILE = 0x10;
    /// <summary>
    /// System metric: width of a small icon.
    /// </summary>
    private const int SM_CXSMICON = 49;

    /// <summary>
    /// <c>NOTIFYICONDATAW</c> (shellapi.h).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public nint hIcon;
        public fixed char szTip[128];
        public uint dwState;
        public uint dwStateMask;
        public fixed char szInfo[256];
        public uint uVersion;
        public fixed char szInfoTitle[64];
        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    /// <summary>
    /// <c>WNDCLASSEXW</c> (winuser.h).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nint> lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public char* lpszMenuName;
        public char* lpszClassName;
        public nint hIconSm;
    }

    /// <summary>
    /// <c>POINT</c> (windef.h).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    /// <summary>
    /// Win32 <c>Shell_NotifyIconW</c> (shell32.dll).
    /// </summary>
    [LibraryImport("shell32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Shell_NotifyIconW(uint message, ref NOTIFYICONDATAW data);

    /// <summary>
    /// Win32 <c>GetModuleHandleW</c> (kernel32.dll).
    /// </summary>
    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint GetModuleHandleW(string? name);

    /// <summary>
    /// Win32 <c>RegisterClassExW</c> (user32.dll).
    /// </summary>
    [LibraryImport("user32.dll")]
    private static partial ushort RegisterClassExW(ref WNDCLASSEXW windowClass);

    /// <summary>
    /// Win32 <c>CreateWindowExW</c> (user32.dll).
    /// </summary>
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateWindowExW(
        uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    /// <summary>
    /// Win32 <c>DefWindowProcW</c> (user32.dll).
    /// </summary>
    [LibraryImport("user32.dll")]
    private static partial nint DefWindowProcW(nint window, uint message, nint wParam, nint lParam);

    /// <summary>
    /// Win32 <c>DestroyWindow</c> (user32.dll).
    /// </summary>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(nint window);

    /// <summary>
    /// Win32 <c>RegisterWindowMessageW</c> (user32.dll).
    /// </summary>
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint RegisterWindowMessageW(string name);

    /// <summary>
    /// Win32 <c>LoadImageW</c> (user32.dll).
    /// </summary>
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint LoadImageW(nint instance, string name, uint type, int width, int height, uint flags);

    /// <summary>
    /// Win32 <c>DestroyIcon</c> (user32.dll).
    /// </summary>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(nint icon);

    /// <summary>
    /// Win32 <c>GetSystemMetricsForDpi</c> (user32.dll).
    /// </summary>
    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetricsForDpi(int index, uint dpi);

    /// <summary>
    /// Win32 <c>GetDpiForSystem</c> (user32.dll).
    /// </summary>
    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForSystem();

    /// <summary>
    /// Win32 <c>CreatePopupMenu</c> (user32.dll).
    /// </summary>
    [LibraryImport("user32.dll")]
    private static partial nint CreatePopupMenu();

    /// <summary>
    /// Win32 <c>AppendMenuW</c> (user32.dll).
    /// </summary>
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AppendMenuW(nint menu, uint flags, nuint id, string? text);

    /// <summary>
    /// Win32 <c>SetMenuDefaultItem</c> (user32.dll).
    /// </summary>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetMenuDefaultItem(nint menu, uint item, uint byPosition);

    /// <summary>
    /// Win32 <c>TrackPopupMenuEx</c> (user32.dll).
    /// </summary>
    [LibraryImport("user32.dll")]
    private static partial int TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint window, nint parameters);

    /// <summary>
    /// Win32 <c>DestroyMenu</c> (user32.dll).
    /// </summary>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyMenu(nint menu);

    /// <summary>
    /// Win32 <c>GetCursorPos</c> (user32.dll).
    /// </summary>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out POINT point);

    /// <summary>
    /// Win32 <c>SetForegroundWindow</c> (user32.dll).
    /// </summary>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint window);

    /// <summary>
    /// Win32 <c>PostMessageW</c> (user32.dll).
    /// </summary>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessageW(nint window, uint message, nint wParam, nint lParam);
}
