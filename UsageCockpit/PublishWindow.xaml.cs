using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using UsageCockpit.Core;

namespace UsageCockpit;

public partial class PublishWindow : Window
{
    public const string DocsUrl = "https://github.com/willianas1/usage-ia-cockpit/blob/main/docs/API.md";

    readonly AppSettings _settings;
    readonly UsagePublisher _publisher;

    public PublishWindow(AppSettings settings, UsagePublisher publisher)
    {
        InitializeComponent();
        _settings = settings;
        _publisher = publisher;

        EnabledCheck.IsChecked = settings.PublishEnabled;
        UrlBox.Text = settings.PublishBaseUrl ?? "";
        KeyBox.Password = settings.PublishKey ?? "";
        TabletCheck.IsChecked = settings.TabletEnabled;
        TabletUrlBox.Text = settings.TabletBaseUrl ?? "";
        TabletKeyBox.Password = settings.TabletKey ?? "";
        TabletPinBox.Text = settings.TabletCertSha256 ?? "";
        ExampleBox.Text = publisher.PreviewJson();
        ShowOutcome();
        publisher.OutcomeChanged += ShowOutcome;
    }

    void Url_Changed(object sender, TextChangedEventArgs e)
    {
        if (EndpointText == null) return;
        EndpointText.Text = UrlBox.Text.Trim().Length == 0 ? "POST {URL base}/v1/usage"
            : UsagePublisher.Endpoint(UrlBox.Text, out var error) is { } url ? $"POST {url}" : error;
    }

    void ShowOutcome()
    {
        var o = _publisher.LastOutcome;
        StatusText.Text = !_settings.PublishEnabled ? "Envio desligado"
            : o == null ? "Aguardando o primeiro envio"
            : o.Ok ? o.Message
            : $"{o.Message} ({o.At:HH:mm:ss})";
        StatusText.Foreground = o is { Ok: false } && _settings.PublishEnabled ? Warn : Foreground;

        var t = _publisher.TabletOutcome;
        TabletStatusText.Text = !_settings.TabletEnabled ? "Envio para o tablet desligado"
            : t == null ? "Aguardando o primeiro envio"
            : t.Ok ? t.Message
            : $"{t.Message} ({t.At:HH:mm:ss})";
        TabletStatusText.Foreground = t is { Ok: false } && _settings.TabletEnabled ? Warn : Foreground;
    }

    async void TabletTest_Click(object sender, RoutedEventArgs e)
    {
        TabletTestButton.IsEnabled = false;
        TabletStatusText.Text = "Testando…";
        TabletStatusText.Foreground = Foreground;
        try
        {
            var o = await _publisher.TestAsync(PublishTarget.Tablet, TabletUrlBox.Text, TabletKeyBox.Password, TabletPinBox.Text);
            TabletStatusText.Text = o.Ok ? $"Teste OK: o tablet aceitou o envio ({o.At:HH:mm:ss})" : $"Teste falhou: {o.Message}";
            TabletStatusText.Foreground = o.Ok ? Foreground : Warn;
        }
        finally
        {
            TabletTestButton.IsEnabled = true;
        }
    }

    static readonly Brush Warn = new SolidColorBrush(Color.FromRgb(0xF0, 0x88, 0x3E));

    async void Test_Click(object sender, RoutedEventArgs e)
    {
        TestButton.IsEnabled = false;
        StatusText.Text = "Testando…";
        try
        {
            var o = await _publisher.TestAsync(UrlBox.Text, KeyBox.Password);
            StatusText.Text = o.Ok ? $"Teste OK: a API aceitou o envio ({o.At:HH:mm:ss})" : $"Teste falhou: {o.Message}";
            StatusText.Foreground = o.Ok ? Foreground : Warn;
        }
        finally
        {
            TestButton.IsEnabled = true;
        }
    }

    void Save_Click(object sender, RoutedEventArgs e)
    {
        var enabled = EnabledCheck.IsChecked == true;
        var url = UrlBox.Text.Trim();
        var key = KeyBox.Password.Trim();
        var tablet = TabletCheck.IsChecked == true;
        var tabletUrl = TabletUrlBox.Text.Trim();
        var tabletKey = TabletKeyBox.Password.Trim();
        var tabletPin = ApiPublisher.NormalizePin(TabletPinBox.Text);
        string? problem = null;
        if (enabled)
        {
            if (UsagePublisher.Endpoint(url, out var error) == null) problem = error;
            else if (key.Length == 0) problem = "Informe a chave de autenticação";
        }
        if (problem == null && tablet)
        {
            if (ApiPublisher.Endpoint(PublishTarget.Tablet, tabletUrl, UsagePublisher.Route, out var error) == null) problem = error;
            else if (tabletKey.Length == 0) problem = "Informe a chave do tablet";
            else if (tabletPin.Length != 64) problem = "Informe a impressão digital do certificado do tablet (64 dígitos hexadecimais)";
        }
        if (problem != null)
        {
            MessageBox.Show(this, problem, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _settings.PublishEnabled = enabled;
        _settings.PublishBaseUrl = url.Length == 0 ? null : url;
        _settings.PublishKey = key;
        _settings.TabletEnabled = tablet;
        _settings.TabletBaseUrl = tabletUrl.Length == 0 ? null : tabletUrl;
        _settings.TabletKey = tabletKey;
        _settings.TabletCertSha256 = tabletPin.Length == 0 ? null : tabletPin;
        _settings.Save();
        _publisher.Reconfigure();
        DialogResult = true; // fecha e avisa quem abriu que a configuração mudou
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    void CopyExample_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(ExampleBox.Text); }
        catch (System.Runtime.InteropServices.COMException) { } // área de transferência ocupada por outro app
    }

    void Docs_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(DocsUrl) { UseShellExecute = true });

    protected override void OnClosed(EventArgs e)
    {
        _publisher.OutcomeChanged -= ShowOutcome;
        base.OnClosed(e);
    }
}
