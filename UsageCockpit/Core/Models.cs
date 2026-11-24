using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UsageCockpit.Core;

/// <summary>Uma leitura de uso de um provedor: janela de sessão (5h) e janela semanal.</summary>
public sealed record UsageSnapshot(
    double SessionPercent,
    DateTimeOffset? SessionResetsAt,
    double WeekPercent,
    DateTimeOffset? WeekResetsAt,
    DateTimeOffset FetchedAt,
    string Source);

/// <summary>Tipo da falha de uma consulta. Vai para a API no campo errorCode (docs/API.md).</summary>
public static class ErrorCodes
{
    /// <summary>Sem login, token vencido ou recusado: as consultas não voltam sozinhas.</summary>
    public const string LoginRequired = "login_required";
    public const string RateLimited = "rate_limited";
    public const string Network = "network";
    public const string Other = "other";
}

/// <summary>Resultado de uma tentativa de leitura. Snapshot nulo significa falha.</summary>
public sealed record FetchResult(UsageSnapshot? Snapshot, string? Error, TimeSpan? RetryAfter = null,
    string? ErrorCode = null)
{
    public static FetchResult Ok(UsageSnapshot s) => new(s, null);

    public static FetchResult Fail(string error, string code, TimeSpan? retryAfter = null) =>
        new(null, error, retryAfter, code);

    public static FetchResult NeedsLogin(string error) => Fail(error, ErrorCodes.LoginRequired);
}

/// <summary>
/// Última leitura boa de cada provedor, gravada em disco. Ao abrir o app (ou se a API responder
/// 429 logo de cara), mostramos esses valores marcados como desatualizados em vez de "--".
/// </summary>
public static class SnapshotCache
{
    static string FilePath => Path.Combine(AppSettings.Folder, "cache.json");
    static readonly object Gate = new();

    public static UsageSnapshot? Get(string key)
    {
        lock (Gate)
            return Read().TryGetValue(key, out var s) ? s : null;
    }

    public static void Put(string key, UsageSnapshot snapshot)
    {
        lock (Gate)
        {
            var all = Read();
            all[key] = snapshot;
            try
            {
                Directory.CreateDirectory(AppSettings.Folder);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(all));
            }
            catch (IOException)
            {
            }
        }
    }

    static System.Collections.Generic.Dictionary<string, UsageSnapshot> Read()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, UsageSnapshot>>(
                    File.ReadAllText(FilePath)) ?? new();
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
        }
        return new();
    }
}

/// <summary>
/// Completo (cockpit), compacto (barras ou mini velocímetros) e ultra compacto (só os números),
/// os dois últimos na horizontal (faixa) ou na vertical (coluna).
/// </summary>
public enum DisplayMode { Full, Compact, CompactVertical, UltraCompact, UltraCompactVertical }

/// <summary>O tamanho do painel, escolhido à parte da forma (horizontal ou vertical).</summary>
public enum PanelSize { Full, Compact, Ultra }

public static class DisplayModes
{
    public static PanelSize SizeOf(DisplayMode m) => m switch
    {
        DisplayMode.Compact or DisplayMode.CompactVertical => PanelSize.Compact,
        DisplayMode.UltraCompact or DisplayMode.UltraCompactVertical => PanelSize.Ultra,
        _ => PanelSize.Full,
    };

    public static bool IsVertical(DisplayMode m) =>
        m is DisplayMode.CompactVertical or DisplayMode.UltraCompactVertical;

    /// <summary>Junta o tamanho e a forma; o modo completo não tem forma.</summary>
    public static DisplayMode Of(PanelSize size, bool vertical) => size switch
    {
        PanelSize.Compact => vertical ? DisplayMode.CompactVertical : DisplayMode.Compact,
        PanelSize.Ultra => vertical ? DisplayMode.UltraCompactVertical : DisplayMode.UltraCompact,
        _ => DisplayMode.Full,
    };
}

public enum AppTheme { Dark, Light }

public enum Corner { TopLeft, TopRight, BottomLeft, BottomRight, Center }

public sealed class AppSettings
{
    public DisplayMode Mode { get; set; } = DisplayMode.Full;

    /// <summary>Último modo compacto usado: é para ele que o botão "compactar" do modo completo volta.</summary>
    public DisplayMode LastCompactMode { get; set; } = DisplayMode.Compact;

    /// <summary>Tema do painel em todos os modos.</summary>
    public AppTheme Theme { get; set; } = AppTheme.Dark;

    /// <summary>Opacidade do fundo, de 0 a 100. O conteúdo (ponteiros, textos) fica sempre opaco.</summary>
    public int BackgroundOpacity { get; set; } = 85;

    /// <summary>Nome do dispositivo do monitor (ex.: \\.\DISPLAY2). Nulo = monitor principal.</summary>
    public string? MonitorDevice { get; set; }

