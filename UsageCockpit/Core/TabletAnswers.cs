using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace UsageCockpit.Core;

/// <summary>
/// Resposta dada no painel do tablet a uma tarefa que espera o usuário. TaskId: o id público da tarefa
/// (c-… Claude Code, x-… Codex). Question: o texto da pergunta que o painel mostrava, para não aplicar a
/// resposta a outra pergunta. Choice: a opção tocada (ou o texto digitado, em Text).
/// </summary>
public sealed record TabletAnswer(string TaskId, string? Question, string? Choice, string? Text);

/// <summary>
/// Respostas vindas do tablet (só com o tablet ligado; com a API nada disso roda). O app busca as
/// respostas no tablet com uma requisição longa (GET /v1/answers?wait=25): a conexão sai do PC, como os
/// envios, e a resposta chega na hora. Cada uma é entregue à ferramenta: no Claude Code, ao hook
/// PermissionRequest que está esperando (arquivo em %APPDATA%\UsageCockpit\answers); no Codex, pelo
/// CodexAnswers.
/// </summary>
public static class TabletAnswers
{
    /// <summary>O hook espera até isto; o timeout do hook no settings.json do Claude Code é 600 s.</summary>
    public static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(590);
    public const int HookTimeoutSeconds = 600;

    static string Folder => Path.Combine(AppSettings.Folder, "answers");

    /// <summary>Hook esperando a resposta de uma tarefa. Kind: permission ou question; Tool e Text: o que mostrar.</summary>
    public sealed record WaitMark(int Pid, DateTimeOffset At, string Kind = "permission", string? Tool = null, string? Text = null);
    sealed record AnswerFile(string? Question, string? Choice, DateTimeOffset At);

    // ---------- Lado do monitor: o painel só mostra botões quando há quem receba a resposta ----------

    /// <summary>Se um hook está esperando a resposta desta tarefa agora.</summary>
    public static bool IsWaiting(string taskId) => Waiting(taskId) != null;

