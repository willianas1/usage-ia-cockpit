using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace UsageCockpit.Core;

public interface IUsageProvider
{
    string Name { get; }
    Task<FetchResult> FetchAsync(CancellationToken ct);

    /// <summary>O que fazer quando o login expira, em uma frase (vai no balão e no painel).</summary>
    string LoginHint { get; }

    /// <summary>Quando o arquivo de login foi gravado pela última vez. Nulo se ele não existe.</summary>
    DateTime? CredentialsWrittenAt { get; }
}

/// <summary>Provedor cujo login o app consegue renovar sozinho, pela ferramenta oficial.</summary>
public interface ILoginRenewer
{
    /// <summary>Renova o login. Retorna nulo quando deu certo, ou a mensagem do que falhou.</summary>
    Task<string?> RenewLoginAsync(CancellationToken ct);
}

static class Http
{
    public static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(15) };

    static Http()
    {
        Client.DefaultRequestHeaders.UserAgent.ParseAdd("UsageCockpit/0.1");
    }

    public static TimeSpan RetryAfter(HttpResponseMessage r)
    {
        var ra = r.Headers.RetryAfter;
        var wait = ra?.Delta ?? (ra?.Date - DateTimeOffset.UtcNow) ?? TimeSpan.Zero;
        // O endpoint do Claude às vezes manda Retry-After: 0. Nunca tentamos de novo antes de 60 s.
        return wait < TimeSpan.FromSeconds(60) ? TimeSpan.FromSeconds(60) : wait;
    }

    public static string Home(params string[] parts) =>
        Path.Combine(new[] { Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) }.Concat(parts).ToArray());
}

/// <summary>
/// Lê o token que o Claude Code salva em ~/.claude/.credentials.json e consulta o mesmo
/// endpoint usado pelo comando /usage. O token nunca é renovado nem gravado por este app,
/// para não invalidar a sessão do próprio Claude Code. Para renovar, o app roda o próprio
/// Claude Code (claude auth status), que troca o token e grava o arquivo ele mesmo.
/// </summary>
public sealed class ClaudeProvider : IUsageProvider, ILoginRenewer
{
    public string Name => "Claude Code";

    public string LoginHint => "Clique em Renovar login, ou rode claude num terminal.";

    static string CredentialsPath => Http.Home(".claude", ".credentials.json");

    public DateTime? CredentialsWrittenAt => File.Exists(CredentialsPath) ? File.GetLastWriteTimeUtc(CredentialsPath) : null;

    static readonly TimeSpan RenewTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Roda "claude auth status" sem janela. Só de consultar o login, o Claude Code renova o token
    /// vencido com o refresh token e grava o .credentials.json, sem abrir sessão nem gastar uso.
    /// </summary>
    public async Task<string?> RenewLoginAsync(CancellationToken ct)
    {
        if (FindClaude() is not { } exe)
            return "Não achei o Claude Code: rode claude num terminal";

        // O .cmd do npm só roda pelo cmd.exe; o claude.exe do instalador nativo roda direto.
        var viaCmd = exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase);
        var psi = new ProcessStartInfo(viaCmd ? "cmd.exe" : exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Http.Home(),
        };
        if (viaCmd) { psi.ArgumentList.Add("/c"); psi.ArgumentList.Add(exe); }
        psi.ArgumentList.Add("auth");
        psi.ArgumentList.Add("status");

        try
        {
            using var proc = Process.Start(psi);
            if (proc == null)
                return "Não consegui abrir o Claude Code: rode claude num terminal";
            // Lê a saída para o processo não travar com o buffer cheio; o conteúdo não interessa.
            var drain = Task.WhenAll(proc.StandardOutput.ReadToEndAsync(ct), proc.StandardError.ReadToEndAsync(ct));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(RenewTimeout);
            try
            {
                await proc.WaitForExitAsync(timeout.Token);
                await drain;
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return "O Claude Code demorou para responder: rode claude num terminal";
            }
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException)
        {
            return "Não consegui abrir o Claude Code: rode claude num terminal";
        }

