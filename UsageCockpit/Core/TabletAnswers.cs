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
/// envios, e a resposta chega na hora. Cada uma é entregue ao hook PermissionRequest que está esperando
/// (Claude Code ou Codex), por um arquivo em %USERPROFILE%\.usage-ia-cockpit\answers.
/// </summary>
public static class TabletAnswers
{
    /// <summary>O hook espera até isto; o timeout do hook no settings.json do Claude Code é 600 s.</summary>
    public static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(590);
    public const int HookTimeoutSeconds = 600;

    /// <summary>
    /// Pasta trocada entre o Cockpit e os hooks. Fica fora do AppData: o Claude Desktop e o Codex Desktop são
    /// apps empacotados (MSIX), e os hooks que eles rodam leem e gravam o AppData numa cópia virtualizada do
    /// pacote, que o Cockpit não enxerga. A pasta do usuário não é virtualizada.
    /// </summary>
    public static string SharedFolder => Http.Home(".usage-ia-cockpit");
    static string Folder => Path.Combine(SharedFolder, "answers");
    static string TabletFlag => Path.Combine(SharedFolder, "tablet-ligado");

    /// <summary>Opção especial do painel: pular a pergunta.</summary>
    public const string Skip = "__pular__";

    /// <summary>Se o tablet está ligado no Cockpit (o hook não lê a configuração, que pode estar virtualizada).</summary>
    public static bool TabletOn() => File.Exists(TabletFlag);

    /// <summary>Chamado pelo Cockpit quando a configuração muda.</summary>
    public static void SetTabletOn(bool on)
    {
        try
        {
            if (on)
            {
                Directory.CreateDirectory(SharedFolder);
                File.WriteAllText(TabletFlag, DateTimeOffset.Now.ToString("O"));
            }
            else if (File.Exists(TabletFlag)) File.Delete(TabletFlag);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Hook esperando a resposta de uma tarefa. Kind: permission ou question; Tool e Text: o que mostrar.</summary>
    public sealed record WaitMark(int Pid, DateTimeOffset At, string Kind = "permission", string? Tool = null, string? Text = null);
    sealed record AnswerFile(string? Question, string? Choice, DateTimeOffset At, string? Text = null);

    // ---------- Lado do monitor: o painel só mostra botões quando há quem receba a resposta ----------

    /// <summary>Se um hook está esperando a resposta desta tarefa agora.</summary>
    public static bool IsWaiting(string taskId) => Waiting(taskId) != null;

    /// <summary>
    /// O hook (Claude Code ou Codex) que espera a resposta desta tarefa, se ainda estiver vivo. O hook renova o
    /// arquivo de espera a cada 2 s; vale enquanto ele for recente. Não dá para olhar o processo: o hook pode
    /// rodar dentro do pacote do Claude Desktop ou do Codex Desktop, fora do alcance do Cockpit.
    /// </summary>
    public static WaitMark? Waiting(string taskId)
    {
        try
        {
            var path = Path.Combine(Folder, taskId + ".wait");
            if (ReadWait(path) is not { } w) return null;
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > AliveFor) return null;
            return DateTimeOffset.Now - w.At > MaxWait ? null : w;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    static readonly TimeSpan AliveEvery = TimeSpan.FromSeconds(2);
    static readonly TimeSpan AliveFor = TimeSpan.FromSeconds(10);

    /// <summary>Entrega a resposta ao hook que espera por ela (arquivo lido pelo processo do hook).</summary>
    public static void DeliverToHook(TabletAnswer a)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var path = Path.Combine(Folder, a.TaskId + ".json");
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(new AnswerFile(a.Question, a.Choice, DateTimeOffset.Now, a.Text)), new UTF8Encoding(false));
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
        var answer = Wait(AgentText.Id("claude", sessionId), new WaitMark(Environment.ProcessId, DateTimeOffset.Now,
            askText != null ? "question" : "permission", tool, expected), expected, AgentText.Str(evt, "transcript_path"), MaxWait);
        return answer == null ? null : Decision(tool, input, evt, askText, answer.Choice, answer.Text);
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
        var answer = Wait(AgentText.Id("codex", thread), new WaitMark(Environment.ProcessId, DateTimeOffset.Now, "permission", tool, text),
            text, null, CodexWait);
        if (answer == null) return null;
        var decision = answer.Choice == "Negar"
            ? new JsonObject { ["behavior"] = "deny", ["message"] = DenyMessage(answer.Text) }
            : new JsonObject { ["behavior"] = "allow" };
        return new JsonObject
        {
            ["hookSpecificOutput"] = new JsonObject { ["hookEventName"] = "PermissionRequest", ["decision"] = decision },
        }.ToJsonString();
    }

    /// <summary>Quanto o hook do Codex espera o tablet antes de deixar o pedido aparecer no PC.</summary>
    public static readonly TimeSpan CodexWait = TimeSpan.FromSeconds(60);
    public const int CodexHookTimeoutSeconds = 90;

    static string DenyMessage(string? text) =>
        string.IsNullOrWhiteSpace(text) ? "Negado pelo painel do tablet." : "Negado pelo painel do tablet: " + text.Trim();

    /// <summary>Marca a espera, aguarda a resposta do tablet e devolve a resposta (opção e/ou texto), ou null.</summary>
    static AnswerFile? Wait(string taskId, WaitMark mark, string? expected, string? transcript, TimeSpan max)
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
            var alive = DateTimeOffset.Now;
            while (DateTimeOffset.Now - start < max)
            {
                Thread.Sleep(250);
                if (DateTimeOffset.Now - alive >= AliveEvery)
                {
                    alive = DateTimeOffset.Now;
                    try { File.SetLastWriteTimeUtc(waitPath, DateTime.UtcNow); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                }
                if (ReadAnswer(answerPath) is { } a)
                {
                    TryDelete(answerPath);
                    if (a.Question != null && expected != null && a.Question != expected) continue; // era outra pergunta
                    if (a.Choice is { Length: > 0 } || !string.IsNullOrWhiteSpace(a.Text)) return a;
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

    static string Decision(string? tool, JsonElement input, JsonElement evt, string? askText, string? choice, string? text)
    {
        var decision = new JsonObject();
        if (askText != null && choice == Skip)
        {
            // Pular: a pergunta é recusada com um recado, e o Claude segue sem a resposta.
            decision["behavior"] = "deny";
            decision["message"] = "O usuário pulou esta pergunta pelo painel do tablet. Siga sem essa resposta.";
        }
        else if (askText != null)
        {
            // Opção tocada ou "Outra resposta…" escrita no tablet.
            // AskUserQuestion: "allow" sozinho não responde; a resposta vai em updatedInput.answers.
            var updated = JsonNode.Parse(input.GetRawText()) as JsonObject ?? new JsonObject();
            updated["answers"] = new JsonObject { [askText] = string.IsNullOrWhiteSpace(text) ? choice : text.Trim() };
            decision["behavior"] = "allow";
            decision["updatedInput"] = updated;
        }
        else if (choice == "Negar")
        {
            // "Negar e explicar…": o texto vai como recado para o Claude fazer diferente.
            decision["behavior"] = "deny";
            decision["message"] = DenyMessage(text);
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
    CancellationTokenSource? _cts;

    public TabletAnswerReceiver(AppSettings settings)
    {
        _settings = settings;
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
                    // Só há quem receba quando um hook está esperando (permissão do Claude Code ou do Codex,
                    // pergunta do Claude Code). Sem hook, a pergunta já foi respondida no PC: a resposta é descartada.
                    if (TabletAnswers.IsWaiting(a.TaskId)) TabletAnswers.DeliverToHook(a);
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
