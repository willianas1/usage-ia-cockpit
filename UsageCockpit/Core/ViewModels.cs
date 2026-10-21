using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using UsageCockpit.Controls;

namespace UsageCockpit.Core;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        Raise(name);
        return true;
    }
}

/// <summary>Estado exibido de um provedor. Mantém a última leitura boa quando uma consulta falha.</summary>
public sealed class ProviderViewModel : Observable
{
    static readonly CultureInfo PtBr = new("pt-BR");

    readonly IUsageProvider _provider;
    UsageSnapshot? _last;
    DateTimeOffset _nextAllowed = DateTimeOffset.MinValue;
    string? _error;

    readonly ActivityMonitor _activity;
    bool _isActive;
    int _sweepToken;

    public ProviderViewModel(IUsageProvider provider, ActivityMonitor activity, string label, Geometry logo,
        Brush brand, Brush logoBrush)
    {
        _provider = provider;
        _activity = activity;
        Label = label;
        Logo = logo;
        Brand = brand;
        LogoBrush = logoBrush;
        _last = SnapshotCache.Get(provider.Name);
    }

    public string Label { get; }
    public Geometry Logo { get; }
    public Brush Brand { get; }
    public Brush LogoBrush { get; }

    /// <summary>A ferramenta está trabalhando agora (os ponteiros tremem).</summary>
    public bool IsActive { get => _isActive; private set => Set(ref _isActive, value); }

    /// <summary>Muda a cada atualização manual; os mostradores fazem a varredura até zero e voltam.</summary>
    public int SweepToken { get => _sweepToken; private set => Set(ref _sweepToken, value); }

    public void Sweep() => SweepToken++;

    /// <summary>Atualiza IsActive. Retorna true quando a ferramenta acabou de parar de trabalhar.</summary>
    public bool CheckActivity()
    {
        var was = IsActive;
        IsActive = _activity.IsActive;
        return was && !IsActive;
    }

    /// <summary>Só para as capturas do modo --snapshot: mostra o estado "em uso".</summary>
    public void ShowAsActiveForPreview() => IsActive = true;

    public DateTimeOffset? LastFetchedAt => _last?.FetchedAt;

    public string ShortStatus => _last == null ? $"{Label}: --" : $"{Label}: 5h {SessionPercentText} · sem {WeekPercentText}";

    public bool HasData => _last != null;
    public double SessionPercent => _last?.SessionPercent ?? 0;
    public double WeekPercent => _last?.WeekPercent ?? 0;
    public string SessionPercentText => _last == null ? "--" : $"{Math.Round(SessionPercent)}%";
    public string WeekPercentText => _last == null ? "--" : $"{Math.Round(WeekPercent)}%";
    public Brush SessionLevelBrush => Palette.Level(SessionPercent, HasData);
    public Brush WeekLevelBrush => Palette.Level(WeekPercent, HasData);
    public string SessionResetText => "reseta em " + Until(_last?.SessionResetsAt);
    public string WeekResetText => "reseta " + When(_last?.WeekResetsAt);
    public string SessionResetShort => Until(_last?.SessionResetsAt);

    /// <summary>Mensagem curta exibida abaixo do provedor quando a última consulta falhou.</summary>
    public string? Error { get => _error; private set { if (Set(ref _error, value)) Raise(nameof(HasError)); } }
    public bool HasError => !string.IsNullOrEmpty(_error);

    /// <summary>Leitura antiga (mais de 2 ciclos sem atualizar, ou vinda dos logs locais).</summary>
    public bool IsStale => _last != null && (DateTimeOffset.Now - _last.FetchedAt > TimeSpan.FromMinutes(15));

    public string Tooltip => _last == null
        ? $"{Label}: {Error ?? "carregando…"}"
        : $"{Label}\nSessão 5h: {SessionPercentText} · {SessionResetText}\nSemana: {WeekPercentText} · {WeekResetText}\n" +
          $"Atualizado {Ago(_last.FetchedAt)}{(_last.Source == "logs" ? " (logs locais)" : "")}" +
          (HasError ? $"\n{Error}" : "");

