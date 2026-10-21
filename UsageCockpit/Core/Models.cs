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

/// <summary>Resultado de uma tentativa de leitura. Snapshot nulo significa falha.</summary>
public sealed record FetchResult(UsageSnapshot? Snapshot, string? Error, TimeSpan? RetryAfter = null)
{
    public static FetchResult Ok(UsageSnapshot s) => new(s, null);
    public static FetchResult Fail(string error, TimeSpan? retryAfter = null) => new(null, error, retryAfter);
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

/// <summary>Completo (cockpit), compacto horizontal (faixa) e compacto vertical (coluna).</summary>
public enum DisplayMode { Full, Compact, CompactVertical }

public enum Corner { TopLeft, TopRight, BottomLeft, BottomRight, Center }

public sealed class AppSettings
{
    public DisplayMode Mode { get; set; } = DisplayMode.Full;

    /// <summary>Último modo compacto usado: é para ele que o botão "compactar" do modo completo volta.</summary>
    public DisplayMode LastCompactMode { get; set; } = DisplayMode.Compact;

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
    public int PollMinutes { get; set; } = 5;
    public bool StartWithWindows { get; set; }

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
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions) ?? new();
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
