using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Kautuka.Monitor;

/// <summary>
/// Tray icon and a small status window. Quit leaves the scheduled task and the appliance running.
/// The window is painted directly. It does not use WinForms or the system button and label controls.
/// </summary>
internal static class MonitorTray
{
    private const string mutexName = @"Local\Kautuka.Monitor";
    private const string className = "KautukaMonitorWindow";
    private const uint wmAppTray = 0x8001;
    private const uint wmAppRefresh = 0x8002;
    private const uint wmDestroy = 0x0002;
    private const uint wmClose = 0x0010;
    private const uint wmPaint = 0x000F;
    private const uint wmErase = 0x0014;
    private const uint wmCommand = 0x0111;
    private const uint wmTimer = 0x0113;
    private const uint wmSetCursor = 0x0020;
    private const uint wmMouseMove = 0x0200;
    private const uint wmLButtonUp = 0x0202;
    private const uint wmMouseLeave = 0x02A1;
    private const int wmLbuttonDblclk = 0x0203;
    private const int wmRbuttonUp = 0x0205;
    private const int swHide = 0;
    private const int swShow = 5;
    private const uint nimAdd = 0;
    private const uint nimModify = 1;
    private const uint nimDelete = 2;
    private const uint nifMessage = 0x1;
    private const uint nifIcon = 0x2;
    private const uint nifTip = 0x4;
    private const int mfString = 0;
    private const int mfSeparator = 0x800;
    private const uint tpmRightbutton = 0x2;
    private const uint wsOverlappedWindow = 0x00CF0000;
    private const int idOpen = 1;
    private const int idRestart = 2;
    private const int idQuit = 3;
    private const int idShow = 5;
    private const int dtWordBreak = 0x10;
    private const int dtNoPrefix = 0x800;
    private const int dtCenter = 0x01;
    private const int dtVCenter = 0x04;
    private const int dtSingleLine = 0x20;

    private static readonly HttpClient probe = new() { Timeout = TimeSpan.FromSeconds(3) };
    private static IntPtr hwnd;
    private static IntPtr icon;
    private static IntPtr windowIcon;
    private static IntPtr titleFont;
    private static IntPtr bodyFont;
    private static IntPtr noteFont;
    private static int fontDpi;
    private static string distroName = AppliancePaths.DefaultDistroName;
    private static string installDir = string.Empty;
    private static string heading = "Checking";
    private static string detail = "Looking for the appliance.";
    private static string note = string.Empty;
    private static bool webUp;
    private static bool lightTheme = true;
    private static int restarting;
    private static int hoverButton = -1;
    private static bool trackingLeave;
    private static Rect openRect;
    private static Rect restartRect;
    private static Rect quitRect;