    public async Task RefreshAsync(bool force, CancellationToken ct)
    {
        if (!force && DateTimeOffset.Now < _nextAllowed)
        {
            RaiseAll();
            return;
        }

        var result = await _provider.FetchAsync(ct);
        if (result.Snapshot != null)
        {
            _last = result.Snapshot;
            Error = null;
            SnapshotCache.Put(_provider.Name, result.Snapshot);
        }
        else
        {
            Error = result.Error;
        }
        if (result.RetryAfter is { } wait)
            _nextAllowed = DateTimeOffset.Now + wait;
        RaiseAll();
    }

    /// <summary>Reavalia textos relativos ("reseta em 38min") sem consultar a rede.</summary>
    public void Tick() => RaiseAll();

    void RaiseAll()
    {
        foreach (var p in new[]
                 {
                     nameof(HasData), nameof(SessionPercent), nameof(WeekPercent), nameof(SessionPercentText),
                     nameof(WeekPercentText), nameof(SessionLevelBrush), nameof(WeekLevelBrush),
                     nameof(SessionResetText), nameof(WeekResetText), nameof(IsStale), nameof(Tooltip),
                     nameof(ShortStatus), nameof(SessionResetShort),
                 })
            Raise(p);
    }

    static string Until(DateTimeOffset? at)
    {
        if (at == null)
            return "--";
        var d = at.Value - DateTimeOffset.Now;
        if (d <= TimeSpan.Zero)
            return "instantes";
        if (d.TotalHours >= 24)
            return $"{(int)d.TotalDays}d {d.Hours}h";
        return d.TotalHours >= 1 ? $"{(int)d.TotalHours}h {d.Minutes:00}min" : $"{Math.Max(1, d.Minutes)}min";
    }

    static string When(DateTimeOffset? at) =>
        at == null ? "--" : at.Value.ToLocalTime().ToString("ddd HH:mm", PtBr).Replace(".", "");

    public static string Ago(DateTimeOffset at)
    {
        var d = DateTimeOffset.Now - at;
        if (d.TotalMinutes < 1)
            return "agora";
        if (d.TotalHours < 1)
            return $"há {(int)d.TotalMinutes} min";
        return d.TotalDays < 1 ? $"há {(int)d.TotalHours} h" : $"há {(int)d.TotalDays} d";
    }
}

public sealed class MainViewModel : Observable
{
    readonly AppSettings _settings;
    DateTimeOffset? _lastRefresh;
    bool _refreshing;

    public MainViewModel(AppSettings settings)
    {
        _settings = settings;
        Claude = new ProviderViewModel(new ClaudeProvider(), ActivityMonitor.ForClaude(), "claude code",
            Logos.Claude, Palette.Clay, Palette.Clay);
        Codex = new ProviderViewModel(new CodexProvider(), ActivityMonitor.ForCodex(), "codex",
            Logos.OpenAI, Palette.OpenAIGreen, Palette.Text);
    }

    /// <summary>
    /// Chamado a cada segundo. Quando uma ferramenta para de trabalhar, o uso acabou de mudar:
    /// consultamos de novo, mas no máximo uma vez por minuto para não tomar 429.
    /// </summary>
    public void CheckActivity()
    {
        var stopped = Claude.CheckActivity() && _settings.ShowClaude;
        stopped |= Codex.CheckActivity() && _settings.ShowCodex;
        if (stopped && (_lastRefresh == null || DateTimeOffset.Now - _lastRefresh > TimeSpan.FromMinutes(1)))
            _ = RefreshAsync(force: false);
    }

    /// <summary>Atualização pedida pelo usuário: ponteiros descem a zero e voltam com os valores novos.</summary>
    public Task RefreshWithSweepAsync()
    {
        Claude.Sweep();
        Codex.Sweep();
        return RefreshAsync(force: true);
    }

    public ProviderViewModel Claude { get; }
    public ProviderViewModel Codex { get; }

    public DisplayMode Mode
    {
        get => _settings.Mode;
        set
        {
            if (_settings.Mode == value) return;
            _settings.Mode = value;
            if (value != DisplayMode.Full)
                _settings.LastCompactMode = value;
            _settings.Save();
            Raise();
        }
    }

    /// <summary>Alterna entre o modo completo e o último modo compacto usado.</summary>
    public void ToggleMode() =>
        Mode = Mode == DisplayMode.Full
            ? (_settings.LastCompactMode == DisplayMode.Full ? DisplayMode.Compact : _settings.LastCompactMode)
            : DisplayMode.Full;

