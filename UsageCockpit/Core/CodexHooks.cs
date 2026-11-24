using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace UsageCockpit.Core;

/// <summary>
/// Respostas do tablet para o Codex. Só existe com o tablet ligado; com a API, nada é instalado.
///
/// - Aprovações: hook PermissionRequest em ~/.codex/hooks.json ("UsageIACockpit.exe --codex-hook"). O
///   Codex só mostra o pedido na tela depois que o hook termina, então ele espera o tablet por pouco tempo
///   (TabletAnswers.CodexWait) e, sem resposta, deixa o pedido aparecer no PC. O Codex pede para confiar
///   no hook uma vez (Configurações › Hooks, ou /hooks na CLI).
/// - Perguntas assíncronas (request_user_input_async) e texto livre: viram mensagem do usuário na conversa,
///   pelo "codex queue --thread &lt;id&gt; --message &lt;texto&gt;".
/// </summary>
public static class CodexHooks
{
    public const string HookArg = "--codex-hook";
    static string HooksPath => Http.Home(".codex", "hooks.json");

    /// <summary>Modo --codex-hook: lê o pedido de aprovação e devolve a decisão tocada no tablet, se houver.</summary>
    public static void Handle()
    {
        try
        {
            using var stdin = Console.OpenStandardInput();
            using var ms = new MemoryStream();
            stdin.CopyTo(ms);
            using var doc = JsonDocument.Parse(ms.ToArray());
            if (AgentText.Str(doc.RootElement, "hook_event_name") != "PermissionRequest") return;
            if (!AppSettings.Load().TabletEnabled) return;
            if (TabletAnswers.WaitForCodex(doc.RootElement) is { } decision) ClaudeHooks.WriteStdout(decision);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
        }
    }

    /// <summary>
    /// Comando do hook. Sem espaços no caminho, vai sem aspas e com barras normais, para funcionar no
    /// cmd, no PowerShell e no bash; com espaços, entre aspas.
    /// </summary>
    static string Command(string exe) =>
        exe.Contains(' ') ? $"\"{exe}\" {HookArg}" : $"{exe.Replace('\\', '/')} {HookArg}";

    /// <summary>Garante a entrada deste app no PermissionRequest do ~/.codex/hooks.json (sem ~/.codex, nada).</summary>
    public static void Ensure()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe) || !Directory.Exists(Path.GetDirectoryName(HooksPath))) return;
        ClaudeHooks.EditJson(HooksPath, root =>
        {
            var hooks = root["hooks"] as JsonObject ?? new JsonObject();
            root["hooks"] = hooks;
            var list = hooks["PermissionRequest"] as JsonArray ?? new JsonArray();
            hooks["PermissionRequest"] = list;
            RemoveOurs(list);
            var command = Command(exe);
            list.Add(new JsonObject
            {
                ["matcher"] = "*",
                ["hooks"] = new JsonArray(new JsonObject
                {
                    ["type"] = "command",
                    ["command"] = command,
                    ["commandWindows"] = command,
                    ["timeout"] = TabletAnswers.CodexHookTimeoutSeconds,
                }),
            });
        });
    }

    /// <summary>Tira só a entrada deste app (tablet desligado ou desinstalação).</summary>
    public static void Remove()
    {
        if (!File.Exists(HooksPath)) return;
        ClaudeHooks.EditJson(HooksPath, root =>
        {
            if (root["hooks"] is not JsonObject hooks || hooks["PermissionRequest"] is not JsonArray list) return;
            RemoveOurs(list);
            if (list.Count == 0) hooks.Remove("PermissionRequest");
            if (hooks.Count == 0) root.Remove("hooks");
        });
    }

    static void RemoveOurs(JsonArray list)
    {
        for (var i = list.Count - 1; i >= 0; i--)
        {
            if (list[i]?["hooks"] is not JsonArray inner) continue;
            for (var j = inner.Count - 1; j >= 0; j--)
                if (ClaudeHooks.IsOurs(inner[j], HookArg)) inner.RemoveAt(j);
            if (inner.Count == 0) list.RemoveAt(i);
        }
    }

    // ---------- Mensagem na conversa (codex queue) ----------

    /// <summary>
    /// Manda a resposta do tablet como mensagem do usuário na conversa do Codex. A conversa é achada pelo
    /// id público da tarefa (x-…) entre os rollouts recentes. Roda fora da thread da interface.
    /// </summary>
    public static void Queue(TabletAnswer a)
    {
        var text = (a.Text ?? a.Choice)?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > 4000) return;
        _ = Task.Run(() =>
        {
            try
            {
                if (ThreadOf(a.TaskId) is not { } thread || CodexJs() is not { } js || Node() is not { } node) return;
                var psi = new ProcessStartInfo(node)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                // ArgumentList: cada argumento vai como está, sem passar por um shell.
                foreach (var arg in new[] { js, "queue", "--thread", thread, "--message", text }) psi.ArgumentList.Add(arg);
                using var p = Process.Start(psi);
                if (p == null) return;
                p.StandardOutput.ReadToEndAsync();
                p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(30_000)) p.Kill(entireProcessTree: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException
                                           or System.ComponentModel.Win32Exception)
            {
                AgentTasksMonitor.Log(e);
            }
        });
    }

    /// <summary>Id da conversa (uuid no fim do nome do rollout) cujo id público é taskId.</summary>
    static string? ThreadOf(string taskId)
    {
        var root = Http.Home(".codex", "sessions");
        if (!Directory.Exists(root)) return null;
        var since = DateTime.UtcNow.AddDays(-2);
        return Directory.EnumerateFiles(root, "rollout-*.jsonl", SearchOption.AllDirectories)
            .Where(f => File.GetLastWriteTimeUtc(f) >= since)
            .Select(f => Path.GetFileNameWithoutExtension(f))
            .Select(n => n.Length >= 36 ? n[^36..] : n)
            .FirstOrDefault(id => AgentText.Id("codex", id) == taskId);
    }

    static string? CodexJs()
    {
        var js = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "npm", "node_modules", "@openai", "codex", "bin", "codex.js");
        return File.Exists(js) ? js : null;
    }

    static string? Node()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try
            {
                var path = Path.Combine(dir.Trim('"'), "node.exe");
                if (File.Exists(path)) return path;
            }
            catch (ArgumentException) { }
        }
        var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe");
        return File.Exists(fallback) ? fallback : null;
    }
}
