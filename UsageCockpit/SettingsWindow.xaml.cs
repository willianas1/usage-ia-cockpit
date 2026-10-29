using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Microsoft.Win32;
using UsageCockpit.Core;

namespace UsageCockpit;

public partial class SettingsWindow : Window
{
    static readonly int[] PollOptions = { 30, 60, 120, 300, 600, 900, 1800 };
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValue = "UsageCockpit";

    readonly AppSettings _settings;
    readonly MainViewModel _vm;
    readonly OverlayWindow _overlay;
    readonly Action _pollChanged;
    readonly UsagePublisher _publisher;
    readonly HealthPublisher _health;
    readonly bool _loading;

    public SettingsWindow(AppSettings settings, MainViewModel vm, OverlayWindow overlay, Action pollChanged,
        UsagePublisher publisher, HealthPublisher health)
    {
        InitializeComponent();
        _settings = settings;
        _vm = vm;
        _overlay = overlay;
        _pollChanged = pollChanged;
        _publisher = publisher;
        _health = health;
        ShowPublishStatus();
        publisher.OutcomeChanged += ShowPublishStatus;
        ShowHealth();
        ShowHealthStatus();
        health.Monitor.Sampled += ShowHealth;
        health.OutcomeChanged += ShowHealthStatus;

        _loading = true;
        FullMode.IsChecked = vm.Size == PanelSize.Full;
        CompactMode.IsChecked = vm.Size == PanelSize.Compact;
        UltraMode.IsChecked = vm.Size == PanelSize.Ultra;
        HorizontalShape.IsChecked = !vm.Vertical;
        VerticalShape.IsChecked = vm.Vertical;
        DarkTheme.IsChecked = settings.Theme == AppTheme.Dark;
        LightTheme.IsChecked = settings.Theme == AppTheme.Light;
        OpacitySlider.Value = settings.BackgroundOpacity;
        OpacityLabel.Text = $"{settings.BackgroundOpacity}%";
        SnapCheck.IsChecked = settings.SnapToEdges;
        StaticNeedlesCheck.IsChecked = settings.StaticNeedles;
        ClaudeCheck.IsChecked = settings.ShowClaude;
        CodexCheck.IsChecked = settings.ShowCodex;
        ScaleSlider.Value = Math.Round(settings.FullScale * 100);
        ScaleLabel.Text = $"{Math.Round(settings.FullScale * 100)}%";
        VerticalScaleSlider.Value = Math.Round(settings.VerticalScale * 100);
        VerticalScaleLabel.Text = $"{Math.Round(settings.VerticalScale * 100)}%";
        StartupCheck.IsChecked = IsStartupEnabled();

        var monitors = Placement.Monitors();
        MonitorCombo.ItemsSource = monitors;
        MonitorCombo.SelectedItem = monitors.FirstOrDefault(m => m.Device == settings.MonitorDevice)
                                    ?? monitors.FirstOrDefault(m => m.IsPrimary);

        PollCombo.ItemsSource = PollOptions.Select(s => s < 60 ? $"{s} s" : $"{s / 60} min").ToList();
        PollCombo.SelectedIndex = Math.Max(0, Array.IndexOf(PollOptions, settings.PollSeconds));

        var times = Enumerable.Range(0, 48).Select(i => TimeSpan.FromMinutes(i * 30)).ToList();
        WorkStartCombo.ItemsSource = times;
        WorkEndCombo.ItemsSource = times;
        WorkStartCombo.SelectedItem = settings.WorkStart;
        WorkEndCombo.SelectedItem = settings.WorkEnd;
        WorkHoursCheck.IsChecked = settings.WorkHoursEnabled;
        HealthCheck.IsChecked = settings.HealthEnabled;
        HealthPublishCheck.IsChecked = settings.HealthPublishEnabled;
        _loading = false;
    }

