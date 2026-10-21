using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using UsageCockpit.Core;
using UsageCockpit.Views;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace UsageCockpit;

public partial class App : Application
{
    public const string AppName = "Usage IA Cockpit";

    Mutex? _single;
    AppSettings _settings = new();
    MainViewModel? _vm;
    OverlayWindow? _overlay;
    SettingsWindow? _settingsWindow;
    Forms.NotifyIcon? _tray;
    readonly DispatcherTimer _poll = new();
    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(30) };
    readonly DispatcherTimer _activity = new() { Interval = TimeSpan.FromSeconds(1) };

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, ex) =>
        {
            Directory.CreateDirectory(AppSettings.Folder);
            File.AppendAllText(Path.Combine(AppSettings.Folder, "crash.log"), $"[{DateTime.Now:s}] {ex.Exception}\n\n");
        };

        if (e.Args.Length >= 2 && e.Args[0] == "--snapshot")
        {
            await SnapshotAsync(e.Args[1]);
            Shutdown();
            return;
        }

        _single = new Mutex(true, @"Local\UsageCockpit", out var isFirst);
        if (!isFirst)
        {
            Shutdown();
            return;
        }

        _settings = AppSettings.Load();
        SettingsWindow.RepairStartupPath();
        _vm = new MainViewModel(_settings);
        _overlay = new OverlayWindow(_settings, _vm);
        _overlay.SettingsRequested += OpenSettings;
        _overlay.MinimizedToTray += NotifyMinimized;
        _overlay.Show();

        CreateTray();

        _poll.Tick += async (_, _) => await _vm.RefreshAsync(force: false);
        RestartPoll();
        _tick.Tick += (_, _) =>
        {
            _vm.Tick();
            _overlay.KeepOnTop();
            UpdateTrayText();
        };
        _tick.Start();
        _activity.Tick += (_, _) => _vm.CheckActivity();
        _activity.Start();

        await _vm.RefreshAsync(force: true);
        UpdateTrayText();
    }

    void RestartPoll()
    {
        _poll.Stop();
        _poll.Interval = TimeSpan.FromMinutes(Math.Max(1, _settings.PollMinutes));
        _poll.Start();
    }

    void OpenSettings()
    {
        if (_vm == null || _overlay == null) return;
        if (_settingsWindow is { IsLoaded: true })
        {
            _settingsWindow.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow(_settings, _vm, _overlay, RestartPoll);
        _settingsWindow.Show();
    }

    void CreateTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Restaurar / minimizar", null, (_, _) => ToggleOverlay());
        menu.Items.Add("Modo completo", null, (_, _) => _vm!.Mode = DisplayMode.Full);
        menu.Items.Add("Compacto horizontal", null, (_, _) => _vm!.Mode = DisplayMode.Compact);
        menu.Items.Add("Compacto vertical", null, (_, _) => _vm!.Mode = DisplayMode.CompactVertical);
        menu.Items.Add("Atualizar agora", null, async (_, _) => { await _vm!.RefreshWithSweepAsync(); UpdateTrayText(); });
        menu.Items.Add("Configurações…", null, (_, _) => OpenSettings());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Sair", null, (_, _) => Quit());

        _tray = new Forms.NotifyIcon
        {
            Icon = MakeTrayIcon(),
            Text = AppName,
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) ToggleOverlay();
        };
    }

    bool _minimizeNoticeShown;

    /// <summary>Na primeira vez que o painel vai para a bandeja, avisa onde ele ficou.</summary>
    void NotifyMinimized()
    {
        if (_tray == null || _minimizeNoticeShown) return;
        _minimizeNoticeShown = true;
        _tray.ShowBalloonTip(3000, AppName, "Continua rodando aqui na bandeja. Clique no ícone para restaurar.",
            Forms.ToolTipIcon.None);
    }

    void ToggleOverlay()
    {
        if (_overlay == null) return;
        if (_overlay.IsVisible)
            _overlay.Hide();
        else
        {
            _overlay.Show();
            _overlay.Reposition();
        }
    }

    void UpdateTrayText()
    {
        if (_tray == null || _vm == null) return;
        var lines = new System.Collections.Generic.List<string> { AppName };
        if (_settings.ShowClaude) lines.Add(_vm.Claude.ShortStatus);
        if (_settings.ShowCodex) lines.Add(_vm.Codex.ShortStatus);
        var text = string.Join("\n", lines);
        _tray.Text = text.Length > 127 ? text[..127] : text; // limite do Windows para o tooltip da bandeja
    }

    public void Quit()
    {
        if (_overlay != null) _overlay.AllowClose = true;
        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        _settings.Save();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _single?.Dispose();
        base.OnExit(e);
    }

    /// <summary>Ícone da bandeja: o mesmo do executável; se não carregar, desenha um mostrador simples.</summary>
    static Drawing.Icon MakeTrayIcon()
    {
        try
        {
            var res = GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"));
            if (res != null)
                using (res.Stream)
                    return new Drawing.Icon(res.Stream, Forms.SystemInformation.SmallIconSize);
        }
        catch (Exception e) when (e is IOException or ArgumentException)
        {
        }

        using var bmp = new Drawing.Bitmap(32, 32);
        using (var g = Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var bg = new Drawing.SolidBrush(Drawing.Color.FromArgb(0x0F, 0x12, 0x16));
            g.FillEllipse(bg, 1, 1, 30, 30);
            using var track = new Drawing.Pen(Drawing.Color.FromArgb(0x3A, 0x40, 0x48), 4);
            g.DrawArc(track, 5, 5, 22, 22, 135, 270);
            using var arc = new Drawing.Pen(Drawing.Color.FromArgb(0xD9, 0x77, 0x57), 4);
            g.DrawArc(arc, 5, 5, 22, 22, 135, 170);
            using var needle = new Drawing.Pen(Drawing.Color.WhiteSmoke, 2.5f)
            {
                StartCap = Drawing.Drawing2D.LineCap.Round,
                EndCap = Drawing.Drawing2D.LineCap.Round,
            };
            g.DrawLine(needle, 16, 16, 24, 9);
        }
        return Drawing.Icon.FromHandle(bmp.GetHicon());
    }

    /// <summary>
    /// Modo de verificação: busca os dados reais e salva PNGs dos dois modos, com fundo opaco e
    /// translúcido sobre um "papel de parede" de teste. Uso: UsageCockpit.exe --snapshot pasta
    /// </summary>
    static async Task SnapshotAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        var settings = new AppSettings { ReadOnly = true };
        var vm = new MainViewModel(settings);
        await vm.RefreshAsync(force: true);
        vm.CheckActivity();

        foreach (var opacity in new[] { 85, 25 })
        {
            vm.BackgroundOpacity = opacity;
            Save(new FullView { DataContext = vm }, Path.Combine(dir, $"full-{opacity}.png"));
            Save(new CompactView { DataContext = vm }, Path.Combine(dir, $"compact-{opacity}.png"));
            Save(new VerticalView { DataContext = vm }, Path.Combine(dir, $"vertical-{opacity}.png"));
        }

        vm.BackgroundOpacity = 85;
        vm.Claude.ShowAsActiveForPreview();
        Save(new FullView { DataContext = vm }, Path.Combine(dir, "full-claude-active.png"));
        Save(new CompactView { DataContext = vm }, Path.Combine(dir, "compact-claude-active.png"));
        Save(new VerticalView { DataContext = vm }, Path.Combine(dir, "vertical-claude-active.png"));

        // Conteúdo da dica semanal do modo vertical (a dica em si só abre com o mouse).
        if (new VerticalView().Resources["WeekTip"] is ToolTip tip && tip.Content is FrameworkElement tipBody)
        {
            tip.Content = null;
            Save(new Border
            {
                Background = tip.Background, BorderBrush = tip.BorderBrush, BorderThickness = new Thickness(1),
                Padding = tip.Padding, Child = tipBody, DataContext = vm.Claude,
            }, Path.Combine(dir, "vertical-tooltip.png"));
        }

        vm.FullScale = 0.7;
        vm.VerticalScale = 0.6;
        Save(new VerticalView { DataContext = vm }, Path.Combine(dir, "vertical-60pct.png"));
        vm.ShowCodex = false;
        Save(new FullView { DataContext = vm }, Path.Combine(dir, "full-claude-70pct.png"));
        Save(new CompactView { DataContext = vm }, Path.Combine(dir, "compact-claude.png"));

        File.WriteAllText(Path.Combine(dir, "status.txt"),
            $"{vm.Claude.Tooltip}\n\n{vm.Codex.Tooltip}\n");
    }

    static void Save(FrameworkElement view, string path)
    {
        var wallpaper = new Grid { Background = new SolidColorBrush(Color.FromRgb(0x5A, 0x7C, 0x9E)) };
        for (var i = 0; i < 12; i++)
            wallpaper.Children.Add(new TextBlock
            {
                Text = "using System; // texto de outra janela por trás do monitor de uso",
                Foreground = Brushes.White,
                FontSize = 14,
                Margin = new Thickness(4, i * 22, 0, 0),
            });
        view.HorizontalAlignment = HorizontalAlignment.Left;
        view.VerticalAlignment = VerticalAlignment.Top;
        wallpaper.Children.Add(new Border { Padding = new Thickness(20, 40, 20, 20), Child = view });

        wallpaper.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        wallpaper.Arrange(new Rect(wallpaper.DesiredSize));
        wallpaper.UpdateLayout();

        var w = (int)Math.Ceiling(wallpaper.ActualWidth);
        var h = (int)Math.Ceiling(wallpaper.ActualHeight);
        var rtb = new RenderTargetBitmap(w * 2, h * 2, 192, 192, PixelFormats.Pbgra32);
        rtb.Render(wallpaper);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
