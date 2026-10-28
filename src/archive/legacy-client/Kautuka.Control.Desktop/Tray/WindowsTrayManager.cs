using System.Runtime.InteropServices;
using System.Runtime.Versioning;

using Kautuka.Control.Core.Tray;

namespace Kautuka.Control.Desktop;

/// <summary>
/// Windows system tray icon via Shell_NotifyIconW (shell32.dll).
/// Runs its own Win32 message pump on a dedicated background thread.
/// AOT-safe: pure P/Invoke, no WinForms, no reflection.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsTrayManager : ITrayManager
{
    private static readonly Action<ILogger, int, Exception?> logTrayWindowCreateFailed = LoggerMessage.Define<int>(
        LogLevel.Error,
        new EventId(100, nameof(logTrayWindowCreateFailed)),
        "Failed to create tray HWND: {Error}");

    // Tray callback messages
    private const uint wmAppTray = 0x8001u;
    private const uint wmAppStatus = 0x8002u; // wParam: 1=online, 0=offline
    private const uint wmAppBalloon = 0x8003u; // show balloon notification

    // Win32 window messages
    private const uint wmDestroy = 0x0002u;
    private const uint wmCommand = 0x0111u;

    // Shell_NotifyIconW operations
    private const uint nimAdd = 0x00000000u;
    private const uint nimModify = 0x00000001u;
    private const uint nimDelete = 0x00000002u;

    // NOTIFYICONDATA flags
    private const uint nifMessage = 0x00000001u;
    private const uint nifIcon = 0x00000002u;
    private const uint nifInfo = 0x00000010u; // balloon notification

    // Balloon icon type
    private const uint niifInfo = 0x00000001u;

    // AppendMenuW flags
    private const int mfString = 0x0000;
    private const int mfSeparator = 0x0800;

    // Tray icon notification low-word events
    private const int wmLbuttonDblclk = 0x0203;
    private const int wmRbuttonUp = 0x0205;

    // TrackPopupMenu flags
    private const uint tpmRightbutton = 0x0002u;
    private const uint tpmRightalign = 0x0008u;

    private readonly ILogger log;

    private IntPtr hwnd;
    private IntPtr hicon;
    private IntPtr hiconOnline;
    private IntPtr hiconOffline;
    private Action? onActivate;
    private Thread? thread;
    private TrayMenuItem[] menu = [];
    private volatile bool disposed;
    private volatile bool gatewayOnline;
    private ushort windowClassAtom;
    // GC root — prevents the WndProc delegate being collected while the message pump runs
    private WndProcDelegate? wndProcDelegate;
    // Balloon payload — written before PostMessage, read on the pump thread
    private string balloonTitle = string.Empty;
    private string balloonMessage = string.Empty;

    public WindowsTrayManager(ILogger log) => this.log = log;

    ~WindowsTrayManager() => Hide();


    public void Show(string tooltip, TrayMenuItem[] menu, Action? onActivate = null)
    {
        this.menu = menu;
        this.onActivate = onActivate;
        thread = new Thread(() => RunMessagePump(tooltip)) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    public void Hide()
    {
        if (hwnd != IntPtr.Zero)
            PostMessage(hwnd, wmDestroy, 0, 0);
    }

    public void UpdateStatus(bool gatewayOnline)
    {
        this.gatewayOnline = gatewayOnline;
        if (hwnd != IntPtr.Zero)
            PostMessage(hwnd, wmAppStatus, gatewayOnline ? 1 : 0, 0);
    }

    public void ShowBalloon(string title, string message)
    {
        balloonTitle = title.Length > 63 ? title[..63] : title;
        balloonMessage = message.Length > 255 ? message[..255] : message;
        if (hwnd != IntPtr.Zero)
            PostMessage(hwnd, wmAppBalloon, 0, 0);
    }

    public void Dispose()
    {
        disposed = true;
        Hide();
        GC.SuppressFinalize(this);
    }

    // ── Win32 message pump ────────────────────────────────────────────────────


    private void RunMessagePump(string tooltip)
    {
        // Hold a managed GC root for the delegate so it survives AOT collection
        wndProcDelegate = WndProc;

        // Register a unique window class for tray messages
        var wc = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            style = 0,
            lpfnWndProc = wndProcDelegate,
            cbClsExtra = 0,
            cbWndExtra = 0,
            hInstance = GetModuleHandle(null),
            hIcon = IntPtr.Zero,
            hCursor = IntPtr.Zero,
            hbrBackground = IntPtr.Zero,
            lpszMenuName = null,
            lpszClassName = "KautukaTrayWndClass"
        };
        windowClassAtom = RegisterClassEx(ref wc);
        hwnd = CreateWindowEx(0, "KautukaTrayWndClass", "KautukaTray", 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        if (hwnd == IntPtr.Zero)
        {
            logTrayWindowCreateFailed(log, Marshal.GetLastWin32Error(), null);
            return;
        }

        // Keep the Kautuka mark visible while preserving the gateway status badge.
        hiconOnline = BuildBrandStatusIcon(0x00, 0xC8, 0x5A); // green
        hiconOffline = BuildBrandStatusIcon(0x80, 0x80, 0x80); // grey
        hicon = gatewayOnline ? _iconOnline : hiconOffline;

        var tipText = BuildTooltip(tooltip, gatewayOnline);
        var nid = new NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = hwnd,
            uID = 1,
            uFlags = nifMessage | nifIcon | NnifTip
            uCallbackMessage = wmAppTray,
            hIcon = hicon,
            szTip = tipText,
        };
        Shell_NotifyIconW(nimAdd, ref nid);

        // MSG loop
        MSG msg;
        while (!disposed && GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        Shell_NotifyIconW(nimDelete, ref nid);
        if (hiconOnline != IntPtr.Zero) DestroyIcon(hiconOnline);
        if (hiconOffline != IntPtr.Zero) DestroyIcon(hiconOffline);
        DestroyWindow(hwnd);
        if (windowClassAtom != 0)
            UnregisterClass("KautukaTrayWndClass", wc.hInstance);
    }

    // Custom WndProc for tray icon messages
    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == wmAppTray)
        {
            int evt = lParam.ToInt32() & 0xFFFF;
            if (evt == wmLbuttonDblclk)
                onActivate?.Invoke();
            else if (evt == wmRbuttonUp)
                ShowContextMenu();
        }
        else if (msg == wmAppStatus)
        {
            var online = wParam.ToInt32() == 1;
            var nid = new NOTIFYICONDATA
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = hwnd,
                uID = 1,
                uFlags = nifIcon | NnifTip
                hIcon = online ? hiconOnline : hiconOffline,
                szTip = BuildTooltip("Kautuka", online),
            };
            Shell_NotifyIconW(nimModify, ref nid);
        }
        else if (msg == wmAppBalloon)
        {
            var nidEx = new NOTIFYICONDATA_EX
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA_EX>(),
                hWnd = hwnd,
                uID = 1,
                uFlags = nifInfo,
                szInfo = balloonMessage,
                szInfoTitle = balloonTitle,
                dwInfoFlags = niifInfo,
                uTimeout = 0,
            };
            Shell_NotifyIconExW(nimModify, ref nidEx);
        }
        else if (msg == wmCommand)
        {
            var id = (wParam.ToInt32() & 0xFFFF);
            if (id > 0 && id <= menu.Length)
                menu[id - 1].OnClick?.Invoke();
        }
        else if (msg == wmDestroy)
        {
            PostQuitMessage(0);
        }
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public int cbSize;
        public int style;
        public WndProcDelegate lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool UnregisterClass([MarshalAs(UnmanagedType.LPWStr)] string lpClassName, IntPtr hInstance);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetModuleHandle([MarshalAs(UnmanagedType.LPWStr)] string? lpModuleName);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int nExitCode);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private static string BuildTooltip(string baseTooltip, bool online) =>
        $"{baseTooltip} — Gateway: {(online ? "Running ✓" : "Offline")}";

    private void ShowContextMenu()
    {
        var hMenu = CreatePopupMenu();
        for (var i = 0; i < menu.Length; i++)
        {
            var item = menu[i];
            if (item.IsSeparator)
                AppendMenuW(hMenu, (uint)mfSeparator, 0, null);
            else
                AppendMenuW(hMenu, (uint)mfString, (uint)(i + 1), item.Label);
        }
        GetCursorPos(out var pt);
        SetForegroundWindow(hwnd);
        TrackPopupMenu(hMenu, tpmRightbutton | TtpmBottomalign | tpmRightalign, pt.X, pt.Y, 0, hwnd, IntPtr.Zero);
        DestroyMenu(hMenu);
    }

    /// <summary>Creates the 16×16 Kautuka mark with a colored gateway-status badge.</summary>
    private static IntPtr BuildBrandStatusIcon(byte statusR, byte statusG, byte statusB)
    {
        const int sz = 16;
        const int markRadius = 4;
        const int badgeX = 12, badgeY = 12, badgeRadius = 2;

        var bmiHeader = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = sz,
            biHeight = -sz, // top-down DIB
            biPlanes = 1,
            biBitCount = 32,
            biCompression = 0,   // BI_RGB
        };

        var hdc = CreateCompatibleDC(IntPtr.Zero);
        var hBmp = CreateDIBSection(hdc, ref bmiHeader, 0, out var bits, IntPtr.Zero, 0);
        if (hBmp == IntPtr.Zero) { DeleteDC(hdc); return IntPtr.Zero; }

        // Fill a rounded, vermilion → turmeric → vermilion mark. Pixels outside
        // the rounded corners stay transparent in the 32-bpp icon.
        var pixels = new int[sz * sz];
        for (var y = 0; y < sz; y++)
        {
            for (var x = 0; x < sz; x++)
            {
                var nearestX = Math.Clamp(x, markRadius, sz - markRadius - 1);
                var nearestY = Math.Clamp(y, markRadius, sz - markRadius - 1);
                var dx = x - nearestX;
                var dy = y - nearestY;
                if (dx * dx + dy * dy > markRadius * markRadius) continue;

                var position = x / (float)(sz - 1);
                var blend = position <= 0.5f ? position * 2 : (1 - position) * 2;
                var red = (byte)0xE3;
                var green = (byte)MathF.Round(0x42 + (0xA0 - 0x42) * blend);
                var blue = (byte)MathF.Round(0x34 + (0x18 - 0x34) * blend);
                pixels[y * sz + x] = blue | (green << 8) | (red << 16) | (0xFF << 24);
            }
        }

        // Rasterize the same custom k path used by the site SVG.
        const int samplesPerAxis = 4;
        var viewScale = 40f / sz;
        for (var y = 0; y < sz; y++)
        {
            for (var x = 0; x < sz; x++)
            {
                var letterCoverage = 0;
                var shadowCoverage = 0;
                for (var sy = 0; sy < samplesPerAxis; sy++)
                {
                    for (var sx = 0; sx < samplesPerAxis; sx++)
                    {
                        var viewX = (x + (sx + 0.5f) / samplesPerAxis) * viewScale;
                        var viewY = (y + (sy + 0.5f) / samplesPerAxis) * viewScale;
                        if (ContainsBrandLetter(viewX, viewY)) letterCoverage++;
                        if (ContainsBrandLetter(viewX, viewY - 1)) shadowCoverage++;
                    }
                }

                var position = x / (float)(sz - 1);
                var blend = position <= 0.5f ? position * 2 : (1 - position) * 2;
                var baseRed = 0xE3;
                var baseGreen = (int)MathF.Round(0x42 + (0xA0 - 0x42) * blend);
                var baseBlue = (int)MathF.Round(0x34 + (0x18 - 0x34) * blend);
                var shadowAmount = shadowCoverage / (float)(samplesPerAxis * samplesPerAxis) * 0.35f;
                var red = (int)MathF.Round(baseRed * (1 - shadowAmount));
                var green = (int)MathF.Round(baseGreen * (1 - shadowAmount));
                var blue = (int)MathF.Round(baseBlue * (1 - shadowAmount));
                var letterAmount = letterCoverage / (float)(samplesPerAxis * samplesPerAxis);
                red = (int)MathF.Round(red + (255 - red) * letterAmount);
                green = (int)MathF.Round(green + (255 - green) * letterAmount);
                blue = (int)MathF.Round(blue + (255 - blue) * letterAmount);
                if (letterCoverage > 0 || shadowCoverage > 0)
                    pixels[y * sz + x] = blue | (green << 8) | (red << 16) | (0xFF << 24);
            }
        }

        // Preserve online/offline state as a small white-ringed badge.
        for (var y = badgeY - 3; y <= badgeY + 3; y++)
        {
            for (var x = badgeX - 3; x <= badgeX + 3; x++)
            {
                var dx = x - badgeX;
                var dy = y - badgeY;
                var distance = dx * dx + dy * dy;
                if (distance <= 9) pixels[y * sz + x] = unchecked((int)0xFFFFFFFF);
                if (distance <= badgeRadius * badgeRadius)
                    pixels[y * sz + x] = statusB | (statusG << 8) | (statusR << 16) | (0xFF << 24);
            }
        }
        Marshal.Copy(pixels, 0, bits, pixels.Length);

        // Monochrome mask — all zeros (Windows uses per-pixel alpha from 32-bpp colour bitmap)
        var maskBytes = new byte[sz * 4]; // 1bpp row stride = ((sz + 31) / 32) * 4 = 4
        var hMask = CreateBitmap(sz, sz, 1, 1, maskBytes);

        var iconInfo = new ICONINFO { fIcon = true, hbmColor = hBmp, hbmMask = hMask };
        var hIcon = CreateIconIndirect(ref iconInfo);

        DeleteObject(hBmp);
        DeleteObject(hMask);
        DeleteDC(hdc);
        return hIcon;
    }

    private static bool ContainsBrandLetter(float x, float y)
    {
        ReadOnlySpan<float> points = [
            11.5f, 7.5f, 15.5f, 7.5f, 15.5f, 21.5f, 23.5f, 13.5f,
            28.5f, 13.5f, 20.5f, 21.5f, 28.5f, 31.5f, 23.5f, 31.5f,
            17.5f, 24.5f, 15.5f, 26.5f, 15.5f, 31.5f, 11.5f, 31.5f];
        var inside = false;
        for (var i = 0; i < points.Length; i += 2)
        {
            var next = (i + 2) % points.Length;
            var xi = points[i];
            var yi = points[i + 1];
            var xj = points[next];
            var yj = points[next + 1];
            if ((yi > y) == (yj > y)) continue;
            if (x < (xj - xi) * (y - yi) / (yj - yi) + xi)
                inside = !inside;
        }
        return inside;
    }

    // ── P/Invoke structs ──────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
    }

    /// <summary>
    /// Extended NOTIFYICONDATA with balloon-tip fields (NOTIFYICONDATA v4 / Shell32 ≥ 5.0).
    /// Must be laid out exactly as NOTIFYICONDATA followed by the balloon fields — matching
    /// the Windows SDK definition to the byte so Shell_NotifyIconW reads the right offsets.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA_EX
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uTimeout;        // union with uVersion — use 0 for default duration
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX, ptY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    // ── P/Invoke declarations ─────────────────────────────────────────────────

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATA lpData);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "Shell_NotifyIconW")]
    private static extern bool Shell_NotifyIconExW(uint dwMessage, ref NOTIFYICONDATA_EX lpData);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(uint dwExStyle, [MarshalAs(UnmanagedType.LPWStr)] string lpClassName, [MarshalAs(UnmanagedType.LPWStr)] string lpWindowName,
        uint dwStyle, int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hWnd);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, uint msg, int wParam, int lParam);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG lpMsg);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG lpmsg);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT lpPoint);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr hIcon);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, uint uIDNewItem, [MarshalAs(UnmanagedType.LPWStr)] string? lpNewItem);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr hMenu);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll")] private static extern bool TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y, int nReserved, IntPtr hWnd, IntPtr prcRect);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER pbmi,
        uint iUsage, out IntPtr ppvBits, IntPtr hSection, uint dwOffset);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateBitmap(int nWidth, int nHeight, uint cPlanes,
        uint cBitsPerPel, byte[]? lpvBits);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    private static extern IntPtr CreateIconIndirect(ref ICONINFO piconinfo);
}
