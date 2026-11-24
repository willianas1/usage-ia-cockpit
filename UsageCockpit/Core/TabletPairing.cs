using System;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace UsageCockpit.Core;

/// <summary>O que o pareamento devolve: é o que vai para TabletBaseUrl, TabletKey e TabletCertSha256.</summary>
public sealed record TabletPairingResult(string BaseUrl, string Key, string Fingerprint);

/// <summary>Falha do pareamento, com a mensagem para mostrar ao usuário.</summary>
public sealed class TabletPairingException(string message) : Exception(message);

/// <summary>
/// Pareamento com o tablet digitando só o endereço e o código de 8 dígitos da tela dele. É um SPAKE2
/// (troca de chaves autenticada por senha) sobre o grupo MODP de 2048 bits do RFC 3526, com a impressão
/// digital do certificado TLS na transcrição: um aparelho no meio não consegue confirmar, e quem não
/// sabe o código só testa um palpite por tentativa (o tablet troca o código depois de 5).
/// Depois de confirmar, o tablet entrega a chave longa de envio e o PC fixa o certificado.
/// O lado do tablet é usage-ia-tablet/app/src/main/java/com/usageia/tablet/Spake2.java; as contas
/// precisam ser idênticas nos dois. Veja docs/API.md, seção "Tablet na rede local".
/// </summary>
public static class TabletPairing
{
    public const int DefaultPort = 8787;
    const int Len = 256;

    static readonly BigInteger P = BigInteger.Parse(
        "00FFFFFFFFFFFFFFFFC90FDAA22168C234C4C6628B80DC1CD129024E088A67CC74020BBEA63B139B22514A08798E3404DD"
        + "EF9519B3CD3A431B302B0A6DF25F14374FE1356D6D51C245E485B576625E7EC6F44C42E9A637ED6B0BFF5CB6F406B7ED"
        + "EE386BFB5A899FA5AE9F24117C4B1FE649286651ECE45B3DC2007CB8A163BF0598DA48361C55D39A69163FA8FD24CF5F"
        + "83655D23DCA3AD961C62F356208552BB9ED529077096966D670C354E4ABC9804F1746C08CA18217C32905E462E36CE3B"
        + "E39E772C180E86039B2783A2EC07A28FB5C55DF06F4C52C9DE2BCBF6955817183995497CEA956AE515D2261898FA0510"
        + "15728E5A8AACAA68FFFFFFFFFFFFFFFF", NumberStyles.HexNumber);
    static readonly BigInteger Q = P >> 1;
    static readonly BigInteger G = 2;
    static readonly BigInteger M = Element("UsageIA SPAKE2 M");
    static readonly BigInteger N = Element("UsageIA SPAKE2 N");

    /// <summary>Só os dígitos do código ("1234 5678" → "12345678").</summary>
    public static string NormalizeCode(string? code) => new((code ?? "").Where(char.IsAsciiDigit).ToArray());

