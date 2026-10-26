using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace UsageCockpit.Core;

// Contrato do envio (schemaVersion 1). Documentado em docs/API.md: mudar um campo aqui exige mudar lá.

public sealed record PublishPayload(
    int SchemaVersion,
    PublishClient Client,
    DateTimeOffset SentAt,
    string Reason,
    bool Paused,
    IReadOnlyDictionary<string, PublishProvider> Providers);

public sealed record PublishClient(string Name, string Version);

public sealed record PublishProvider(
    string Label,
    bool Enabled,
    bool InUse,
    DateTimeOffset? LastActivityAt,
    string Status,
    string? Error,
    PublishUsage? Usage);

public sealed record PublishUsage(DateTimeOffset FetchedAt, string Source, PublishUsageWindow Session, PublishUsageWindow Week);

public sealed record PublishUsageWindow(double Percent, DateTimeOffset? ResetsAt, int WindowMinutes);

/// <summary>Resultado de um envio, exibido nas configurações.</summary>
public sealed record PublishOutcome(bool Ok, string Message, DateTimeOffset At);

/// <summary>
/// Envia o uso consolidado para uma API configurada pelo usuário: POST {urlBase}/v1/usage com
/// Authorization: Bearer. Nunca envia tokens dos provedores, só percentuais, horários e estado.
/// Todas as chamadas vêm da thread da interface; o envio em si é assíncrono e não bloqueia nada.
/// </summary>
public sealed class UsagePublisher
{
    public const string Route = "v1/usage";
    public const int SchemaVersion = 1;

    // Igual ao menor intervalo de consulta: no máximo um envio por consulta, mesmo com atividade.
    static readonly TimeSpan MinGap = TimeSpan.FromSeconds(AppSettings.MinPollSeconds);
    static readonly TimeSpan HeartbeatEvery = TimeSpan.FromMinutes(5);
    static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(10);

    static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    static readonly JsonSerializerOptions PrettyJson = new(JsonOptions) { WriteIndented = true };

