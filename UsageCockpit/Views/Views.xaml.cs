using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using UsageCockpit.Core;

namespace UsageCockpit.Views;

/// <summary>Ações que os botões das views pedem para a janela executar.</summary>
public interface IOverlayCommands
{
    void RefreshNow();
    void RefreshProvider(ProviderViewModel provider);
    void ToggleMode();
    void OpenSettings();
    void MinimizeToTray();
    void ShowMonitorMenu(FrameworkElement anchor);
    void BeginResize();
    void ResizeBy(double relativeChange);
    void EndResize();
}

public partial class FullView : UserControl
{
    public FullView() => InitializeComponent();

    IOverlayCommands? Commands => Window.GetWindow(this) as IOverlayCommands;

    void Refresh_Click(object sender, RoutedEventArgs e) => Commands?.RefreshNow();
    void Monitor_Click(object sender, RoutedEventArgs e) => Commands?.ShowMonitorMenu(MonitorButton);
    void Compact_Click(object sender, RoutedEventArgs e) => Commands?.ToggleMode();
    void Settings_Click(object sender, RoutedEventArgs e) => Commands?.OpenSettings();
    void Minimize_Click(object sender, RoutedEventArgs e) => Commands?.MinimizeToTray();

    void Resize_Started(object sender, DragStartedEventArgs e) => Commands?.BeginResize();
    void Resize_Completed(object sender, DragCompletedEventArgs e) => Commands?.EndResize();
    void Resize_Delta(object sender, DragDeltaEventArgs e) => ResizeGrip.Apply(this, e, Commands);
}

public partial class VerticalView : UserControl
{
    public VerticalView() => InitializeComponent();

    IOverlayCommands? Commands => Window.GetWindow(this) as IOverlayCommands;

    void Logo_Click(object sender, RoutedEventArgs e) => LogoClick.Refresh(sender);
    void Expand_Click(object sender, RoutedEventArgs e) => Commands?.ToggleMode();
    void Minimize_Click(object sender, RoutedEventArgs e) => Commands?.MinimizeToTray();

    void Resize_Started(object sender, DragStartedEventArgs e) => Commands?.BeginResize();
    void Resize_Completed(object sender, DragCompletedEventArgs e) => Commands?.EndResize();
    void Resize_Delta(object sender, DragDeltaEventArgs e) => ResizeGrip.Apply(this, e, Commands);
}

/// <summary>Clique no logo de uma conta, nos modos compactos: atualiza só aquela conta.</summary>
static class LogoClick
{
    public static void Refresh(object sender)
    {
        if (sender is FrameworkElement { DataContext: ProviderViewModel provider } element)
            (Window.GetWindow(element) as IOverlayCommands)?.RefreshProvider(provider);
    }
}

static class ResizeGrip
{
    /// <summary>
    /// As variações chegam em unidades do painel já escalado; dividimos pelo tamanho sem escala
    /// para obter a mudança relativa. A média de X e Y mantém a proporção do painel.
    /// </summary>
    public static void Apply(UserControl view, DragDeltaEventArgs e, IOverlayCommands? commands)
    {
        var panel = (FrameworkElement)view.Content;
        if (panel.ActualWidth <= 0 || panel.ActualHeight <= 0) return;
        var rel = (e.HorizontalChange / panel.ActualWidth + e.VerticalChange / panel.ActualHeight) / 2;
        commands?.ResizeBy(rel);
    }
}

public partial class CompactView : UserControl
{
    public CompactView() => InitializeComponent();

    IOverlayCommands? Commands => Window.GetWindow(this) as IOverlayCommands;

    void Logo_Click(object sender, RoutedEventArgs e) => LogoClick.Refresh(sender);
    void Expand_Click(object sender, RoutedEventArgs e) => Commands?.ToggleMode();
    void Minimize_Click(object sender, RoutedEventArgs e) => Commands?.MinimizeToTray();
}

public partial class UltraCompactView : UserControl
{
    public UltraCompactView() => InitializeComponent();

    void Logo_Click(object sender, RoutedEventArgs e) => LogoClick.Refresh(sender);
}

public partial class UltraCompactVerticalView : UserControl
{
    public UltraCompactVerticalView() => InitializeComponent();

    void Logo_Click(object sender, RoutedEventArgs e) => LogoClick.Refresh(sender);
}