        return TokenValid() ? null : "O login não foi renovado: rode claude num terminal e faça /login";
    }

    static bool TokenValid()
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(CredentialsPath));
            return doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth)
                && oauth.TryGetProperty("expiresAt", out var exp)
                && DateTimeOffset.FromUnixTimeMilliseconds(exp.GetInt64()) > DateTimeOffset.UtcNow;
        }
        catch (Exception e) when (e is IOException or JsonException or InvalidOperationException
                                      or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>O claude do PATH; senão, os lugares padrão do instalador nativo e do npm.</summary>
    static string? FindClaude()
    {
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Append(Http.Home(".local", "bin"))
            .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"));
        foreach (var dir in dirs)
            foreach (var name in new[] { "claude.exe", "claude.cmd" })
            {
                try
                {
                    var path = Path.Combine(dir, name);
                    if (File.Exists(path)) return path;
                }
                catch (ArgumentException)
                {
                    // Pasta com caractere inválido no PATH: ignora.
                }
            }
        return null;
    }

    public async Task<FetchResult> FetchAsync(CancellationToken ct)
    {
        string token;
        try
        {
            if (!File.Exists(CredentialsPath))
                return FetchResult.NeedsLogin("Faça login no Claude Code (claude)");

            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(CredentialsPath, ct));
            if (!doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth)
                || !oauth.TryGetProperty("accessToken", out var at))
                return FetchResult.NeedsLogin("Login do Claude Code não encontrado");

            token = at.GetString() ?? "";
            if (oauth.TryGetProperty("expiresAt", out var exp)
                && DateTimeOffset.FromUnixTimeMilliseconds(exp.GetInt64()) <= DateTimeOffset.UtcNow)
                return FetchResult.NeedsLogin("Login do Claude Code expirado");
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            return FetchResult.Fail("Não consegui ler o login do Claude Code", ErrorCodes.Other);
        }

        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/api/oauth/usage");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Add("anthropic-beta", "oauth-2025-04-20");

        try
        {
            using var res = await Http.Client.SendAsync(req, ct);
            if (res.StatusCode == HttpStatusCode.TooManyRequests)
                return FetchResult.Fail("Muitas consultas, aguardando", ErrorCodes.RateLimited, Http.RetryAfter(res));
            if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return FetchResult.NeedsLogin("Login do Claude Code recusado");
            if (!res.IsSuccessStatusCode)
                return FetchResult.Fail($"Erro {(int)res.StatusCode} ao consultar o uso", ErrorCodes.Other);

            using var body = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            var root = body.RootElement;
            var (s, sr) = Window(root, "five_hour");
            var (w, wr) = Window(root, "seven_day");
            return FetchResult.Ok(new UsageSnapshot(s, sr, w, wr, DateTimeOffset.Now, "api"));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            return FetchResult.Fail("Sem conexão com a Anthropic", ErrorCodes.Network);
        }
    }

    static (double pct, DateTimeOffset? resets) Window(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var w) || w.ValueKind != JsonValueKind.Object)
            return (0, null);
        var pct = w.TryGetProperty("utilization", out var u) && u.ValueKind == JsonValueKind.Number ? u.GetDouble() : 0;
        DateTimeOffset? at = w.TryGetProperty("resets_at", out var r) && r.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(r.GetString(), out var d) ? d : null;
        return (pct, at);
    }
}

/// <summary>
/// Lê ~/.codex/auth.json (login com a conta do ChatGPT) e consulta o endpoint de limites.
/// Se a rede falhar, usa o último "rate_limits" gravado nos logs de sessão do Codex.
/// </summary>
public sealed class CodexProvider : IUsageProvider
{
    public string Name => "Codex";

    public string LoginHint => "Rode codex login num terminal.";

    static string AuthPath => Http.Home(".codex", "auth.json");

    public DateTime? CredentialsWrittenAt => File.Exists(AuthPath) ? File.GetLastWriteTimeUtc(AuthPath) : null;
    static string SessionsPath => Http.Home(".codex", "sessions");

    public async Task<FetchResult> FetchAsync(CancellationToken ct)
    {
        var api = await FromApiAsync(ct);
        if (api.Snapshot != null || api.RetryAfter != null)
            return api;

        var local = FromSessionLogs();
        return local != null ? FetchResult.Ok(local) : api;
    }