    // Sem seguir redirecionamentos: a chave não pode ir parar em outro endereço.
    static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(10),
    };

    static UsagePublisher()
    {
        Client.DefaultRequestHeaders.UserAgent.ParseAdd($"UsageCockpit/{AppVersion}");
    }

    readonly AppSettings _settings;
    readonly MainViewModel _vm;

    string? _pending;
    bool _flushing;
    bool _keyRejected;
    int _failures;
    DateTimeOffset _lastAttemptAt = DateTimeOffset.MinValue;
    DateTimeOffset _retryAt = DateTimeOffset.MinValue;
    CancellationTokenSource _wait = new();

    public UsagePublisher(AppSettings settings, MainViewModel vm)
    {
        _settings = settings;
        _vm = vm;
    }

    public static string AppVersion
    {
        get
        {
            var v = typeof(UsagePublisher).Assembly.GetName().Version;
            return v == null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    public PublishOutcome? LastOutcome { get; private set; }
    public event Action? OutcomeChanged;

    bool Enabled => _settings.PublishEnabled && !_keyRejected;

    /// <summary>Pede um envio. Pedidos próximos são agrupados (no máximo um a cada 30 s). Pausado, não envia.</summary>
    public void Publish(string reason)
    {
        if (_vm.IsPaused) return;
        Enqueue(reason);
    }

    /// <summary>Pausou ou voltou: avisa a API uma vez (campo paused); pausado, nada mais é enviado.</summary>
    public void PauseChanged() => Enqueue("heartbeat");

    void Enqueue(string reason)
    {
        if (!Enabled) return;
        _pending = reason;
        if (!_flushing)
            _ = FlushAsync();
    }

    /// <summary>Chamado periodicamente: manda um sinal de vida se nada foi enviado nos últimos 5 min.</summary>
    public void HeartbeatIfDue()
    {
        if (DateTimeOffset.Now - _lastAttemptAt >= HeartbeatEvery)
            Publish("heartbeat");
    }

    /// <summary>A configuração mudou: esquece falhas anteriores e envia logo.</summary>
    public void Reconfigure()
    {
        _keyRejected = false;
        _failures = 0;
        _retryAt = DateTimeOffset.MinValue;
        _wait.Cancel();
        _wait = new CancellationTokenSource();
        if (_settings.PublishEnabled)
            Publish("heartbeat");
        else
            SetOutcome(null);
    }

    async Task FlushAsync()
    {
        _flushing = true;
        try
        {
            while (_pending != null)
            {
                var notBefore = _lastAttemptAt + MinGap > _retryAt ? _lastAttemptAt + MinGap : _retryAt;
                var wait = notBefore - DateTimeOffset.Now;
                if (wait > TimeSpan.Zero)
                {
                    try { await Task.Delay(wait, _wait.Token); }
                    catch (TaskCanceledException) { }
                }
                if (!Enabled || _pending is not { } reason)
                {
                    _pending = null;
                    break;
                }
                _pending = null;
                _lastAttemptAt = DateTimeOffset.Now;

                if (Endpoint(_settings.PublishBaseUrl, out var error) is not { } url)
                {
                    SetOutcome(new PublishOutcome(false, error!, DateTimeOffset.Now));
                    break;
                }
                var outcome = await SendAsync(url, _settings.PublishKey ?? "", BuildPayload(reason));
                SetOutcome(outcome.Result);
                if (outcome.Result.Ok)
                {
                    _failures = 0;
                    _retryAt = DateTimeOffset.MinValue;
                }
                else if (outcome.KeyRejected)
                {
                    // Chave errada não melhora sozinha: para até o usuário corrigir.
                    _keyRejected = true;
                }
                else
                {
                    _failures++;
                    var backoff = TimeSpan.FromSeconds(Math.Min(MaxBackoff.TotalSeconds, 30 * Math.Pow(2, _failures - 1)));
                    _retryAt = DateTimeOffset.Now + (outcome.RetryAfter is { } ra && ra > backoff ? ra : backoff);
                }
            }
        }
        finally
        {
            _flushing = false;
        }
    }

    void SetOutcome(PublishOutcome? outcome)
    {
        LastOutcome = outcome;
        OutcomeChanged?.Invoke();
    }

    /// <summary>Botão "Testar conexão": envia com os valores digitados, sem salvar nada.</summary>
    public async Task<PublishOutcome> TestAsync(string? baseUrl, string key)
    {
        if (Endpoint(baseUrl, out var error) is not { } url)
            return new PublishOutcome(false, error!, DateTimeOffset.Now);
        return (await SendAsync(url, key, BuildPayload("test"))).Result;
    }

    /// <summary>JSON exatamente como seria enviado agora; usado como exemplo na janela de configuração.</summary>
    public string PreviewJson() => JsonSerializer.Serialize(BuildPayload("test"), PrettyJson);

    /// <summary>
    /// Monta {urlBase}/v1/usage. Exige HTTPS; HTTP só para localhost, para quem está desenvolvendo a API.
    /// </summary>
    public static Uri? Endpoint(string? baseUrl, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            error = "Informe a URL base da API";
            return null;
        }
        if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var b)
            || (b.Scheme != Uri.UriSchemeHttps && b.Scheme != Uri.UriSchemeHttp))
        {
            error = "URL inválida: use https://seu-dominio";
            return null;
        }
        if (b.Scheme == Uri.UriSchemeHttp && !b.IsLoopback)
        {
            error = "Use HTTPS (HTTP só é aceito para localhost)";
            return null;
        }
        if (!string.IsNullOrEmpty(b.Query) || !string.IsNullOrEmpty(b.Fragment) || !string.IsNullOrEmpty(b.UserInfo))
        {
            error = "A URL base não pode ter ?, # nem usuário/senha";
            return null;
        }
        var path = b.AbsolutePath.TrimEnd('/');
        return new Uri($"{b.Scheme}://{b.Authority}{path}/{Route}");
    }

    PublishPayload BuildPayload(string reason) => new(
        SchemaVersion,
        new PublishClient("UsageCockpit", AppVersion),
        DateTimeOffset.Now,
        reason,
        _vm.IsPaused,
        new Dictionary<string, PublishProvider>
        {
            ["claude"] = Describe(_vm.Claude, _settings.ShowClaude),
            ["codex"] = Describe(_vm.Codex, _settings.ShowCodex),
        });

    static PublishProvider Describe(ProviderViewModel p, bool enabled)
    {
        // Conta desligada não é consultada nem observada: vai sem leitura e sem atividade.
        if (!enabled)
            return new PublishProvider(p.Label, false, false, null, "no_data", null, null);

        var s = p.Last;
        var status = s == null ? "no_data" : p.HasError ? "error" : p.IsStale ? "stale" : "ok";
        var usage = s == null ? null : new PublishUsage(
            s.FetchedAt.ToLocalTime(),
            s.Source,
            new PublishUsageWindow(s.SessionPercent, s.SessionResetsAt?.ToLocalTime(), 5 * 60),
            new PublishUsageWindow(s.WeekPercent, s.WeekResetsAt?.ToLocalTime(), 7 * 24 * 60));
        return new PublishProvider(p.Label, true, p.IsActive, p.LastActivityAt, status, p.Error, usage);
    }

    readonly record struct SendResult(PublishOutcome Result, bool KeyRejected = false, TimeSpan? RetryAfter = null);

    static async Task<SendResult> SendAsync(Uri url, string key, PublishPayload payload)
    {
        var at = DateTimeOffset.Now;
        if (string.IsNullOrWhiteSpace(key))
            return new(new PublishOutcome(false, "Informe a chave de autenticação", at), KeyRejected: true);

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json"),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Trim());

            using var res = await Client.SendAsync(req);
            var code = (int)res.StatusCode;
            if (res.IsSuccessStatusCode)
                return new(new PublishOutcome(true, $"Enviado às {at:HH:mm:ss}", at));
            if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return new(new PublishOutcome(false, $"Chave recusada pela API ({code})", at), KeyRejected: true);
            if (res.StatusCode == HttpStatusCode.TooManyRequests)
                return new(new PublishOutcome(false, "A API pediu para esperar (429)", at), RetryAfter: Http.RetryAfter(res));
            if (code is >= 300 and < 400)
                return new(new PublishOutcome(false, $"A API redirecionou ({code}): use a URL final", at));
            return new(new PublishOutcome(false, $"A API respondeu {code}", at));
        }
        catch (FormatException)
        {
            return new(new PublishOutcome(false, "A chave tem caracteres inválidos", at), KeyRejected: true);
        }
        catch (TaskCanceledException)
        {
            return new(new PublishOutcome(false, "Tempo esgotado ao enviar", at));
        }
        catch (HttpRequestException)
        {
            return new(new PublishOutcome(false, "Sem conexão com a API", at));
        }
    }
}

/// <summary>Cifra textos com a conta do Windows (DPAPI): só o mesmo usuário, nesta máquina, consegue ler.</summary>
public static class Secret
{
    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("UsageCockpit.Publish");

    public static string Protect(string plain) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser));

    public static string? Unprotect(string? cipher)
    {
        if (string.IsNullOrEmpty(cipher)) return null;
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(cipher), Entropy,
                DataProtectionScope.CurrentUser));
        }
        catch (Exception e) when (e is CryptographicException or FormatException)
        {
            return null; // copiado de outro usuário/máquina: o usuário digita a chave de novo
        }
    }
}
