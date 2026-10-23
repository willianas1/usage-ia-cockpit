using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Media;

namespace UsageCockpit.Controls;

public static class Palette
{
    public static readonly Brush Clay = Make(0xD9, 0x77, 0x57);
    public static readonly Brush OpenAIGreen = Make(0x10, 0xA3, 0x7F);
    public static readonly Brush Ok = Make(0x3F, 0xB9, 0x50);
    public static readonly Brush Warn = Make(0xEF, 0x9F, 0x27);
    public static readonly Brush Danger = Make(0xE2, 0x4B, 0x4A);
    public static readonly Brush DangerSoft = Make(0xE2, 0x4B, 0x4A, 0x59);
    public static readonly Brush LedOff = Make(0x22, 0x27, 0x2E);
    public static readonly Brush LedRim = Make(0x2D, 0x33, 0x3B);

    /// <summary>
    /// Tema claro: neutros invertidos e tons de texto mais escuros, para ler sobre fundo branco.
    /// As views são recriadas ao trocar de tema, então quem lê estas cores ao desenhar já pega as novas.
    /// </summary>
    public static bool Light { get; private set; }

    static readonly Brush TrackDark = Make(0x23, 0x28, 0x2F), TrackLight = Make(0xE3, 0xE7, 0xEC);
    static readonly Brush TickDark = Make(0x6B, 0x72, 0x80), TickLight = Make(0x8C, 0x95, 0x9F);
    static readonly Brush MutedDark = Make(0x8B, 0x94, 0x9E), MutedLight = Make(0x57, 0x60, 0x6A);
    static readonly Brush TextDark = Make(0xD0, 0xD7, 0xDE), TextLight = Make(0x1F, 0x23, 0x28);
    static readonly Brush NeedleDark = Make(0xF5, 0xF5, 0xF5), NeedleLight = Make(0x24, 0x29, 0x2F);
    static readonly Brush HubDark = Make(0x0B, 0x0D, 0x10), HubLight = Make(0xF0, 0xF2, 0xF5);
    static readonly Brush HubRimDark = Make(0x3A, 0x40, 0x48), HubRimLight = Make(0xC9, 0xD1, 0xD9);
    static readonly Brush WeekBlueDark = Make(0x4C, 0x9A, 0xFF), WeekBlueLight = Make(0x1F, 0x6F, 0xEB);

    public static Brush Track => Light ? TrackLight : TrackDark;
    public static Brush Tick => Light ? TickLight : TickDark;
    public static Brush Muted => Light ? MutedLight : MutedDark;
    public static Brush Text => Light ? TextLight : TextDark;
    public static Brush Needle => Light ? NeedleLight : NeedleDark;
    public static Brush Hub => Light ? HubLight : HubDark;
    public static Brush HubRim => Light ? HubRimLight : HubRimDark;
    public static Brush NoData => Tick;

    // Uso semanal em todos os modos: cores próprias, para não se confundir com o da sessão.
    public static Brush WeekBlue => Light ? WeekBlueLight : WeekBlueDark;
    public static readonly Brush WeekDanger = Make(0xD6, 0x33, 0x84);
    public static readonly Brush WeekWarn = Make(0xE8, 0xD4, 0x4D);
    public static readonly Brush WeekWarnSoft = Make(0xE8, 0xD4, 0x4D, 0x40);
    public static readonly Brush WeekDangerSoft = Make(0xD6, 0x33, 0x84, 0x40);

    /// <summary>Cor do semanal: azul até 80%, depois um vermelho diferente do arco da sessão.</summary>
    public static Brush Week(double pct, bool hasData = true) =>
        !hasData ? NoData : pct < DangerFrom ? WeekBlue : WeekDanger;

    /// <summary>Cor do semanal no modo completo, que tem também a faixa amarela de 60% a 80%.</summary>
    public static Brush WeekBand(double pct, bool hasData = true) =>
        !hasData ? NoData : pct < WarnFrom ? WeekBlue : pct < DangerFrom ? WeekWarn : WeekDanger;

    /// <summary>Verde até 70%, âmbar até 90%, vermelho acima.</summary>
    public static readonly Brush WarnSoft = Make(0xEF, 0x9F, 0x27, 0x59);

    /// <summary>Faixas de uso: amarela a partir de 60%, vermelha a partir de 80%.</summary>
    public const double WarnFrom = 60, DangerFrom = 80;