    void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _vm.Size = FullMode.IsChecked == true ? PanelSize.Full
            : UltraMode.IsChecked == true ? PanelSize.Ultra
            : PanelSize.Compact;
    }

    void Shape_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _vm.Vertical = VerticalShape.IsChecked == true;
    }

    void Theme_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _vm.Theme = LightTheme.IsChecked == true ? AppTheme.Light : AppTheme.Dark;
    }

    void Opacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (OpacityLabel == null) return;
        var v = (int)Math.Round(e.NewValue);
        OpacityLabel.Text = $"{v}%";
        if (_loading) return;
        _vm.BackgroundOpacity = v; // aplica ao vivo; grava ao fechar
    }

    void Accounts_Click(object sender, RoutedEventArgs e)
    {
        var claude = ClaudeCheck.IsChecked == true;
        var codex = CodexCheck.IsChecked == true;
        if (!claude && !codex)
        {
            // Pelo menos uma conta: desfaz o clique que desmarcaria a última.
            ((CheckBox)sender).IsChecked = true;
            return;
        }
        _vm.ShowClaude = claude;
        _vm.ShowCodex = codex;
    }

    void Scale_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ScaleLabel == null) return;
        ScaleLabel.Text = $"{Math.Round(e.NewValue)}%";
        // O slider dispara ValueChanged dentro do InitializeComponent (mínimo 55), antes de _vm existir.
        if (_loading || _vm is null) return;
        _vm.FullScale = e.NewValue / 100;
    }

    void VerticalScale_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (VerticalScaleLabel == null) return;
        VerticalScaleLabel.Text = $"{Math.Round(e.NewValue)}%";
        // Também dispara dentro do InitializeComponent (mínimo 60), antes de _vm existir.
        if (_loading || _vm is null) return;
        _vm.VerticalScale = e.NewValue / 100;
    }

    void Monitor_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || MonitorCombo.SelectedItem is not MonitorInfo m) return;
        _overlay.MoveToMonitor(m.Device);
    }

    void Corner_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && Enum.TryParse<Corner>(tag, out var c))
            _overlay.MoveToCorner(c);
    }

    void Snap_Click(object sender, RoutedEventArgs e)
    {
        _settings.SnapToEdges = SnapCheck.IsChecked == true;
        _settings.Save();
    }

    void StaticNeedles_Click(object sender, RoutedEventArgs e)
    {
        _settings.StaticNeedles = StaticNeedlesCheck.IsChecked == true;
        Controls.Instrument.StaticNeedles = _settings.StaticNeedles;
        _settings.Save();
    }

    void Poll_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || PollCombo.SelectedIndex < 0) return;
        _settings.PollSeconds = PollOptions[PollCombo.SelectedIndex];
        _settings.Save();
        _pollChanged();
    }

    void WorkHours_Click(object sender, RoutedEventArgs e) => ApplyWorkHours();

    void WorkTime_Changed(object sender, SelectionChangedEventArgs e) => ApplyWorkHours();

    void ApplyWorkHours()
    {
        if (_loading) return;
        _settings.WorkHoursEnabled = WorkHoursCheck.IsChecked == true;
        if (WorkStartCombo.SelectedItem is TimeSpan start) _settings.WorkStart = start;
        if (WorkEndCombo.SelectedItem is TimeSpan end) _settings.WorkEnd = end;
        _settings.Save();
        _vm.WorkHoursChanged();
    }

    void Startup_Click(object sender, RoutedEventArgs e)
    {
        var enable = StartupCheck.IsChecked == true;
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enable && Environment.ProcessPath is { } exe)
            key.SetValue(RunValue, $"\"{exe}\"");
        else
            key.DeleteValue(RunValue, throwOnMissingValue: false);
        _settings.StartWithWindows = enable;
        _settings.Save();
    }

    /// <summary>
    /// Se o início com o Windows está ligado mas aponta para outro exe (o app foi movido ou
    /// reinstalado), atualiza o caminho para o exe que está rodando agora.
    /// </summary>
    public static void RepairStartupPath()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key?.GetValue(RunValue) is not string current || Environment.ProcessPath is not { } exe) return;
        var wanted = $"\"{exe}\"";
        if (!string.Equals(current, wanted, StringComparison.OrdinalIgnoreCase))
            key.SetValue(RunValue, wanted);
    }

    static bool IsStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(RunValue) != null;
    }

    void Publish_Click(object sender, RoutedEventArgs e)
    {
        // A saúde usa a mesma URL e a mesma chave: salvar lá também reinicia o envio dela.
        if (new PublishWindow(_settings, _publisher) { Owner = this }.ShowDialog() == true)
            _health.Reconfigure();
        ShowHealthStatus();
    }

    void ShowPublishStatus()
    {
        var o = _publisher.LastOutcome;
        PublishStatus.Text = !_settings.PublishEnabled ? "Desligado"
            : o == null ? "Ligado, aguardando o primeiro envio"
            : o.Ok ? $"Ligado · {o.Message.ToLowerInvariant()}"
            : $"Ligado · erro: {o.Message}";
    }

    static readonly Brush Warn = new SolidColorBrush(Color.FromRgb(0xEF, 0x9F, 0x27));
    static readonly Brush Danger = new SolidColorBrush(Color.FromRgb(0xE2, 0x4B, 0x4A));
    static readonly Brush Ok = new SolidColorBrush(Color.FromRgb(0x3F, 0xB9, 0x50));
    static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0x9E));

    void Health_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.HealthEnabled = HealthCheck.IsChecked == true;
        _settings.HealthPublishEnabled = HealthPublishCheck.IsChecked == true;
        _settings.Save();
        _health.Apply(); // liga ou desliga o coletor e manda um heartbeat
        _publisher.HealthChanged(); // o painel do Echo Show escolhe o layout por este campo do uso
        ShowHealth();
        ShowHealthStatus();
    }

    async void HealthTest_Click(object sender, RoutedEventArgs e)
    {
        HealthTestButton.IsEnabled = false;
        HealthPublishStatus.Text = "Testando…";
        HealthPublishStatus.Foreground = Foreground;
        try
        {
            var o = await _health.TestAsync();
            HealthPublishStatus.Text = o.Ok ? $"Teste OK: a API aceitou o envio ({o.At:HH:mm:ss})" : $"Teste falhou: {o.Message}";
            HealthPublishStatus.Foreground = o.Ok ? Foreground : Warn;
        }
        finally
        {
            HealthTestButton.IsEnabled = _settings.HealthEnabled;
        }
    }

    void ShowHealthStatus()
    {
        var o = _health.LastOutcome;
        HealthTestButton.IsEnabled = _settings.HealthEnabled;
        HealthPublishStatus.Foreground = Foreground;
        if (!_settings.HealthEnabled)
            HealthPublishStatus.Text = "Monitoramento desligado";
        else if (!_settings.HealthPublishEnabled)
            HealthPublishStatus.Text = "Envio da saúde desligado";
        else if (!_settings.PublishEnabled)
            HealthPublishStatus.Text = "Ligue o envio para API na aba Sistema para enviar a saúde";
        else if (o == null)
            HealthPublishStatus.Text = "Aguardando o primeiro envio";
        else if (o.Ok)
            HealthPublishStatus.Text = o.Message;
        else
        {
            HealthPublishStatus.Text = $"Erro: {o.Message} ({o.At:HH:mm:ss})";
            HealthPublishStatus.Foreground = Warn;
        }
    }

    void ShowHealth()
    {
        var r = _health.Monitor.Latest;
        HealthAlertsPanel.Children.Clear();
        if (r == null)
        {
            HealthEmpty.Text = _settings.HealthEnabled ? "Lendo… a primeira leitura leva cerca de 5 s."
                : "Ligue o monitoramento para ver o resumo.";
            HealthEmpty.Visibility = Visibility.Visible;
            HealthSummary.Visibility = Visibility.Collapsed;
            HealthAlertsPanel.Children.Add(new TextBlock { Text = "—", Foreground = Muted });
            return;
        }

        HealthEmpty.Visibility = Visibility.Collapsed;
        HealthSummary.Visibility = Visibility.Visible;
        HealthCpuText.Text = $"{_health.Monitor.CpuAverage(TimeSpan.FromMinutes(1)):0}% no último minuto";

        var m = r.Memory;
        HealthMemoryText.Text = $"{m.CommitPercent:0}% · {Gb(m.CommitBytes)} de {Gb(m.CommitLimitBytes)}";

        HealthDiskText.Text = r.SystemDisk is { } d
            ? $"{d.Name} {d.UsedPercent:0}% usado · {Gb(d.FreeBytes)} livres"
            : "—";

        HealthSpeedText.Text = r.SpeedMHz is { } mhz && r.BaseMHz is { } baseMhz
            ? $"{mhz / 1000:0.00} GHz · {r.SpeedPercent:0}% do clock base ({baseMhz / 1000:0.00} GHz)"
            : r.BaseMHz is { } b ? $"— · clock base {b / 1000:0.00} GHz" : "—";

        var mode = r.Power.Mode switch
        {
            "efficiency" => "Eficiência",
            "balanced" => "Equilibrado",
            "performance" => "Desempenho",
            _ => "Desconhecido",
        };
        var source = r.Power.Source switch
        {
            "ac" => " · na tomada",
            "battery" => r.Power.BatteryPercent is { } pct ? $" · na bateria ({pct}%)" : " · na bateria",
            _ => "",
        };
        HealthPowerText.Text = mode + source;

        if (r.Alerts.Count == 0)
        {
            HealthAlertsPanel.Children.Add(new TextBlock { Text = "● Nenhum alerta", Foreground = Ok });
            return;
        }
        foreach (var a in r.Alerts)
        {
            var line = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
            line.Inlines.Add(new Run("● ") { Foreground = a.Level == "danger" ? Danger : Warn });
            line.Inlines.Add(new Run(a.Title));
            line.Inlines.Add(new LineBreak());
            line.Inlines.Add(new Run(a.Action) { Foreground = Muted, FontSize = 11 });
            HealthAlertsPanel.Children.Add(line);
        }
    }

    static string Gb(long bytes) => $"{bytes / (double)(1L << 30):0.0} GB";

    void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        _health.Monitor.Sampled -= ShowHealth;
        _health.OutcomeChanged -= ShowHealthStatus;
        _publisher.OutcomeChanged -= ShowPublishStatus;
        _settings.Save();
        base.OnClosed(e);
    }
}
