using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace UsageCockpit.Core;

/// <summary>Resultado de um envio, exibido nas configurações.</summary>
public sealed record PublishOutcome(bool Ok, string Message, DateTimeOffset At);

/// <summary>
/// Envio para a API configurada pelo usuário (POST {urlBase}/{rota}, Authorization: Bearer), comum ao
/// uso (/v1/usage) e à saúde do PC (/v1/health). Agrupa pedidos próximos, espera mais a cada falha,
/// para de vez com 401/403 até a configuração ser salva de novo e respeita o Retry-After do 429.
/// Todas as chamadas vêm da thread da interface; o envio em si é assíncrono e não bloqueia nada.
/// </summary>
public abstract class ApiPublisher
{
    static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(10);

    protected static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    protected static readonly JsonSerializerOptions PrettyJson = new(JsonOptions) { WriteIndented = true };

    // Sem seguir redirecionamentos: a chave não pode ir parar em outro endereço.
    static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(10),
    };

    static ApiPublisher()
    {
        Client.DefaultRequestHeaders.UserAgent.ParseAdd($"UsageCockpit/{AppVersion}");
    }

    protected readonly AppSettings Settings;
    readonly string _route;
    readonly TimeSpan _minGap;

    string? _pending;
    bool _flushing;
    bool _keyRejected;
    int _failures;
    DateTimeOffset _lastAttemptAt = DateTimeOffset.MinValue;
    DateTimeOffset _retryAt = DateTimeOffset.MinValue;
    CancellationTokenSource _wait = new();

    protected ApiPublisher(AppSettings settings, string route, TimeSpan minGap)
    {
        Settings = settings;
        _route = route;
        _minGap = minGap;
    }

    public static string AppVersion
    {
        get
        {
            var v = typeof(ApiPublisher).Assembly.GetName().Version;
            return v == null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    public PublishOutcome? LastOutcome { get; private set; }
    public event Action? OutcomeChanged;

    /// <summary>Quando foi a última tentativa de envio (com ou sem sucesso).</summary>
    protected DateTimeOffset LastAttemptAt => _lastAttemptAt;

    /// <summary>Se a configuração atual pede envios.</summary>
    protected abstract bool Configured { get; }

    /// <summary>Se já há o que enviar. Sem dados, o pedido pendente é descartado.</summary>
    protected virtual bool Ready => true;

    protected abstract object BuildPayload(string reason);

    /// <summary>Qual motivo fica quando chega um pedido com outro já pendente. Padrão: o mais novo.</summary>
    protected virtual string MergeReason(string pending, string reason) => reason;

    bool Enabled => Configured && !_keyRejected;

    /// <summary>Pede um envio. Pedidos próximos são agrupados (no máximo um a cada intervalo mínimo).</summary>
    protected void Enqueue(string reason)
    {
        if (!Enabled) return;
        _pending = _pending == null ? reason : MergeReason(_pending, reason);
        if (!_flushing)
            _ = FlushAsync();
    }

    /// <summary>A configuração mudou: esquece falhas anteriores e libera um envio imediato.</summary>
    protected void ResetFailures()
    {
        _keyRejected = false;
        _failures = 0;
        _retryAt = DateTimeOffset.MinValue;
        _wait.Cancel();
        _wait = new CancellationTokenSource();
    }

    async Task FlushAsync()
    {
        _flushing = true;
        try
        {
            while (_pending != null)
            {
                var notBefore = _lastAttemptAt + _minGap > _retryAt ? _lastAttemptAt + _minGap : _retryAt;
                var wait = notBefore - DateTimeOffset.Now;
                if (wait > TimeSpan.Zero)
                {
                    try { await Task.Delay(wait, _wait.Token); }
                    catch (TaskCanceledException) { }
                }
                if (!Enabled || !Ready || _pending is not { } reason)
                {
                    _pending = null;
                    break;
                }
                _pending = null;
                _lastAttemptAt = DateTimeOffset.Now;

                if (Endpoint(Settings.PublishBaseUrl, _route, out var error) is not { } url)
                {
                    SetOutcome(new PublishOutcome(false, error!, DateTimeOffset.Now));
                    break;
                }
                var outcome = await SendAsync(url, Settings.PublishKey ?? "", BuildPayload(reason));
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

    protected void SetOutcome(PublishOutcome? outcome)
    {
        LastOutcome = outcome;
        OutcomeChanged?.Invoke();
    }

    /// <summary>Envia um payload com reason "test" para a URL e a chave dadas, sem salvar nada.</summary>
    public async Task<PublishOutcome> TestAsync(string? baseUrl, string key)
    {
        if (Endpoint(baseUrl, _route, out var error) is not { } url)
            return new PublishOutcome(false, error!, DateTimeOffset.Now);
        return (await SendAsync(url, key, BuildPayload("test"))).Result;
    }

    /// <summary>
    /// Monta {urlBase}/{rota}. Exige HTTPS; HTTP só para localhost, para quem está desenvolvendo a API.
    /// </summary>
    public static Uri? Endpoint(string? baseUrl, string route, out string? error)
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
        return new Uri($"{b.Scheme}://{b.Authority}{path}/{route}");
    }

    readonly record struct SendResult(PublishOutcome Result, bool KeyRejected = false, TimeSpan? RetryAfter = null);

    static async Task<SendResult> SendAsync(Uri url, string key, object payload)
    {
        var at = DateTimeOffset.Now;
        if (string.IsNullOrWhiteSpace(key))
            return new(new PublishOutcome(false, "Informe a chave de autenticação", at), KeyRejected: true);

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload, payload.GetType(), JsonOptions),
                    Encoding.UTF8, "application/json"),
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
