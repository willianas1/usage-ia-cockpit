using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using UsageCockpit.Core;

namespace UsageCockpit;

public partial class SettingsWindow : Window
{
    static readonly int[] PollOptions = { 2, 5, 10, 15, 30 };
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValue = "UsageCockpit";

    readonly AppSettings _settings;
    readonly MainViewModel _vm;
    readonly OverlayWindow _overlay;
    readonly Action _pollChanged;
    readonly bool _loading;

    public SettingsWindow(AppSettings settings, MainViewModel vm, OverlayWindow overlay, Action pollChanged)
    {
        InitializeComponent();
        _settings = settings;
        _vm = vm;
        _overlay = overlay;
        _pollChanged = pollChanged;

        _loading = true;
        FullMode.IsChecked = settings.Mode == DisplayMode.Full;
        CompactMode.IsChecked = settings.Mode == DisplayMode.Compact;
        VerticalMode.IsChecked = settings.Mode == DisplayMode.CompactVertical;
        OpacitySlider.Value = settings.BackgroundOpacity;
        OpacityLabel.Text = $"{settings.BackgroundOpacity}%";
        SnapCheck.IsChecked = settings.SnapToEdges;
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

        PollCombo.ItemsSource = PollOptions.Select(m => $"{m} min").ToList();
        PollCombo.SelectedIndex = Math.Max(0, Array.IndexOf(PollOptions, settings.PollMinutes));
        _loading = false;
    }

    void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _vm.Mode = FullMode.IsChecked == true ? DisplayMode.Full
            : VerticalMode.IsChecked == true ? DisplayMode.CompactVertical
            : DisplayMode.Compact;
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

    void Poll_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || PollCombo.SelectedIndex < 0) return;
        _settings.PollMinutes = PollOptions[PollCombo.SelectedIndex];
        _settings.Save();
        _pollChanged();
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

    void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        _settings.Save();
        base.OnClosed(e);
    }
}