    /// <summary>Posição relativa dentro da área útil do monitor (0 = esquerda/topo, 1 = direita/base).</summary>
    public double RelX { get; set; } = 1;
    public double RelY { get; set; } = 0;

    /// <summary>Escala do modo completo (1 = tamanho original). Ajustada pela alça de redimensionar.</summary>
    public double FullScale { get; set; } = 1;

    /// <summary>Escala do compacto vertical. Só diminui: 1 já é o tamanho máximo.</summary>
    public double VerticalScale { get; set; } = 1;

    public bool ShowClaude { get; set; } = true;
    public bool ShowCodex { get; set; } = true;

    public bool SnapToEdges { get; set; } = true;

    /// <summary>Ponteiros e barras sem tremor nem varredura.</summary>
    public bool StaticNeedles { get; set; }

    /// <summary>Menor intervalo de consulta permitido, para não tomar 429 dos provedores.</summary>
    public const int MinPollSeconds = 15;

    public int PollSeconds { get; set; } = 300;

    /// <summary>Até a versão 0.5 o intervalo era gravado em minutos. Só é lido, para migrar.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? PollMinutes { get; set; }

    /// <summary>Fora do horário de trabalho as consultas e os envios pausam sozinhos.</summary>
    public bool WorkHoursEnabled { get; set; }
    public TimeSpan WorkStart { get; set; } = TimeSpan.FromHours(8);
    public TimeSpan WorkEnd { get; set; } = TimeSpan.FromHours(18);

    /// <summary>Se o horário está dentro do expediente. Aceita expediente que vira a noite (22:00 às 06:00).</summary>
    public bool IsWorkTime(DateTime now)
    {
        if (!WorkHoursEnabled || WorkStart == WorkEnd) return true;
        var t = now.TimeOfDay;
        return WorkStart < WorkEnd ? t >= WorkStart && t < WorkEnd : t >= WorkStart || t < WorkEnd;
    }

    public bool StartWithWindows { get; set; }

    /// <summary>Envio do uso para uma API externa (docs/API.md). Desligado por padrão.</summary>
    public bool PublishEnabled { get; set; }
    public string? PublishBaseUrl { get; set; }

    /// <summary>Chave da API cifrada com DPAPI. Use <see cref="PublishKey"/> para ler e gravar.</summary>
    public string? PublishKeyProtected { get; set; }

    [JsonIgnore]
    public string? PublishKey
    {
        get => Secret.Unprotect(PublishKeyProtected);
        set => PublishKeyProtected = string.IsNullOrEmpty(value) ? null : Secret.Protect(value);
    }

    /// <summary>
    /// Segundo destino dos envios: o tablet na rede local (usage-ia-tablet), que recebe as mesmas rotas
    /// por HTTPS com certificado autoassinado. O certificado é aceito só se a impressão digital SHA-256
    /// for a configurada aqui.
    /// </summary>
    public bool TabletEnabled { get; set; }
    public string? TabletBaseUrl { get; set; }
    public string? TabletCertSha256 { get; set; }

    /// <summary>Chave do tablet cifrada com DPAPI. Use <see cref="TabletKey"/> para ler e gravar.</summary>
    public string? TabletKeyProtected { get; set; }

    [JsonIgnore]
    public string? TabletKey
    {
        get => Secret.Unprotect(TabletKeyProtected);
        set => TabletKeyProtected = string.IsNullOrEmpty(value) ? null : Secret.Protect(value);
    }

    /// <summary>Algum destino ligado (API ou tablet): é o que liga a coleta das tarefas e os hooks.</summary>
    [JsonIgnore]
    public bool AnyPublishEnabled => PublishEnabled || TabletEnabled;

    /// <summary>Coleta de indicadores de saúde do PC (CPU, memória, discos, energia). Desligada por padrão.</summary>
    public bool HealthEnabled { get; set; }

    /// <summary>Envia a saúde para {URL base}/v1/health, com a URL e a chave do envio para API.</summary>
    public bool HealthPublishEnabled { get; set; } = true;

    [JsonIgnore]
    public static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UsageCockpit");

    [JsonIgnore]
    static string FilePath => Path.Combine(Folder, "settings.json");

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath)
                && JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions) is { } s)
            {
                if (s.PollMinutes is int minutes)
                {
                    s.PollSeconds = minutes * 60;
                    s.PollMinutes = null;
                }
                return s;
            }
        }
        catch
        {
            // Arquivo corrompido: volta ao padrão em vez de impedir o app de abrir.
        }
        return new();
    }

    /// <summary>Usado no modo --snapshot, para não sobrescrever as configurações reais.</summary>
    [JsonIgnore]
    public bool ReadOnly { get; init; }

    public void Save()
    {
        if (ReadOnly) return;
        Directory.CreateDirectory(Folder);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(tmp, FilePath, overwrite: true);
    }
}