    /// <summary>Cor do número: verde abaixo de 60%, amarelo até 80%, vermelho acima.</summary>
    public static Brush Level(double pct, bool hasData = true) =>
        !hasData ? NoData
        : Light ? (pct < WarnFrom ? OkText : pct < DangerFrom ? WarnText : DangerText)
        : pct < WarnFrom ? Ok : pct < DangerFrom ? Warn : Danger;

    // Os mesmos tons, escurecidos para os números no tema claro.
    static readonly Brush OkText = Make(0x1A, 0x7F, 0x37);
    static readonly Brush WarnText = Make(0xBC, 0x6C, 0x00);
    static readonly Brush DangerText = Make(0xCF, 0x22, 0x2E);

    /// <summary>Troca o tema: cores desenhadas em código e os pincéis compartilhados do App.xaml.</summary>
    public static void ApplyTheme(bool light)
    {
        Light = light;
        if (System.Windows.Application.Current?.Resources is not { } res) return;
        res["MutedBrush"] = Muted;
        res["TextBrush"] = Text;
        res["ButtonHoverBrush"] = light ? Make(0xE6, 0xEA, 0xEF) : Make(0x2A, 0x30, 0x38);
        res["ButtonHoverText"] = light ? Make(0x1F, 0x23, 0x28) : Make(0xF0, 0xF3, 0xF6);
        res["TipBackground"] = light ? Make(0xFF, 0xFF, 0xFF) : Make(0x15, 0x18, 0x1D);
        res["TipBorder"] = light ? Make(0xD0, 0xD7, 0xDE) : Make(0x2D, 0x33, 0x3B);
    }

    /// <summary>Cor das barras: a da marca abaixo de 60%; depois amarelo e vermelho como as faixas.</summary>
    public static Brush Bar(double pct, Brush brand) =>
        pct < WarnFrom ? brand : pct < DangerFrom ? Warn : Danger;

    static Brush Make(byte r, byte g, byte b, byte a = 0xFF)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }
}

/// <summary>Logos oficiais (Simple Icons, viewBox 24×24).</summary>
public static class Logos
{
    // Precisa vir antes dos logos: campos estáticos são inicializados na ordem em que aparecem.
    static readonly Regex Token = new(@"[A-Za-z]|[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?", RegexOptions.Compiled);

    public static readonly Geometry Claude = Parse("m4.7144 15.9555 4.7174-2.6471.079-.2307-.079-.1275h-.2307l-.7893-.0486-2.6956-.0729-2.3375-.0971-2.2646-.1214-.5707-.1215-.5343-.7042.0546-.3522.4797-.3218.686.0608 1.5179.1032 2.2767.1578 1.6514.0972 2.4468.255h.3886l.0546-.1579-.1336-.0971-.1032-.0972L6.973 9.8356l-2.55-1.6879-1.3356-.9714-.7225-.4918-.3643-.4614-.1578-1.0078.6557-.7225.8803.0607.2246.0607.8925.686 1.9064 1.4754 2.4893 1.8336.3643.3035.1457-.1032.0182-.0728-.164-.2733-1.3539-2.4467-1.445-2.4893-.6435-1.032-.17-.6194c-.0607-.255-.1032-.4674-.1032-.7285L6.287.1335 6.6997 0l.9957.1336.419.3642.6192 1.4147 1.0018 2.2282 1.5543 3.0296.4553.8985.2429.8318.091.255h.1579v-.1457l.1275-1.706.2368-2.0947.2307-2.6957.0789-.7589.3764-.9107.7468-.4918.5828.2793.4797.686-.0668.4433-.2853 1.8517-.5586 2.9021-.3643 1.9429h.2125l.2429-.2429.9835-1.3053 1.6514-2.0643.7286-.8196.85-.9046.5464-.4311h1.0321l.759 1.1293-.34 1.1657-1.0625 1.3478-.8804 1.1414-1.2628 1.7-.7893 1.36.0729.1093.1882-.0183 2.8535-.607 1.5421-.2794 1.8396-.3157.8318.3886.091.3946-.3278.8075-1.967.4857-2.3072.4614-3.4364.8136-.0425.0304.0486.0607 1.5482.1457.6618.0364h1.621l3.0175.2247.7892.522.4736.6376-.079.4857-1.2142.6193-1.6393-.3886-3.825-.9107-1.3113-.3279h-.1822v.1093l1.0929 1.0686 2.0035 1.8092 2.5075 2.3314.1275.5768-.3218.4554-.34-.0486-2.2039-1.6575-.85-.7468-1.9246-1.621h-.1275v.17l.4432.6496 2.3436 3.5214.1214 1.0807-.17.3521-.6071.2125-.6679-.1214-1.3721-1.9246L14.38 17.959l-1.1414-1.9428-.1397.079-.674 7.2552-.3156.3703-.7286.2793-.6071-.4614-.3218-.7468.3218-1.4753.3886-1.9246.3157-1.53.2853-1.9004.17-.6314-.0121-.0425-.1397.0182-1.4328 1.9672-2.1796 2.9446-1.7243 1.8456-.4128.164-.7164-.3704.0667-.6618.4008-.5889 2.386-3.0357 1.4389-1.882.929-1.0868-.0062-.1579h-.0546l-6.3385 4.1164-1.1293.1457-.4857-.4554.0608-.7467.2307-.2429 1.9064-1.3114Z");