    /// <summary>
    /// Endereço digitado (IP, IP:porta ou https://IP:porta) → URL base https://host:porta. Sem porta, usa 8787.
    /// </summary>
    public static string? NormalizeAddress(string? input, out string? error)
    {
        error = null;
        var s = (input ?? "").Trim().TrimEnd('/');
        if (s.Length == 0)
        {
            error = "Informe o endereço do tablet (aparece na tela de pareamento dele)";
            return null;
        }
        if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            error = "Use o endereço sem http:// (o tablet só recebe por HTTPS)";
            return null;
        }
        if (s.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) s = s[8..];
        if (!s.Contains(':')) s += ":" + DefaultPort;
        if (!Uri.TryCreate("https://" + s, UriKind.Absolute, out var u) || u.AbsolutePath != "/"
            || !string.IsNullOrEmpty(u.Query) || !string.IsNullOrEmpty(u.UserInfo) || u.HostNameType == UriHostNameType.Unknown)
        {
            error = "Endereço inválido: use o IP da tela do tablet, por exemplo 192.168.1.113";
            return null;
        }
        return $"https://{u.Authority}";
    }

    /// <summary>Endereço para mostrar a partir da URL base salva (sem https:// e sem a porta padrão).</summary>
    public static string DisplayAddress(string? baseUrl)
    {
        if (!Uri.TryCreate(baseUrl ?? "", UriKind.Absolute, out var u)) return "";
        return u.Port == DefaultPort ? u.Host : u.Authority;
    }

    public static async Task<TabletPairingResult> PairAsync(string address, string code, CancellationToken ct = default)
    {
        if (NormalizeAddress(address, out var error) is not { } baseUrl) throw new TabletPairingException(error!);
        code = NormalizeCode(code);
        if (code.Length != 8) throw new TabletPairingException("O código tem 8 dígitos (aparece na tela do tablet)");

        // O primeiro certificado visto fica fixado para as duas etapas; a transcrição prova que é o do tablet.
        string? seen = null;
        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = (_, cert, _, _) =>
            {
                if (cert == null) return false;
                var fp = cert.GetCertHashString(HashAlgorithmName.SHA256).ToUpperInvariant();
                return (seen ??= fp) == fp;
            },
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"UsageCockpit/{ApiPublisher.AppVersion}");

        var w = W(code);
        var x = RandomExponent();
        var xElem = BigInteger.ModPow(G, x, P) * BigInteger.ModPow(M, w, P) % P;

        var start = await PostAsync(client, $"{baseUrl}/v1/pair/start", new JsonObject { ["x"] = Hex(Encode(xElem)) }, ct);
        var fingerprint = seen ?? throw new TabletPairingException("O tablet não apresentou certificado");
        var session = start["session"]?.GetValue<string>();
        var yBytes = Unhex(start["y"]?.GetValue<string>());
        var tabletConfirm = Unhex(start["confirm"]?.GetValue<string>());
        if (session == null || yBytes == null || tabletConfirm == null)
            throw new TabletPairingException("Resposta inesperada do tablet");

        var (yElem, k) = await Task.Run(() =>
        {
            var y = Decode(yBytes);
            return (y, BigInteger.ModPow(y * BigInteger.ModPow(BigInteger.ModPow(N, w, P), P - 2, P) % P, x, P));
        }, ct);
        if (k <= BigInteger.One) throw new TabletPairingException("Resposta inválida do tablet");
        var confirmKey = HMACSHA256.HashData(Transcript(fingerprint, xElem, yElem, k, w), "UsageIA confirm"u8);
        if (!CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(confirmKey, "tablet"u8), tabletConfirm))
            throw new TabletPairingException(
                "Código errado. Confira o código na tela do tablet: ele muda depois de 5 tentativas erradas.");

        var finish = await PostAsync(client, $"{baseUrl}/v1/pair/finish", new JsonObject
        {
            ["session"] = session,
            ["confirm"] = Hex(HMACSHA256.HashData(confirmKey, "pc"u8)),
        }, ct);
        var key = finish["key"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(key)) throw new TabletPairingException("O tablet não entregou a chave");
        return new TabletPairingResult(baseUrl, key, fingerprint);
    }

    static async Task<JsonNode> PostAsync(HttpClient client, string url, JsonObject body, CancellationToken ct)
    {
        HttpResponseMessage res;
        try
        {
            res = await client.PostAsync(url, new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), ct);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TabletPairingException("Tempo esgotado. Confira o endereço e se o PC e o tablet estão na mesma rede.");
        }
        catch (HttpRequestException)
        {
            throw new TabletPairingException("Sem conexão com o tablet. Confira o endereço e se o PC e o tablet estão na mesma rede.");
        }
        using (res)
        {
            var text = await res.Content.ReadAsStringAsync(ct);
            if (res.IsSuccessStatusCode)
            {
                try { return JsonNode.Parse(text) ?? throw new JsonException(); }
                catch (JsonException) { throw new TabletPairingException("Resposta inesperada do tablet"); }
            }
            string? code = null;
            try { code = JsonNode.Parse(text)?["error"]?["code"]?.GetValue<string>(); }
            catch (Exception e) when (e is JsonException or InvalidOperationException) { }
            throw new TabletPairingException(((int)res.StatusCode, code) switch
            {
                (403, _) => "O tablet não está aceitando pareamento. Toque no botão de conexão no topo do painel e tente de novo.",
                (429, "code_changed") => "O código mudou depois de 5 tentativas. Use o código novo da tela do tablet.",
                (429, _) => $"Muitas tentativas deste PC. Espere {Minutes(res)} e tente de novo.",
                (404, _) => "O app do tablet é de uma versão sem pareamento por código. Atualize o app do tablet ou use a configuração manual.",
                (401, _) => "O tablet recusou a confirmação. Tente de novo.",
                var (status, _) => $"O tablet respondeu {status}",
            });
        }
    }

    static string Minutes(HttpResponseMessage res)
    {
        var s = res.Headers.RetryAfter?.Delta?.TotalSeconds ?? 600;
        var m = Math.Max(1, (int)Math.Ceiling(s / 60));
        return m == 1 ? "1 minuto" : $"{m} minutos";
    }

    // ---------- SPAKE2 (mesmas contas do Spake2.java) ----------

    static BigInteger W(string code) =>
        Unsigned(SHA256.HashData(Encoding.UTF8.GetBytes("UsageIA pair code:" + code))) % Q;

    static BigInteger RandomExponent() => Unsigned(RandomNumberGenerator.GetBytes(40)) + BigInteger.One;

    static BigInteger Element(string label)
    {
        var buf = new byte[9 * 32];
        for (var i = 0; i < 9; i++)
            SHA256.HashData(Encoding.UTF8.GetBytes($"{label}:{i}"), buf.AsSpan(i * 32, 32));
        var v = Unsigned(buf) % P;
        return v * v % P;
    }

    /// <summary>Elemento recebido: 256 bytes, 1 &lt; e &lt; P-1 e e^Q = 1 (recusa valores forjados).</summary>
    static BigInteger Decode(byte[] b)
    {
        if (b.Length != Len) throw new TabletPairingException("Resposta inválida do tablet");
        var e = Unsigned(b);
        if (e <= BigInteger.One || e >= P - 1 || !BigInteger.ModPow(e, Q, P).IsOne)
            throw new TabletPairingException("Resposta inválida do tablet");
        return e;
    }

    static byte[] Encode(BigInteger e)
    {
        var raw = e.ToByteArray(isUnsigned: true, isBigEndian: true);
        var out_ = new byte[Len];
        raw.AsSpan(Math.Max(0, raw.Length - Len)).CopyTo(out_.AsSpan(Len - Math.Min(raw.Length, Len)));
        return out_;
    }

    static byte[] Transcript(string fingerprint, BigInteger x, BigInteger y, BigInteger k, BigInteger w)
    {
        using var ms = new System.IO.MemoryStream();
        void Field(byte[] v)
        {
            Span<byte> len = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(len, v.Length);
            ms.Write(len);
            ms.Write(v);
        }
        Field("UsageIA-pair-v1"u8.ToArray());
        Field(Encoding.ASCII.GetBytes(fingerprint));
        Field(Encode(x));
        Field(Encode(y));
        Field(Encode(k));
        Field(Encode(w));
        return SHA256.HashData(ms.ToArray());
    }

    static BigInteger Unsigned(byte[] b) => new(b, isUnsigned: true, isBigEndian: true);

    static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

    static byte[]? Unhex(string? s)
    {
        if (s == null) return null;
        try { return Convert.FromHexString(s); }
        catch (FormatException) { return null; }
    }
}
