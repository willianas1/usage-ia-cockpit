using System;

namespace UsageCockpit.Core;

/// <summary>Preço da API em US$ por milhão de tokens, por tipo de token.</summary>
public sealed record ModelPrice(string Name, double Input, double Output, double CacheRead, double CacheWrite5m, double CacheWrite1h)
{
    /// <summary>Custo de uma contagem de tokens. Gravação no cache sem detalhe de duração conta como 5 min.</summary>
    public double Cost(long input, long write5m, long write1h, long output, long read) =>
        (input * Input + write5m * CacheWrite5m + write1h * CacheWrite1h + output * Output + read * CacheRead) / 1e6;
}

/// <summary>
/// Custo equivalente das tarefas: cada tipo de token pelo preço da API do modelo, como os fornecedores
/// cobram. Nas assinaturas (Claude Pro/Max, ChatGPT) o limite segue a mesma lógica, mas a fórmula não é
/// publicada: o valor serve de comparação, não é cobrança.
/// Claude: tabela da Anthropic; gravação no cache custa 1,25× a entrada (5 min) ou 2× (1 h).
/// Codex: gpt-5.6-terra pela página do modelo na OpenAI; gpt-6.2-sol por sites que acompanham a tabela
/// da OpenAI (a página oficial não foi consultada). Revisar quando os modelos mudarem. Modelo fora da
/// tabela: sem custo (nulo).
/// </summary>
public static class ModelPrices
{
    static ModelPrice Claude(string name, double input, double output, double read) =>
        new(name, input, output, read, input * 1.25, input * 2);

    // Do mais específico para o mais geral: "claude-opus-5" também é prefixo de "claude-opus-5-5".
    static readonly (string Prefix, ModelPrice Price)[] Table =
    {
        ("claude-fable-5-2", Claude("Claude Fable 5.2", 8, 40, 0.20)),
        ("claude-mythos-5-2", Claude("Claude Mythos 5.2", 8, 40, 0.20)),
        ("claude-fable-5-1", Claude("Claude Fable 5.1", 10, 50, 0.25)),
        ("claude-mythos-5-1", Claude("Claude Mythos 5.1", 10, 50, 0.25)),
        ("claude-fable-5", Claude("Claude Fable 5", 10, 50, 1.00)),
        ("claude-mythos-5", Claude("Claude Mythos 5", 10, 50, 1.00)),
        ("claude-opus-5-5", Claude("Claude Opus 5.5", 4, 20, 0.20)),
        ("claude-opus-5", Claude("Claude Opus 5", 5, 25, 0.50)),
        ("claude-opus-4-8", Claude("Claude Opus 4.8", 5, 25, 0.50)),
        ("claude-opus-4-7", Claude("Claude Opus 4.7", 5, 25, 0.50)),
        ("claude-opus-4-6", Claude("Claude Opus 4.6", 5, 25, 0.50)),
        ("claude-sonnet-5-5", Claude("Claude Sonnet 5.5", 2, 10, 0.10)),
        ("claude-sonnet-5", Claude("Claude Sonnet 5", 2, 10, 0.20)),
        ("claude-sonnet-4-6", Claude("Claude Sonnet 4.6", 3, 15, 0.30)),
        ("claude-haiku-5-5", Claude("Claude Haiku 5.5", 0.10, 0.50, 0.01)),
        ("claude-haiku-4-5", Claude("Claude Haiku 4.5", 1, 5, 0.10)),
        ("gpt-6.2-sol", new ModelPrice("GPT-6.2 Sol", 2, 10, 0.10, 2.50, 2.50)),
        // Página do modelo na OpenAI; sem preço de gravação no cache publicado: usa o da entrada.
        ("gpt-5.6-terra", new ModelPrice("GPT-5.6 Terra", 2, 12, 0.20, 2, 2)),
    };

    /// <summary>Preço do modelo (aceita sufixos como "[1m]" ou datas), ou nulo se não estiver na tabela.</summary>
    public static ModelPrice? Find(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return null;
        var id = model.Trim().ToLowerInvariant();
        var bracket = id.IndexOf('[');
        if (bracket > 0) id = id[..bracket];
        if (id.StartsWith("anthropic.", StringComparison.Ordinal)) id = id["anthropic.".Length..];
        foreach (var (prefix, price) in Table)
            if (id.StartsWith(prefix, StringComparison.Ordinal)) return price;
        return null;
    }
}