    public static readonly Geometry OpenAI = Parse("M22.2819 9.8211a5.9847 5.9847 0 0 0-.5157-4.9108 6.0462 6.0462 0 0 0-6.5098-2.9A6.0651 6.0651 0 0 0 4.9807 4.1818a5.9847 5.9847 0 0 0-3.9977 2.9 6.0462 6.0462 0 0 0 .7427 7.0966 5.98 5.98 0 0 0 .511 4.9107 6.051 6.051 0 0 0 6.5146 2.9001A5.9847 5.9847 0 0 0 13.2599 24a6.0557 6.0557 0 0 0 5.7718-4.2058 5.9894 5.9894 0 0 0 3.9977-2.9001 6.0557 6.0557 0 0 0-.7475-7.0729zm-9.022 12.6081a4.4755 4.4755 0 0 1-2.8764-1.0408l.1419-.0804 4.7783-2.7582a.7948.7948 0 0 0 .3927-.6813v-6.7369l2.02 1.1686a.071.071 0 0 1 .038.052v5.5826a4.504 4.504 0 0 1-4.4945 4.4944zm-9.6607-4.1254a4.4708 4.4708 0 0 1-.5346-3.0137l.142.0852 4.783 2.7582a.7712.7712 0 0 0 .7806 0l5.8428-3.3685v2.3324a.0804.0804 0 0 1-.0332.0615L9.74 19.9502a4.4992 4.4992 0 0 1-6.1408-1.6464zM2.3408 7.8956a4.485 4.485 0 0 1 2.3655-1.9728V11.6a.7664.7664 0 0 0 .3879.6765l5.8144 3.3543-2.0201 1.1685a.0757.0757 0 0 1-.071 0l-4.8303-2.7865A4.504 4.504 0 0 1 2.3408 7.872zm16.5963 3.8558L13.1038 8.364 15.1192 7.2a.0757.0757 0 0 1 .071 0l4.8303 2.7913a4.4944 4.4944 0 0 1-.6765 8.1042v-5.6772a.79.79 0 0 0-.407-.667zm2.0107-3.0231l-.142-.0852-4.7735-2.7818a.7759.7759 0 0 0-.7854 0L9.409 9.2297V6.8974a.0662.0662 0 0 1 .0284-.0615l4.8303-2.7866a4.4992 4.4992 0 0 1 6.6802 4.66zM8.3065 12.863l-2.02-1.1638a.0804.0804 0 0 1-.038-.0567V6.0742a4.4992 4.4992 0 0 1 7.3757-3.4537l-.142.0805L8.704 5.459a.7948.7948 0 0 0-.3927.6813zm1.0976-2.3654l2.602-1.4998 2.6069 1.4998v2.9994l-2.5974 1.4997-2.6067-1.4997Z");

    // Caminhos SVG compactados ("1.2.3", "0-.5") não são aceitos pelo parser do WPF:
    // separamos cada número com espaço. "F1" usa a regra de preenchimento nonzero, padrão do SVG.
    static Geometry Parse(string svgPath)
    {
        var normalized = "F1 " + string.Join(" ", Token.Matches(svgPath).Select(m => m.Value));
        var g = Geometry.Parse(normalized);
        g.Freeze();
        return g;
    }
}
