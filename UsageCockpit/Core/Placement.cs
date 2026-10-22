using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace UsageCockpit.Core;

public sealed record MonitorInfo(string Device, string Label, bool IsPrimary);

/// <summary>
/// Posicionamento em pixels físicos via Win32. O app roda com DPI PerMonitorV2, então cada
/// monitor tem sua própria escala e o WPF redimensiona a janela sozinho ao trocar de tela.
/// </summary>
public static class Placement
{
    public static IReadOnlyList<MonitorInfo> Monitors()
    {
        var screens = Forms.Screen.AllScreens.OrderBy(s => s.Bounds.Left).ThenBy(s => s.Bounds.Top).ToList();
        return screens.Select((s, i) => new MonitorInfo(
            s.DeviceName,
            $"Monitor {i + 1} — {s.Bounds.Width}×{s.Bounds.Height}{(s.Primary ? " (principal)" : "")}",
            s.Primary)).ToList();
    }

    /// <summary>Monitor salvo nas configurações; se ele não estiver conectado, o principal.</summary>
    static Forms.Screen Target(string? device) =>
        Forms.Screen.AllScreens.FirstOrDefault(s => s.DeviceName == device) ?? Forms.Screen.PrimaryScreen!;

    public static void Apply(IntPtr hwnd, AppSettings s)
    {
        var screen = Target(s.MonitorDevice);
        var wa = screen.WorkingArea;
        GetWindowRect(hwnd, out var r);

        // Se a janela está em outro monitor, primeiro levamos ela para lá: isso dispara a troca de
        // DPI e o WPF ajusta o tamanho. Só então calculamos a posição final com o tamanho certo.
        if (MonitorFromWindow(hwnd) != MonitorFromRect(wa))
        {
            SetWindowPos(hwnd, HwndTopmost, wa.Left, wa.Top, 0, 0, NoSize | NoActivate);
            GetWindowRect(hwnd, out r);
        }

        var w = r.Right - r.Left;
        var h = r.Bottom - r.Top;
        var x = wa.Left + (int)Math.Round(Math.Clamp(s.RelX, 0, 1) * Math.Max(0, wa.Width - w));
        var y = wa.Top + (int)Math.Round(Math.Clamp(s.RelY, 0, 1) * Math.Max(0, wa.Height - h));
        SetWindowPos(hwnd, HwndTopmost, x, y, 0, 0, NoSize | NoActivate);
    }

    /// <summary>
    /// Depois de um arraste: descobre em qual monitor a janela ficou, gruda nas bordas próximas,
    /// mantém dentro da área útil e grava a posição como fração da área livre.
    /// </summary>
    public static void Capture(IntPtr hwnd, AppSettings s, double dpiScale)
    {
        GetWindowRect(hwnd, out var r);
        var screen = Forms.Screen.FromHandle(hwnd);
        var wa = screen.WorkingArea;
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        int x = r.Left, y = r.Top;

        if (s.SnapToEdges)
        {
            var snap = (int)Math.Round(24 * dpiScale);
            if (Math.Abs(x - wa.Left) < snap) x = wa.Left;
            if (Math.Abs(wa.Right - (x + w)) < snap) x = wa.Right - w;
            if (Math.Abs(y - wa.Top) < snap) y = wa.Top;
            if (Math.Abs(wa.Bottom - (y + h)) < snap) y = wa.Bottom - h;
        }

        x = Math.Clamp(x, wa.Left, Math.Max(wa.Left, wa.Right - w));
        y = Math.Clamp(y, wa.Top, Math.Max(wa.Top, wa.Bottom - h));

        s.MonitorDevice = screen.DeviceName;
        s.RelX = wa.Width > w ? (double)(x - wa.Left) / (wa.Width - w) : 0;
        s.RelY = wa.Height > h ? (double)(y - wa.Top) / (wa.Height - h) : 0;
        SetWindowPos(hwnd, HwndTopmost, x, y, 0, 0, NoSize | NoActivate);
    }

    public static void SetCorner(AppSettings s, Corner c)
    {
        (s.RelX, s.RelY) = c switch
        {
            Corner.TopLeft => (0.0, 0.0),
            Corner.TopRight => (1.0, 0.0),
            Corner.BottomLeft => (0.0, 1.0),
            Corner.BottomRight => (1.0, 1.0),
            _ => (0.5, 0.5),
        };
    }

    /// <summary>Some aplicativos em tela cheia "roubam" o topo. Reafirma a janela como sempre visível.</summary>
    public static void KeepOnTop(IntPtr hwnd) =>
        SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, NoSize | NoMove | NoActivate);

    /// <summary>Tira a janela do Alt+Tab e da barra de tarefas.</summary>
    public static void MakeToolWindow(IntPtr hwnd)
    {
        var ex = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr((ex | WsExToolWindow) & ~WsExAppWindow));
    }

    /// <summary>
    /// No Windows 11 o DWM arredonda os cantos e desenha borda e sombra em toda janela.
    /// O painel já tem seus próprios cantos e borda, então desligamos os do sistema.
    /// </summary>
    public static void DisableWindowFrame(IntPtr hwnd)
    {
        int doNotRound = 1, noColor = unchecked((int)0xFFFFFFFE);
        DwmSetWindowAttribute(hwnd, 33, ref doNotRound, sizeof(int));  // DWMWA_WINDOW_CORNER_PREFERENCE
        DwmSetWindowAttribute(hwnd, 34, ref noColor, sizeof(int));     // DWMWA_BORDER_COLOR = nenhuma
    }

    /// <summary>
    /// O Windows impõe uma largura mínima de janela (~136 px a 100%), maior que o compacto vertical:
    /// sobrava espaço vazio nas laterais. Zeramos o mínimo para a janela ter o tamanho do conteúdo.
    /// </summary>
    public static IntPtr NoMinimumSize(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WmGetMinMaxInfo = 0x0024;
        if (msg != WmGetMinMaxInfo) return IntPtr.Zero;
        var info = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        info.MinTrackSize = new Point { X = 1, Y = 1 };
        Marshal.StructureToPtr(info, lParam, false);
        handled = true;
        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Point { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    struct MinMaxInfo { public Point Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    static IntPtr MonitorFromRect(System.Drawing.Rectangle rc)
    {
        var r = new Rect { Left = rc.Left, Top = rc.Top, Right = rc.Right, Bottom = rc.Bottom };
        return MonitorFromRect(ref r, 2);
    }

    static IntPtr MonitorFromWindow(IntPtr hwnd) => MonitorFromWindow(hwnd, 2);

    static readonly IntPtr HwndTopmost = new(-1);
    const uint NoSize = 0x0001, NoMove = 0x0002, NoActivate = 0x0010;
    const int GwlExStyle = -20;
    const long WsExToolWindow = 0x80, WsExAppWindow = 0x40000;

    [StructLayout(LayoutKind.Sequential)]
    struct Rect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromRect(ref Rect rect, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);
}