    async Task<FetchResult> FromApiAsync(CancellationToken ct)
    {
        string token, account;
        try
        {
            if (!File.Exists(AuthPath))
                return FetchResult.NeedsLogin("Faça login no Codex (ChatGPT): rode codex login");
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(AuthPath, ct));
            if (!doc.RootElement.TryGetProperty("tokens", out var t) || t.ValueKind != JsonValueKind.Object)
                return FetchResult.NeedsLogin("Codex (ChatGPT) sem login do ChatGPT (só API key)");
            token = t.GetProperty("access_token").GetString() ?? "";
            account = t.TryGetProperty("account_id", out var a) ? a.GetString() ?? "" : "";
        }
        catch (Exception e) when (e is IOException or JsonException or InvalidOperationException
                                  or System.Collections.Generic.KeyNotFoundException)
        {
            return FetchResult.Fail("Não consegui ler o login do Codex (ChatGPT)", ErrorCodes.Other);
        }

        using var req = new HttpRequestMessage(HttpMethod.Get, "https://chatgpt.com/backend-api/wham/usage");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (account.Length > 0)
            req.Headers.Add("ChatGPT-Account-Id", account);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };

        try
        {
            using var res = await Http.Client.SendAsync(req, ct);
            if (res.StatusCode == HttpStatusCode.TooManyRequests)
                return FetchResult.Fail("Muitas consultas, aguardando", ErrorCodes.RateLimited, Http.RetryAfter(res));
            if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return FetchResult.NeedsLogin("Login do Codex (ChatGPT) recusado: rode codex login");
            if (!res.IsSuccessStatusCode)
                return FetchResult.Fail($"Erro {(int)res.StatusCode} ao consultar o uso", ErrorCodes.Other);

            using var body = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            if (!body.RootElement.TryGetProperty("rate_limit", out var rl) || rl.ValueKind != JsonValueKind.Object)
                return FetchResult.Fail("Resposta sem limites de uso", ErrorCodes.Other);
            var (s, sr) = Window(rl, "primary_window", "reset_at");
            var (w, wr) = Window(rl, "secondary_window", "reset_at");
            return FetchResult.Ok(new UsageSnapshot(s, sr, w, wr, DateTimeOffset.Now, "api"));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            return FetchResult.Fail("Sem conexão com a OpenAI", ErrorCodes.Network);
        }
    }

    /// <summary>Procura o "rate_limits" mais recente nos arquivos rollout-*.jsonl.</summary>
    static UsageSnapshot? FromSessionLogs()
    {
        try
        {
            if (!Directory.Exists(SessionsPath))
                return null;
            var files = new DirectoryInfo(SessionsPath)
                .EnumerateFiles("rollout-*.jsonl", SearchOption.AllDirectories)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Take(5);

            foreach (var f in files)
            {
                string? last = null;
                using (var fs = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var sr = new StreamReader(fs))
                {
                    string? line;
                    while ((line = sr.ReadLine()) != null)
                        if (line.Contains("\"rate_limits\"", StringComparison.Ordinal))
                            last = line;
                }
                if (last == null)
                    continue;

                using var doc = JsonDocument.Parse(last);
                var rl = FindProperty(doc.RootElement, "rate_limits");
                if (rl is not { ValueKind: JsonValueKind.Object } r)
                    continue;
                var (s, sAt) = Window(r, "primary", "resets_at");
                var (w, wAt) = Window(r, "secondary", "resets_at");
                return new UsageSnapshot(s, sAt, w, wAt, new DateTimeOffset(f.LastWriteTimeUtc, TimeSpan.Zero).ToLocalTime(), "logs");
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
        }
        return null;
    }

    static JsonElement? FindProperty(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var p in e.EnumerateObject())
        {
            if (p.NameEquals(name))
                return p.Value;
            if (FindProperty(p.Value, name) is { } found)
                return found;
        }
        return null;
    }

    static (double pct, DateTimeOffset? resets) Window(JsonElement parent, string name, string resetField)
    {
        if (!parent.TryGetProperty(name, out var w) || w.ValueKind != JsonValueKind.Object)
            return (0, null);
        var pct = w.TryGetProperty("used_percent", out var u) && u.ValueKind == JsonValueKind.Number ? u.GetDouble() : 0;
        DateTimeOffset? at = w.TryGetProperty(resetField, out var r) && r.ValueKind == JsonValueKind.Number
            ? DateTimeOffset.FromUnixTimeSeconds(r.GetInt64()) : null;
        return (pct, at);
    }
}