    public int BackgroundOpacity
    {
        get => _settings.BackgroundOpacity;
        set
        {
            value = Math.Clamp(value, 0, 100);
            if (_settings.BackgroundOpacity == value) return;
            _settings.BackgroundOpacity = value;
            RaiseBackground();
        }
    }

    // Alfa mínimo de 1/255: em janelas com transparência, pixels 100% transparentes não recebem
    // cliques, e o usuário perderia a capacidade de arrastar o app pelo fundo.
    byte Alpha(double max) => (byte)Math.Max(1, Math.Round(255 * max * _settings.BackgroundOpacity / 100.0));

    public Brush PanelBackground => Frozen(Color.FromArgb(Alpha(1), 0x0F, 0x12, 0x16));
    public Brush PanelBorder => Frozen(Color.FromArgb(Alpha(1), 0x2D, 0x33, 0x3B));

    /// <summary>Com fundo bem transparente, uma sombra leve nos textos mantém a leitura sobre qualquer janela.</summary>
    public bool UseTextShadow => _settings.BackgroundOpacity < 60;

    public const double MinScale = 0.55, MaxScale = 1.6;

    public double FullScale
    {
        get => _settings.FullScale;
        set
        {
            value = Math.Round(Math.Clamp(value, MinScale, MaxScale), 3);
            if (Math.Abs(_settings.FullScale - value) < 0.001) return;
            _settings.FullScale = value;
            Raise();
        }
    }

    public const double MinVerticalScale = 0.6, MaxVerticalScale = 1;

    public double VerticalScale
    {
        get => _settings.VerticalScale;
        set
        {
            value = Math.Round(Math.Clamp(value, MinVerticalScale, MaxVerticalScale), 3);
            if (Math.Abs(_settings.VerticalScale - value) < 0.001) return;
            _settings.VerticalScale = value;
            Raise();
        }
    }

    /// <summary>Redimensiona o modo atual (completo ou compacto vertical); o horizontal não muda de tamanho.</summary>
    public void ScaleCurrentMode(Func<double, double> change)
    {
        if (Mode == DisplayMode.Full) FullScale = change(FullScale);
        else if (Mode == DisplayMode.CompactVertical) VerticalScale = change(VerticalScale);
    }

    public bool ShowClaude
    {
        get => _settings.ShowClaude;
        set => SetAccounts(value, _settings.ShowCodex);
    }

    public bool ShowCodex
    {
        get => _settings.ShowCodex;
        set => SetAccounts(_settings.ShowClaude, value);
    }

    public bool ShowDivider => _settings.ShowClaude && _settings.ShowCodex;

    void SetAccounts(bool claude, bool codex)
    {
        // Pelo menos uma conta precisa continuar visível.
        if (!claude && !codex) { Raise(nameof(ShowClaude)); Raise(nameof(ShowCodex)); return; }
        var turnedOn = (claude && !_settings.ShowClaude) || (codex && !_settings.ShowCodex);
        _settings.ShowClaude = claude;
        _settings.ShowCodex = codex;
        _settings.Save();
        Raise(nameof(ShowClaude));
        Raise(nameof(ShowCodex));
        Raise(nameof(ShowDivider));
        if (turnedOn)
            _ = RefreshAsync(force: false);
    }

    public string UpdatedText => _refreshing ? "atualizando…" : _lastRefresh == null ? "" : ProviderViewModel.Ago(_lastRefresh.Value);

    public void RaiseBackground()
    {
        Raise(nameof(BackgroundOpacity));
        Raise(nameof(PanelBackground));
        Raise(nameof(PanelBorder));
        Raise(nameof(UseTextShadow));
    }

    public async Task RefreshAsync(bool force, CancellationToken ct = default)
    {
        if (_refreshing) return;
        _refreshing = true;
        Raise(nameof(UpdatedText));
        try
        {
            // Contas desligadas não são consultadas.
            await Task.WhenAll(
                _settings.ShowClaude ? Claude.RefreshAsync(force, ct) : Task.CompletedTask,
                _settings.ShowCodex ? Codex.RefreshAsync(force, ct) : Task.CompletedTask);
            _lastRefresh = DateTimeOffset.Now;
        }
        finally
        {
            _refreshing = false;
            Raise(nameof(UpdatedText));
        }
    }

    public void Tick()
    {
        Claude.Tick();
        Codex.Tick();
        Raise(nameof(UpdatedText));
    }

    static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