    /// <summary>O hook (Claude Code ou Codex) que espera a resposta desta tarefa, se ainda estiver vivo.</summary>
    public static WaitMark? Waiting(string taskId)
    {
        try
        {
            if (ReadWait(Path.Combine(Folder, taskId + ".wait")) is not { } w) return null;
            if (DateTimeOffset.Now - w.At > MaxWait) return null;
            using var p = Process.GetProcessById(w.Pid);
            return p.HasExited ? null : w;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// <summary>Entrega a resposta ao hook que espera por ela (arquivo lido pelo processo do hook).</summary>
    public static void DeliverToHook(TabletAnswer a)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var path = Path.Combine(Folder, a.TaskId + ".json");
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(new AnswerFile(a.Question, a.Choice, DateTimeOffset.Now)), new UTF8Encoding(false));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    // ---------- Lado do hook (processo --claude-hook, chamado pelo Claude Code) ----------

    /// <summary>
    /// Hook PermissionRequest com o tablet ligado: espera a resposta tocada no painel e devolve o JSON de
    /// decisão para o Claude Code (stdout). Devolve null (sem decisão: vale o diálogo do terminal) quando
    /// você responde no terminal antes, quando outro pedido da mesma sessão assume ou no tempo máximo.
    /// </summary>
    public static string? WaitForClaude(JsonElement evt, string sessionId)
    {
        var tool = AgentText.Str(evt, "tool_name");
        var input = AgentText.Obj(evt, "tool_input");
        string? askText = null;
        if (tool == "AskUserQuestion")
        {
            // Só uma pergunta: o painel mostra uma de cada vez. Com várias, responda no PC.
            var qs = AgentText.Obj(input, "questions");
            if (qs.ValueKind != JsonValueKind.Array || qs.GetArrayLength() != 1) return null;
            askText = AgentText.Str(qs[0], "question");
            if (string.IsNullOrEmpty(askText)) return null;
        }
        var expected = askText != null ? AgentText.Clip(askText, 600) : AgentText.ToolSummary(tool, input);
        var choice = Wait(AgentText.Id("claude", sessionId), new WaitMark(Environment.ProcessId, DateTimeOffset.Now,
            askText != null ? "question" : "permission", tool, expected), expected, AgentText.Str(evt, "transcript_path"), MaxWait);
        return choice == null ? null : Decision(tool, input, evt, askText, choice);
    }

    /// <summary>
    /// Hook PermissionRequest do Codex com o tablet ligado. O Codex só mostra o pedido na tela depois que o
    /// hook termina, então a espera é curta (CodexWait): sem resposta no tablet, o pedido aparece no PC.
    /// </summary>
    public static string? WaitForCodex(JsonElement evt)
    {
        if (AgentText.Str(evt, "session_id") is not { Length: >= 8 and <= 80 } thread) return null;
        var tool = AgentText.Str(evt, "tool_name");
        var input = AgentText.Obj(evt, "tool_input");
        var text = AgentText.Clip(AgentText.Str(input, "description"), 600) ?? AgentText.ToolSummary(tool, input);
        var choice = Wait(AgentText.Id("codex", thread), new WaitMark(Environment.ProcessId, DateTimeOffset.Now, "permission", tool, text),
            text, null, CodexWait);
        if (choice == null) return null;
        var decision = choice == "Negar"
            ? new JsonObject { ["behavior"] = "deny", ["message"] = "Negado pelo painel do tablet." }
            : new JsonObject { ["behavior"] = "allow" };
        return new JsonObject
        {
            ["hookSpecificOutput"] = new JsonObject { ["hookEventName"] = "PermissionRequest", ["decision"] = decision },
        }.ToJsonString();
    }

    /// <summary>Quanto o hook do Codex espera o tablet antes de deixar o pedido aparecer no PC.</summary>
    public static readonly TimeSpan CodexWait = TimeSpan.FromSeconds(60);
    public const int CodexHookTimeoutSeconds = 90;

    /// <summary>Marca a espera, aguarda a resposta do tablet e devolve a opção escolhida (ou null).</summary>
    static string? Wait(string taskId, WaitMark mark, string? expected, string? transcript, TimeSpan max)
    {
        var pid = mark.Pid;
        var waitPath = Path.Combine(Folder, taskId + ".wait");
        var answerPath = Path.Combine(Folder, taskId + ".json");
        var start = mark.At;
        try
        {
            Directory.CreateDirectory(Folder);
            if (File.Exists(answerPath)) File.Delete(answerPath); // resposta de uma pergunta anterior
            File.WriteAllText(waitPath, JsonSerializer.Serialize(mark), new UTF8Encoding(false));
            var length = Length(transcript);
            while (DateTimeOffset.Now - start < max)
            {
                Thread.Sleep(250);
                if (ReadAnswer(answerPath) is { } a)
                {
                    TryDelete(answerPath);
                    if (a.Question != null && expected != null && a.Question != expected) continue; // era outra pergunta
                    if (a.Choice is { Length: > 0 } choice) return choice;
                }
                // Respondido no terminal: a conversa andou. Outro pedido da sessão: o arquivo de espera mudou.
                if (Length(transcript) != length) return null;
                if (ReadWait(waitPath) is not { } w || w.Pid != pid) return null;
            }
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
        finally
        {
            if (ReadWait(waitPath) is { } w && w.Pid == pid) TryDelete(waitPath);
        }
    }

    static string Decision(string? tool, JsonElement input, JsonElement evt, string? askText, string choice)
    {
        var decision = new JsonObject();
        if (askText != null)
        {
            // AskUserQuestion: "allow" sozinho não responde; a resposta vai em updatedInput.answers.
            var updated = JsonNode.Parse(input.GetRawText()) as JsonObject ?? new JsonObject();
            updated["answers"] = new JsonObject { [askText] = choice };
            decision["behavior"] = "allow";
            decision["updatedInput"] = updated;
        }
        else if (choice == "Negar")
        {
            decision["behavior"] = "deny";
            decision["message"] = "Negado pelo painel do tablet.";
        }
        else
        {
            decision["behavior"] = "allow";
            // O plano aprovado precisa voltar em updatedInput (o Claude Code injeta o conteúdo do plano).
            if (tool == "ExitPlanMode") decision["updatedInput"] = JsonNode.Parse(input.GetRawText());
            var suggestions = AgentText.Obj(evt, "permission_suggestions");
            if (choice == "Permitir sempre" && suggestions.ValueKind == JsonValueKind.Array && suggestions.GetArrayLength() > 0)
                decision["updatedPermissions"] = JsonNode.Parse(suggestions.GetRawText());
        }
        return new JsonObject
        {
            ["hookSpecificOutput"] = new JsonObject
            {
                ["hookEventName"] = "PermissionRequest",
                ["decision"] = decision,
            },
        }.ToJsonString();
    }

    static long Length(string? path)
    {
        try { return path != null && File.Exists(path) ? new FileInfo(path).Length : -1; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return -1; }
    }

    static AnswerFile? ReadAnswer(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<AnswerFile>(File.ReadAllText(path)) : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    static WaitMark? ReadWait(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<WaitMark>(File.ReadAllText(path)) : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>
/// Busca no tablet as respostas tocadas no painel (GET /v1/answers?wait=25, HTTPS com o certificado
/// fixado) e as entrega. Roda só com o tablet ligado; Apply liga ou desliga conforme a configuração.
/// </summary>
public sealed class TabletAnswerReceiver
{
    readonly AppSettings _settings;
    readonly Action<TabletAnswer> _codex;
    CancellationTokenSource? _cts;

    public TabletAnswerReceiver(AppSettings settings, Action<TabletAnswer> codex)
    {
        _settings = settings;
        _codex = codex;
    }

    public void Apply()
    {
        _cts?.Cancel();
        _cts = null;
        if (!_settings.TabletEnabled) return;
        var pin = ApiPublisher.NormalizePin(_settings.TabletCertSha256);
        var key = _settings.TabletKey;
        if (pin.Length != 64 || string.IsNullOrWhiteSpace(key)
            || ApiPublisher.Endpoint(PublishTarget.Tablet, _settings.TabletBaseUrl, "v1/answers?wait=25", out _) is not { } url)
            return;
        var cts = _cts = new CancellationTokenSource();
        _ = Task.Run(() => LoopAsync(url, key!.Trim(), pin, cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
    }

    async Task LoopAsync(Uri url, string key, string pin, CancellationToken ct)
    {
        var client = ApiPublisher.TabletLongPollClient(pin);
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                using var res = await client.SendAsync(req, ct);
                if (res.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                {
                    // Chave recusada: pode ser passageiro (tablet reinstalado ou pareado de novo). Tenta de novo
                    // a cada minuto em vez de desistir até a configuração ser salva.
                    await Task.Delay(TimeSpan.FromMinutes(1), ct);
                    continue;
                }
                if (!res.IsSuccessStatusCode) throw new HttpRequestException(res.StatusCode.ToString());
                var body = await res.Content.ReadAsStringAsync(ct);
                failures = 0;
                foreach (var a in Parse(body))
                {
                    // Com um hook esperando (permissão do Claude Code ou do Codex, pergunta do Claude Code), a
                    // resposta vai para ele; senão, é resposta a uma pergunta assíncrona do Codex (mensagem).
                    if (TabletAnswers.IsWaiting(a.TaskId)) TabletAnswers.DeliverToHook(a);
                    else if (a.TaskId.StartsWith("x-", StringComparison.Ordinal)) _codex(a);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or IOException)
            {
                // Tablet fora do ar: tenta de novo com espera crescente (2 s … 30 s).
                failures++;
                try { await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, 2 * failures)), ct); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    static IEnumerable<TabletAnswer> Parse(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var list = AgentText.Obj(doc.RootElement, "answers");
        if (list.ValueKind != JsonValueKind.Array) yield break;
        foreach (var a in list.EnumerateArray())
        {
            if (AgentText.Str(a, "taskId") is not { Length: > 2 and < 40 } id) continue;
            yield return new TabletAnswer(id, AgentText.Str(a, "question"), AgentText.Str(a, "choice"), AgentText.Str(a, "text"));
        }
    }
}
