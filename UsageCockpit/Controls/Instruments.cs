using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace UsageCockpit.Controls;

/// <summary>Base para os instrumentos: percentual + cor da marca, redesenhados quando mudam.</summary>
public abstract class Instrument : FrameworkElement
{
    public static readonly DependencyProperty PercentProperty = DependencyProperty.Register(
        nameof(Percent), typeof(double), typeof(Instrument),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BrandProperty = DependencyProperty.Register(
        nameof(Brand), typeof(Brush), typeof(Instrument),
        new FrameworkPropertyMetadata(Palette.Clay, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty HasDataProperty = DependencyProperty.Register(
        nameof(HasData), typeof(bool), typeof(Instrument),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Percent { get => (double)GetValue(PercentProperty); set => SetValue(PercentProperty, value); }
    public Brush Brand { get => (Brush)GetValue(BrandProperty); set => SetValue(BrandProperty, value); }
    public bool HasData { get => (bool)GetValue(HasDataProperty); set => SetValue(HasDataProperty, value); }

    protected double P => Math.Clamp(Percent, 0, 100);

    static bool _staticNeedles;

    /// <summary>
    /// Ponteiros e barrinhas sem animação: sem tremor e sem varredura, vão direto ao valor.
    /// O anel em volta do logo continua girando, porque indica a ferramenta em uso.
    /// </summary>
    public static bool StaticNeedles
    {
        get => _staticNeedles;
        set
        {
            if (_staticNeedles == value) return;
            _staticNeedles = value;
            StaticNeedlesChanged?.Invoke(null, EventArgs.Empty);
        }
    }

    public static event EventHandler? StaticNeedlesChanged;

    protected static Pen MakePen(Brush b, double w, PenLineCap cap = PenLineCap.Flat)
    {
        var p = new Pen(b, w) { StartLineCap = cap, EndLineCap = cap };
        p.Freeze();
        return p;
    }
}

/// <summary>Barra estilo "tanque" com marcas em 25/50/75%.</summary>
public sealed class FuelBar : Instrument
{
    protected override Size MeasureOverride(Size available) =>
        new(double.IsInfinity(available.Width) ? 160 : available.Width, 8);

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var clip = new RectangleGeometry(new Rect(0, 0, w, 8), 2, 2);
        dc.PushClip(clip);
        dc.DrawRectangle(Palette.Track, null, new Rect(0, 0, w, 8));
        // Mesmas faixas do mostrador: amarela de 60% a 80%, vermelha de 80% a 100%.
        var warn = w * Palette.WarnFrom / 100;
        var danger = w * Palette.DangerFrom / 100;
        dc.DrawRectangle(Palette.WarnSoft, null, new Rect(warn, 0, danger - warn, 8));
        dc.DrawRectangle(Palette.DangerSoft, null, new Rect(danger, 0, w - danger, 8));
        if (HasData)
            dc.DrawRectangle(Palette.Bar(P, Brand), null, new Rect(0, 0, w * P / 100, 8));
        foreach (var f in new[] { .25, .5, .75 })
            dc.DrawRectangle(Palette.Hub, null, new Rect(Math.Round(w * f), 0, 1, 8));
        dc.Pop();
    }
}

/// <summary>Barrinha fina do modo compacto. Fica vermelha a partir de 90%.</summary>
public sealed class MiniBar : Instrument
{
    static readonly Random Noise = new();

    // A ponta da barra oscila de leve, como os ponteiros do modo completo: mostra que o app está vivo.
    double _jitter;
    int _frame;
    bool _hooked;

    public MiniBar()
    {
        Loaded += (_, _) => { StaticNeedlesChanged += OnStaticChanged; UpdateHook(); };
        Unloaded += (_, _) => { StaticNeedlesChanged -= OnStaticChanged; UpdateHook(); };
    }

    void OnStaticChanged(object? sender, EventArgs e)
    {
        UpdateHook();
        InvalidateVisual();
    }

    // Sem animação, a barra não precisa do relógio: só redesenha quando o percentual muda.
    void UpdateHook()
    {
        var on = IsLoaded && !StaticNeedles;
        if (on == _hooked) return;
        _hooked = on;
        if (on) Ticker.Tick += OnTick;
        else Ticker.Tick -= OnTick;
    }

    public static readonly DependencyProperty SweepTokenProperty = DependencyProperty.Register(
        nameof(SweepToken), typeof(int), typeof(MiniBar),
        new FrameworkPropertyMetadata(0, (d, _) => ((MiniBar)d)._sweepStart = Ticker.Now));

    /// <summary>Cada mudança faz a barra esvaziar e encher de novo, como a varredura dos ponteiros.</summary>
    public int SweepToken { get => (int)GetValue(SweepTokenProperty); set => SetValue(SweepTokenProperty, value); }

    const double DownSeconds = 0.35, UpSeconds = 0.6;
    double _sweepStart = double.NegativeInfinity;
    double _sweepFactor = 1;

    void OnTick(object? sender, EventArgs e)
    {
        var t = Ticker.Now;
        var since = t - _sweepStart;
        var sweeping = since < DownSeconds + UpSeconds;
        if (!sweeping && (++_frame % 2 != 0 || !HasData)) return; // em repouso, metade da taxa do relógio basta
        _sweepFactor = !sweeping ? 1
            : since < DownSeconds ? 1 - since / DownSeconds
            : 1 - Math.Pow(1 - (since - DownSeconds) / UpSeconds, 3);
        _jitter = 0.8 * Math.Sin(t * 17 + Percent) + 0.4 * (Noise.NextDouble() - 0.5);
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size available) => new(64, 5);

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, w, 5), 2.5, 2.5));
        dc.DrawRectangle(Palette.Track, null, new Rect(0, 0, w, 5));
        if (HasData)
        {
            var fill = StaticNeedles ? w * P / 100 : Math.Clamp(w * P / 100 * _sweepFactor + _jitter, 0, w);
            dc.DrawRectangle(Palette.Bar(P, Brand), null, new Rect(0, 0, fill, 5));
        }
        dc.Pop();
    }
}
