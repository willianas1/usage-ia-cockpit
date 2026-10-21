using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using UsageCockpit.Core;
using UsageCockpit.Views;

namespace UsageCockpit;

public partial class OverlayWindow : Window, IOverlayCommands
{
    readonly AppSettings _settings;
    readonly MainViewModel _vm;
    readonly FullView _full = new();
    readonly CompactView _compact = new();
    readonly VerticalView _vertical = new();
    IntPtr _hwnd;
    bool _dragging;

    public event Action? SettingsRequested;
    public event Action? MinimizedToTray;

    public OverlayWindow(AppSettings settings, MainViewModel vm)
    {
        InitializeComponent();
        _settings = settings;
        _vm = vm;
        DataContext = vm;
        ApplyMode();

        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.Mode))
                ApplyMode();
        };

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            Placement.MakeToolWindow(_hwnd);
            // Fundo da superfície DirectX transparente, para o DWM compor o alfa de cada pixel.
            if (HwndSource.FromHwnd(_hwnd)?.CompositionTarget is { } target)
                target.BackgroundColor = Colors.Transparent;
            Placement.DisableWindowFrame(_hwnd);
        };
        Loaded += (_, _) => Reposition();
        IsVisibleChanged += (_, _) => Controls.Ticker.Paused = !IsVisible;

        // Tamanho muda ao trocar de modo ou de escala (DPI): reaplica a posição salva para que
        // a janela continue ancorada no mesmo canto/borda.
        SizeChanged += (_, _) => { if (!_dragging) Reposition(); };
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(Reposition, DispatcherPriority.Loaded);
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) =>
            Dispatcher.BeginInvoke(Reposition, DispatcherPriority.Background);

        MouseLeftButtonDown += OnMouseDown;
        PreviewMouseWheel += OnMouseWheel;
        MouseRightButtonUp += (_, e) => { BuildContextMenu().IsOpen = true; e.Handled = true; };
    }

    void ApplyMode() => Host.Content = _settings.Mode switch
    {
        DisplayMode.Compact => _compact,
        DisplayMode.CompactVertical => _vertical,
        _ => _full,
    };

    public void Reposition()
    {
        if (_hwnd != IntPtr.Zero)
            Placement.Apply(_hwnd, _settings);
    }

    public void KeepOnTop()
    {
        if (_hwnd != IntPtr.Zero && IsVisible)
            Placement.KeepOnTop(_hwnd);
    }

    void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMode();
            return;
        }

        _dragging = true;
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // DragMove só funciona com o botão ainda pressionado; um clique muito rápido cai aqui.
        }
        finally
        {
            _dragging = false;
        }
        Placement.Capture(_hwnd, _settings, VisualTreeHelper.GetDpi(this).DpiScaleX);
        _settings.Save();
    }

    // Durante o redimensionamento o canto superior esquerdo fica fixo (como em qualquer janela);
    // ao soltar, a posição é recapturada e volta a ser ancorada pela fração salva.
    public void BeginResize() => _dragging = true;

    public void ResizeBy(double relativeChange) => _vm.ScaleCurrentMode(s => s * (1 + relativeChange));

    public void EndResize()
    {
        _dragging = false;
        Placement.Capture(_hwnd, _settings, VisualTreeHelper.GetDpi(this).DpiScaleX);
        _settings.Save();
    }

    void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_vm.Mode == DisplayMode.Compact || (Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        _vm.ScaleCurrentMode(s => s + (e.Delta > 0 ? 0.05 : -0.05));
        _settings.Save();
        e.Handled = true;
    }

    public void RefreshNow() => _ = _vm.RefreshWithSweepAsync();

    public void ToggleMode() =>
        _vm.ToggleMode();

    public void OpenSettings() => SettingsRequested?.Invoke();

    /// <summary>Esconde o painel; ele volta pelo clique no ícone da bandeja.</summary>
    public void MinimizeToTray()
    {
        Hide();
        MinimizedToTray?.Invoke();
    }

    public void ShowMonitorMenu(FrameworkElement anchor)
    {
        var menu = new ContextMenu { PlacementTarget = anchor };
        foreach (var item in MonitorItems())
            menu.Items.Add(item);
        menu.IsOpen = true;
    }

    public void MoveToMonitor(string device)
    {
        _settings.MonitorDevice = device;
        _settings.Save();
        Reposition();
    }

    public void MoveToCorner(Corner c)
    {
        Placement.SetCorner(_settings, c);
        _settings.Save();
        Reposition();
    }

    MenuItem[] MonitorItems()
    {
        var current = _settings.MonitorDevice;
        var list = Placement.Monitors();
        var items = new MenuItem[list.Count];
        for (var i = 0; i < list.Count; i++)
        {
            var m = list[i];
            var item = new MenuItem
            {
                Header = m.Label,
                IsCheckable = true,
                IsChecked = m.Device == current || (current == null && m.IsPrimary),
            };
            item.Click += (_, _) => MoveToMonitor(m.Device);
            items[i] = item;
        }
        return items;
    }

    public void Restore()
    {
        Show();
        Reposition();
    }

    Window? _menuHost;

    /// <summary>
    /// Menu do ícone da bandeja: o mesmo do clique direito no painel, aberto no ponteiro do mouse.
    /// O ContextMenuStrip do WinForms calculava a altura errada com escala de DPI e escondia o fim
    /// do menu (incluindo "Sair") atrás da barra de tarefas; o menu do WPF se posiciona certo.
    /// </summary>
    public void ShowTrayMenu()
    {
        // Um menu WPF só fecha ao clicar fora se alguma janela do app estiver ativa; esta janela
        // invisível existe só para isso, já que o painel pode estar oculto.
        _menuHost ??= new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
            ShowInTaskbar = false, Topmost = true, Width = 1, Height = 1, Left = -100, Top = -100,
        };
        _menuHost.Show();
        _menuHost.Activate();

        var menu = BuildContextMenu();
        menu.PlacementTarget = _menuHost;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.Closed += (_, _) => _menuHost.Hide();
        menu.IsOpen = true;
    }

    ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu { PlacementTarget = this };

        MenuItem Item(string header, Action click, bool? check = null)
        {
            var mi = new MenuItem { Header = header };
            if (check != null) { mi.IsCheckable = true; mi.IsChecked = check.Value; }
            mi.Click += (_, _) => click();
            return mi;
        }

        menu.Items.Add(Item("Modo completo", () => _vm.Mode = DisplayMode.Full, _vm.Mode == DisplayMode.Full));
        menu.Items.Add(Item("Compacto horizontal", () => _vm.Mode = DisplayMode.Compact, _vm.Mode == DisplayMode.Compact));
        menu.Items.Add(Item("Compacto vertical", () => _vm.Mode = DisplayMode.CompactVertical,
            _vm.Mode == DisplayMode.CompactVertical));
        menu.Items.Add(new Separator());

        var accounts = new MenuItem { Header = "Contas" };
        accounts.Items.Add(Item("Claude Code", () => _vm.ShowClaude = !_vm.ShowClaude, _vm.ShowClaude));
        accounts.Items.Add(Item("Codex", () => _vm.ShowCodex = !_vm.ShowCodex, _vm.ShowCodex));
        menu.Items.Add(accounts);

        var monitors = new MenuItem { Header = "Monitor" };
        foreach (var mi in MonitorItems())
            monitors.Items.Add(mi);
        menu.Items.Add(monitors);

        var pos = new MenuItem { Header = "Posição" };
        pos.Items.Add(Item("Canto superior esquerdo", () => MoveToCorner(Corner.TopLeft)));
        pos.Items.Add(Item("Canto superior direito", () => MoveToCorner(Corner.TopRight)));
        pos.Items.Add(Item("Canto inferior esquerdo", () => MoveToCorner(Corner.BottomLeft)));
        pos.Items.Add(Item("Canto inferior direito", () => MoveToCorner(Corner.BottomRight)));
        pos.Items.Add(Item("Centro", () => MoveToCorner(Corner.Center)));
        menu.Items.Add(pos);

        var opacity = new MenuItem { Header = "Opacidade do fundo" };
        foreach (var v in new[] { 0, 25, 50, 75, 100 })
        {
            var value = v;
            opacity.Items.Add(Item($"{v}%", () => { _vm.BackgroundOpacity = value; _settings.Save(); },
                _vm.BackgroundOpacity == v));
        }
        menu.Items.Add(opacity);

        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Atualizar agora", RefreshNow));
        menu.Items.Add(Item("Configurações…", OpenSettings));
        // Aberto pela bandeja com o painel oculto, o mesmo item traz o painel de volta.
        menu.Items.Add(IsVisible
            ? Item("Minimizar para a bandeja", MinimizeToTray)
            : Item("Restaurar painel", Restore));
        menu.Items.Add(Item("Sair", ((App)Application.Current).Quit));
        return menu;
    }

    public bool AllowClose { get; set; }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Fechar (Alt+F4) só esconde; para sair de verdade use o menu ou a bandeja.
        if (AllowClose) return;
        e.Cancel = true;
        Hide();
    }
}