    public static unsafe int Run(string distro, string applianceInstallDir)
    {
        distroName = distro;
        installDir = applianceInstallDir;
        lightTheme = ReadLightTheme();

        Mutex mutex;
        bool created;
        try
        {
            mutex = new Mutex(initiallyOwned: true, mutexName, out created);
        }
        catch (AbandonedMutexException)
        {
            mutex = new Mutex(initiallyOwned: false, mutexName, out _);
            created = true;
        }

        if (!created)
        {
            mutex.Dispose();
            return 0;
        }

        using (mutex)
        {
            icon = CreateMonogram(TrayIconSize(small: true));
            windowIcon = CreateMonogram(TrayIconSize(small: false));
            var windowClass = new WndClassEx
            {
                cbSize = Marshal.SizeOf<WndClassEx>(),
                lpfnWndProc = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, IntPtr, IntPtr>)&WindowProc,
                hInstance = GetModuleHandle(null),
                lpszClassName = className,
                hCursor = LoadCursor(IntPtr.Zero, new IntPtr(32512)),
                hIcon = windowIcon,
                hIconSm = icon,
            };
            RegisterClassEx(ref windowClass);

            hwnd = CreateWindowEx(
                0,
                className,
                "Kautuka",
                wsOverlappedWindow,
                120,
                120,
                480,
                280,
                IntPtr.Zero,
                IntPtr.Zero,
                windowClass.hInstance,
                IntPtr.Zero);
            if (hwnd == IntPtr.Zero)
                return 1;

            int dark = lightTheme ? 0 : 1;
            _ = DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));

            var notify = NotifyIcon();
            ShellNotifyIcon(nimAdd, ref notify);
            SetTimer(hwnd, 1, 5000, IntPtr.Zero);
            _ = RefreshAsync();

            while (GetMessage(out Msg message, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }

            ShellNotifyIcon(nimDelete, ref notify);
            ReleaseFonts();
            if (icon != IntPtr.Zero)
                DestroyIcon(icon);
            if (windowIcon != IntPtr.Zero && windowIcon != icon)
                DestroyIcon(windowIcon);
        }

        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static IntPtr WindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == wmAppTray)
        {
            int evt = lParam.ToInt32() & 0xFFFF;
            if (evt == wmLbuttonDblclk)
                Show();
            else if (evt == wmRbuttonUp)
                ShowMenu();
            return IntPtr.Zero;
        }

        if (message == wmAppRefresh || message == wmTimer)
        {
            if (message == wmTimer)
                _ = RefreshAsync();
            InvalidateRect(window, IntPtr.Zero, false);
            var notify = NotifyIcon();
            ShellNotifyIcon(nimModify, ref notify);
            return IntPtr.Zero;
        }

        if (message == wmErase)
            return new IntPtr(1);

        if (message == wmPaint)
        {
            Paint(window);
            return IntPtr.Zero;
        }

        if (message == wmMouseMove)
        {
            EnsureLeaveTracking(window);
            int next = HitButton(MousePoint(lParam));
            if (next != hoverButton)
            {
                hoverButton = next;
                InvalidateRect(window, IntPtr.Zero, false);
            }

            return IntPtr.Zero;
        }

        if (message == wmMouseLeave)
        {
            trackingLeave = false;
            if (hoverButton != -1)
            {
                hoverButton = -1;
                InvalidateRect(window, IntPtr.Zero, false);
            }

            return IntPtr.Zero;
        }

        if (message == wmLButtonUp)
        {
            switch (HitButton(MousePoint(lParam)))
            {
                case 0:
                    OpenAppliance();
                    break;
                case 1:
                    Restart();
                    break;
                case 2:
                    DestroyWindow(window);
                    break;
            }

            return IntPtr.Zero;
        }

        if (message == wmSetCursor && hoverButton >= 0)
        {
            SetCursor(LoadCursor(IntPtr.Zero, new IntPtr(32649)));
            return new IntPtr(1);
        }

        if (message == wmCommand)
        {
            switch (wParam.ToInt32() & 0xFFFF)
            {
                case idShow:
                    Show();
                    break;
                case idOpen:
                    OpenAppliance();
                    break;
                case idRestart:
                    Restart();
                    break;
                case idQuit:
                    DestroyWindow(window);
                    break;
            }

            return IntPtr.Zero;
        }

        if (message == wmClose)
        {
            ShowWindow(window, swHide);
            return IntPtr.Zero;
        }

        if (message == wmDestroy)
        {
            PostQuitMessage(0);
            return IntPtr.Zero;
        }

        return DefWindowProc(window, message, wParam, lParam);
    }

    private static void Paint(IntPtr window)
    {
        BeginPaint(window, out PaintStruct paint);
        GetClientRect(window, out Rect client);
        int dpi = GetDpiForWindow(window);
        if (dpi <= 0)
            dpi = 96;

        EnsureFonts(dpi);
        Layout(client, dpi, out openRect, out restartRect, out quitRect);
        Palette palette = lightTheme ? Palette.Light : Palette.Dark;

        IntPtr buffer = CreateCompatibleDC(paint.hdc);
        IntPtr bitmap = CreateCompatibleBitmap(paint.hdc, client.Right, client.Bottom);
        IntPtr previousBitmap = SelectObject(buffer, bitmap);
        IntPtr background = CreateSolidBrush(palette.Background);
        _ = FillRect(buffer, ref client, background);
        DeleteObject(background);

        int pad = Scale(28, dpi);
        int textLeft = pad + Scale(16, dpi);
        var text = new Rect(textLeft, Scale(28, dpi), client.Right - pad, Scale(64, dpi));
        DrawDot(buffer, pad, Scale(36, dpi), Scale(8, dpi), StatusColor(palette));
        DrawLine(buffer, titleFont, palette.Foreground, heading, text, dtSingleLine | dtNoPrefix);

        text = new Rect(pad, Scale(68, dpi), client.Right - pad, Scale(118, dpi));
        DrawLine(buffer, bodyFont, palette.Muted, detail, text, dtWordBreak | dtNoPrefix);

        text = new Rect(pad, Scale(122, dpi), client.Right - pad, restartRect.Top - Scale(16, dpi));
        DrawLine(buffer, noteFont, palette.Muted, note, text, dtWordBreak | dtNoPrefix);

        int primary = webUp ? 0 : 1;
        DrawButton(buffer, openRect, "Open", 0, primary, dpi, palette);
        DrawButton(buffer, restartRect, "Restart", 1, primary, dpi, palette);
        DrawButton(buffer, quitRect, "Quit", 2, primary, dpi, palette);

        BitBlt(paint.hdc, 0, 0, client.Right, client.Bottom, buffer, 0, 0, 0x00CC0020);
        SelectObject(buffer, previousBitmap);
        DeleteObject(bitmap);
        DeleteDC(buffer);
        EndPaint(window, ref paint);
    }

    private static void DrawDot(IntPtr dc, int x, int y, int size, int color)
    {
        IntPtr brush = CreateSolidBrush(color);
        IntPtr pen = CreatePen(0, 1, color);
        IntPtr oldBrush = SelectObject(dc, brush);
        IntPtr oldPen = SelectObject(dc, pen);
        Ellipse(dc, x, y, x + size, y + size);
        SelectObject(dc, oldPen);
        SelectObject(dc, oldBrush);
        DeleteObject(pen);
        DeleteObject(brush);
    }

    private static void DrawLine(IntPtr dc, IntPtr font, int color, string text, Rect bounds, int format)
    {
        IntPtr old = SelectObject(dc, font);
        _ = SetBkMode(dc, 1);
        _ = SetTextColor(dc, color);
        _ = DrawText(dc, text, text.Length, ref bounds, format);
        SelectObject(dc, old);
    }

    private static void DrawButton(IntPtr dc, Rect bounds, string label, int index, int primary, int dpi, Palette palette)
    {
        bool filled = index == primary;
        bool hot = hoverButton == index;
        int fill = filled
            ? palette.Primary
            : hot ? palette.Accent : palette.Surface;
        int ink = filled ? palette.OnPrimary : palette.Foreground;
        int edge = filled ? palette.Primary : palette.Border;
        int radius = Scale(8, dpi);

        IntPtr brush = CreateSolidBrush(fill);
        IntPtr pen = CreatePen(0, 1, edge);
        IntPtr oldBrush = SelectObject(dc, brush);
        IntPtr oldPen = SelectObject(dc, pen);
        RoundRect(dc, bounds.Left, bounds.Top, bounds.Right, bounds.Bottom, radius, radius);
        SelectObject(dc, oldPen);
        SelectObject(dc, oldBrush);
        DeleteObject(pen);
        DeleteObject(brush);
        DrawLine(dc, bodyFont, ink, label, bounds, dtCenter | dtVCenter | dtSingleLine | dtNoPrefix);
    }

    private static int StatusColor(Palette palette)
    {
        if (restarting == 1)
            return palette.Muted;

        if (heading.StartsWith("Blocked", StringComparison.Ordinal))
            return palette.Danger;

        return webUp ? palette.Primary : palette.Muted;
    }

    private static void Layout(Rect client, int dpi, out Rect open, out Rect restart, out Rect quit)
    {
        int pad = Scale(28, dpi);
        int gap = Scale(10, dpi);
        int height = Scale(36, dpi);
        int top = client.Bottom - pad - height;
        int width = (client.Right - (pad * 2) - (gap * 2)) / 3;
        open = new Rect(pad, top, pad + width, top + height);
        int second = pad + width + gap;
        restart = new Rect(second, top, second + width, top + height);
        int third = second + width + gap;
        quit = new Rect(third, top, third + width, top + height);
    }

    private static int HitButton(Point point)
    {
        if (Contains(openRect, point))
            return 0;

        if (Contains(restartRect, point))
            return 1;

        return Contains(quitRect, point) ? 2 : -1;
    }

    private static bool Contains(Rect rect, Point point)
        => point.X >= rect.Left && point.X < rect.Right && point.Y >= rect.Top && point.Y < rect.Bottom;

    private static Point MousePoint(IntPtr lParam)
    {
        int packed = lParam.ToInt32();
        return new Point((short)(packed & 0xFFFF), (short)((packed >> 16) & 0xFFFF));
    }

    private static void EnsureLeaveTracking(IntPtr window)
    {
        if (trackingLeave)
            return;

        var track = new TrackMouse
        {
            cbSize = (uint)Marshal.SizeOf<TrackMouse>(),
            dwFlags = 2,
            hwndTrack = window,
        };
        trackingLeave = TrackMouseEvent(ref track);
    }

    private static void EnsureFonts(int dpi)
    {
        if (fontDpi == dpi && titleFont != IntPtr.Zero)
            return;

        ReleaseFonts();
        fontDpi = dpi;
        titleFont = MakeFont(22, 600, dpi, "Segoe UI Variable Display");
        bodyFont = MakeFont(15, 400, dpi, "Segoe UI Variable Text");
        noteFont = MakeFont(13, 400, dpi, "Segoe UI Variable Text");
    }

    private static IntPtr MakeFont(int pointSize, int weight, int dpi, string face)
        => CreateFont(-Scale(pointSize, dpi), 0, 0, 0, weight, 0, 0, 0, 1, 0, 0, 5, 0, face);

    private static void ReleaseFonts()
    {
        if (titleFont != IntPtr.Zero)
            DeleteObject(titleFont);
        if (bodyFont != IntPtr.Zero)
            DeleteObject(bodyFont);
        if (noteFont != IntPtr.Zero)
            DeleteObject(noteFont);
        titleFont = IntPtr.Zero;
        bodyFont = IntPtr.Zero;
        noteFont = IntPtr.Zero;
        fontDpi = 0;
    }

    private static int Scale(int px, int dpi) => px * dpi / 96;

    private static int TrayIconSize(bool small)
    {
        int size = GetSystemMetrics(small ? 49 : 11);
        int minimum = small ? 16 : 32;
        return size < minimum ? minimum : size;
    }

    /// <summary>The Kautuka gradient mark used for the tray and window frame.</summary>
    private static unsafe IntPtr CreateMonogram(int size)
    {
        IntPtr screen = GetDC(IntPtr.Zero);
        IntPtr colorDc = CreateCompatibleDC(screen);
        IntPtr color = CreateCompatibleBitmap(screen, size, size);
        IntPtr previousColor = SelectObject(colorDc, color);

        var radius = Math.Max(1, (int)MathF.Round(size * 0.27f));
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var nearestX = Math.Clamp(x, radius, size - radius - 1);
                var nearestY = Math.Clamp(y, radius, size - radius - 1);
                var dx = x - nearestX;
                var dy = y - nearestY;
                if (dx * dx + dy * dy > radius * radius) continue;

                var position = x / (float)Math.Max(1, size - 1);
                var blend = position <= 0.5f ? position * 2 : (1 - position) * 2;
                var green = (int)MathF.Round(0x42 + (0xA0 - 0x42) * blend);
                var blue = (int)MathF.Round(0x34 + (0x18 - 0x34) * blend);
                var colorRef = (uint)(0xE3 | (green << 8) | (blue << 16));
                _ = SetPixelV(colorDc, x, y, colorRef);
            }
        }

        var viewScale = 40f / size;
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var viewX = (x + 0.5f) * viewScale;
                var viewY = (y + 0.5f) * viewScale;
                if (ContainsBrandLetter(viewX, viewY - 1))
                {
                    var position = x / (float)Math.Max(1, size - 1);
                    var blend = position <= 0.5f ? position * 2 : (1 - position) * 2;
                    var green = (int)MathF.Round((0x42 + (0xA0 - 0x42) * blend) * 0.65f);
                    var blue = (int)MathF.Round((0x34 + (0x18 - 0x34) * blend) * 0.65f);
                    var red = (int)MathF.Round(0xE3 * 0.65f);
                    _ = SetPixelV(colorDc, x, y, (uint)(red | (green << 8) | (blue << 16)));
                }
                if (ContainsBrandLetter(viewX, viewY))
                    _ = SetPixelV(colorDc, x, y, 0xFFFFFF);
            }
        }
        SelectObject(colorDc, previousColor);

        IntPtr maskDc = CreateCompatibleDC(screen);
        var maskStride = ((size + 31) / 32) * 4;
        var maskBytes = new byte[maskStride * size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var nearestX = Math.Clamp(x, radius, size - radius - 1);
                var nearestY = Math.Clamp(y, radius, size - radius - 1);
                var dx = x - nearestX;
                var dy = y - nearestY;
                if (dx * dx + dy * dy <= radius * radius) continue;
                maskBytes[y * maskStride + x / 8] |= (byte)(0x80 >> (x % 8));
            }
        }
        IntPtr mask;
        fixed (byte* maskPointer = maskBytes)
            mask = CreateBitmap(size, size, 1, 1, (IntPtr)maskPointer);
        IntPtr previousMask = SelectObject(maskDc, mask);
        SelectObject(maskDc, previousMask);

        var info = new IconInfo
        {
            fIcon = 1,
            hbmColor = color,
            hbmMask = mask,
        };
        IntPtr created = CreateIconIndirect(ref info);

        DeleteObject(color);
        DeleteObject(mask);
        DeleteDC(colorDc);
        DeleteDC(maskDc);
        _ = ReleaseDC(IntPtr.Zero, screen);
        return created;
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

    private static void Show()
    {
        ShowWindow(hwnd, swShow);
        SetForegroundWindow(hwnd);
    }

    private static void ShowMenu()
    {
        IntPtr menu = CreatePopupMenu();
        AppendMenu(menu, mfString, idShow, "Status");
        AppendMenu(menu, mfString, idOpen, "Open Kautuka");
        AppendMenu(menu, mfString, idRestart, "Restart appliance");
        AppendMenu(menu, mfSeparator, 0, null);
        AppendMenu(menu, mfString, idQuit, "Quit");
        GetCursorPos(out Point point);
        SetForegroundWindow(hwnd);
        TrackPopupMenu(menu, tpmRightbutton, point.X, point.Y, 0, hwnd, IntPtr.Zero);
        DestroyMenu(menu);
    }

    private static void OpenAppliance()
    {
        if (!webUp)
        {
            heading = "Stopped";
            detail = "Kautuka is not answering on port 20229.";
            note = "Restart the appliance, then open it.";
            InvalidateRect(hwnd, IntPtr.Zero, false);
            Show();
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = AppliancePaths.WebUrl,
            UseShellExecute = true,
        });
    }

    private static void Restart()
    {
        if (Interlocked.Exchange(ref restarting, 1) == 1)
            return;

        heading = "Restarting";
        detail = "Starting the appliance.";
        note = string.Empty;
        InvalidateRect(hwnd, IntPtr.Zero, false);
        _ = Task.Run(async () =>
        {
            try
            {
                await ApplianceSupervisor.RunAsync(
                    distroName,
                    installDir,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                heading = "Stopped";
                detail = "Restart failed.";
                note = string.Empty;
            }
            catch (IOException)
            {
                heading = "Stopped";
                detail = "Restart failed.";
                note = string.Empty;
            }
            finally
            {
                Interlocked.Exchange(ref restarting, 0);
                await RefreshAsync().ConfigureAwait(false);
            }
        });
    }

    private static async Task RefreshAsync()
    {
        bool up = await ProbeWebAsync().ConfigureAwait(false);
        webUp = up;
        DescribeStatus(up);

        if (hwnd != IntPtr.Zero)
            PostMessage(hwnd, wmAppRefresh, IntPtr.Zero, IntPtr.Zero);
    }

    private static async Task<bool> ProbeWebAsync()
    {
        try
        {
            using HttpResponseMessage response = await probe.GetAsync(new Uri(AppliancePaths.WebUrl)).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException)
        {
            return false;
        }
    }

    private static void DescribeStatus(bool up)
    {
        if (restarting == 1)
        {
            heading = "Restarting";
            detail = "Starting the appliance.";
            note = string.Empty;
            return;
        }

        string? last = ReadLastSupervisorLine();
        string? breaking = ExtractBreaking(last);
        note = DescribeSupervisor(last);
        if (breaking is not null)
        {
            heading = "Blocked";
            detail = breaking;
            return;
        }

        if (up)
        {
            heading = "Up";
            detail = "Answering at localhost:20229.";
            return;
        }

        heading = "Stopped";
        detail = "Restart brings the appliance back.";
    }

    private static string? ReadLastSupervisorLine()
    {
        string path = ApplianceSupervisor.LogPath(installDir);
        if (!File.Exists(path))
            return null;

        string[] lines = File.ReadAllLines(path);
        return lines.Length == 0 ? null : lines[^1];
    }

    private static string? ExtractBreaking(string? last)
    {
        if (last is null)
            return null;

        int marker = last.IndexOf("[Breaking ", StringComparison.Ordinal);
        if (marker < 0)
            return null;

        int end = last.IndexOf(']', marker);
        return end > marker ? last[marker..end] : "A host setting is blocking the appliance.";
    }

    private static string DescribeSupervisor(string? last)
    {
        if (last is null)
            return "No supervisor pass yet.";

        int error = last.IndexOf(" error=", StringComparison.Ordinal);
        if (error >= 0)
            return "Last supervisor: " + last[(error + " error=".Length)..].Trim();

        return last.Contains("keepaliveStarted=True", StringComparison.Ordinal)
            ? "Last supervisor: started a keepalive."
            : "Last supervisor: keepalive already held.";
    }

    private static bool ReadLightTheme()
    {
        using Microsoft.Win32.RegistryKey? key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is not int value || value != 0;
    }

    private static NotifyIconData NotifyIcon()
        => new()
        {
            cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
            hWnd = hwnd,
            uID = 1,
            uFlags = nifMessage | nifIcon | nifTip,
            uCallbackMessage = wmAppTray,
            hIcon = icon,
            szTip = "Kautuka — " + heading,
        };

    private readonly struct Palette
    {
        public int Background { get; init; }
        public int Surface { get; init; }
        public int Foreground { get; init; }
        public int Muted { get; init; }
        public int Primary { get; init; }
        public int OnPrimary { get; init; }
        public int Border { get; init; }
        public int Accent { get; init; }
        public int Danger { get; init; }

        public static Palette Light { get; } = new()
        {
            Background = Rgb(251, 249, 246),
            Surface = Rgb(255, 255, 255),
            Foreground = Rgb(27, 28, 26),
            Muted = Rgb(69, 72, 66),
            Primary = Rgb(87, 94, 83),
            OnPrimary = Rgb(255, 255, 255),
            Border = Rgb(197, 199, 192),
            Accent = Rgb(234, 232, 229),
            Danger = Rgb(186, 26, 26),
        };

        public static Palette Dark { get; } = new()
        {
            Background = Rgb(10, 10, 12),
            Surface = Rgb(20, 20, 23),
            Foreground = Rgb(248, 250, 252),
            Muted = Rgb(148, 163, 184),
            Primary = Rgb(209, 196, 233),
            OnPrimary = Rgb(10, 10, 12),
            Border = Rgb(30, 30, 34),
            Accent = Rgb(28, 28, 33),
            Danger = Rgb(255, 180, 171),
        };

        private static int Rgb(int red, int green, int blue) => red | (green << 8) | (blue << 16);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public int cbSize;
        public int style;
        public IntPtr lpfnWndProc;
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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
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

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public Point(int x, int y)
        {
            X = x;
            Y = y;
        }

        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public Rect(int left, int top, int right, int bottom)
        {
            Left = left;
            Top = top;
            Right = right;
            Bottom = bottom;
        }

        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PaintStruct
    {
        public IntPtr hdc;
        public int fErase;
        public Rect rcPaint;
        public int fRestore;
        public int fIncUpdate;
        public ulong reserved0;
        public ulong reserved1;
        public ulong reserved2;
        public ulong reserved3;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        public int fIcon;
        public uint xHotspot;
        public uint yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TrackMouse
    {
        public uint cbSize;
        public uint dwFlags;
        public IntPtr hwndTrack;
        public uint dwHoverTime;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", EntryPoint = "RegisterClassExW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WndClassEx lpwcx);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        uint dwExStyle,
        string lpClassName,
        string lpWindowName,
        uint dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", EntryPoint = "DefWindowProcW", ExactSpelling = true)]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int nExitCode);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    private static extern int GetMessage(out Msg lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Msg lpMsg);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref Msg lpMsg);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    private static extern IntPtr LoadCursor(IntPtr hInstance, IntPtr lpCursorName);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    private static extern IntPtr SetCursor(IntPtr hCursor);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    private static extern int FrameRect(IntPtr hdc, ref Rect rect, IntPtr brush);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    private static extern IntPtr CreateIconIndirect(ref IconInfo info);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    private static extern IntPtr SetTimer(IntPtr hWnd, IntPtr nIDEvent, uint uElapse, IntPtr lpTimerFunc);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point lpPoint);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", EntryPoint = "AppendMenuW", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr hMenu, int uFlags, nuint uIDNewItem, string? lpNewItem);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    private static extern bool TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y, int nReserved, IntPtr hWnd, IntPtr prcRect);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    private static extern bool InvalidateRect(IntPtr hWnd, IntPtr rect, bool erase);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out Rect rect);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    private static extern int GetDpiForWindow(IntPtr hWnd);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    private static extern IntPtr BeginPaint(IntPtr hWnd, out PaintStruct paint);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    private static extern bool EndPaint(IntPtr hWnd, ref PaintStruct paint);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", EntryPoint = "DrawTextW", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int DrawText(IntPtr hdc, string text, int length, ref Rect bounds, int format);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    private static extern bool TrackMouseEvent(ref TrackMouse track);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern bool ShellNotifyIcon(uint dwMessage, ref NotifyIconData lpData);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateSolidBrush(int color);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreatePen(int style, int width, int color);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("gdi32.dll", EntryPoint = "CreateFontW", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFont(
        int height,
        int width,
        int escapement,
        int orientation,
        int weight,
        uint italic,
        uint underline,
        uint strikeOut,
        uint charSet,
        uint outPrecision,
        uint clipPrecision,
        uint quality,
        uint pitchAndFamily,
        string face);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr ho);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("gdi32.dll")]
    private static extern int SetBkMode(IntPtr hdc, int mode);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("gdi32.dll")]
    private static extern int SetTextColor(IntPtr hdc, int color);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    private static extern int FillRect(IntPtr hdc, ref Rect rect, IntPtr brush);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("gdi32.dll")]
    private static extern bool RoundRect(IntPtr hdc, int left, int top, int right, int bottom, int width, int height);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("gdi32.dll")]
    private static extern bool SetPixelV(IntPtr hdc, int x, int y, uint color);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("gdi32.dll")]
    private static extern bool Ellipse(IntPtr hdc, int left, int top, int right, int bottom);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr dest, int x, int y, int width, int height, IntPtr src, int srcX, int srcY, uint rop);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateBitmap(int width, int height, uint planes, uint bitCount, IntPtr bits);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("gdi32.dll")]
    private static extern bool PatBlt(IntPtr hdc, int x, int y, int width, int height, uint rop);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
