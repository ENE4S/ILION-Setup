// ILION Setup — emblema «Pulso» (variante B): Sputnik de esfera crema lisa con contorno negro, cuatro antenas amarillas
// barridas hacia atrás y anillos de onda amarillos. Estética de cartel de los años 70-80, sin símbolos de ningún país.
// Mientras Setup trabaja, los anillos salen del satélite (pulso) y el satélite gira despacio; en reposo queda quieto.
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Ilion.Setup.App;

internal static class Sputnik
{
    private static readonly Brush Cream = Frozen("#EBDEBD"), Yellow = Frozen("#FFD61A"), Ink = Frozen("#171714"), Shine = Frozen("#FFFFFF");
    private static Brush Frozen(string hex) { var b = (Brush)new BrushConverter().ConvertFromString(hex)!; b.Freeze(); return b; }

    /// <summary>El «Pulso» de «size» píxeles (lienzo de 210×200, como el diseño elegido).</summary>
    public static FrameworkElement Build(double size, double opacity = 1)
    {
        var c = new Canvas { Width = 210, Height = 200 };
        // Tres anillos de onda (ocultos en reposo; laten mientras se trabaja)
        var rings = new Canvas { Width = 210, Height = 200, Tag = "rings", Opacity = 0 };
        for (var i = 0; i < 3; i++)
        {
            var ring = new Ellipse { Width = 160, Height = 160, Stroke = Yellow, StrokeThickness = 4, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(0.45, 0.45), Opacity = 0 };
            Canvas.SetLeft(ring, 25); Canvas.SetTop(ring, 20); rings.Children.Add(ring);
        }
        c.Children.Add(rings);
        // El satélite: antenas (trazo negro debajo, amarillo encima), esfera crema con contorno y brillo
        var sat = new Canvas { Width = 210, Height = 200, Tag = "sat", RenderTransformOrigin = new Point(105.0 / 210, 100.0 / 200), RenderTransform = new RotateTransform(0) };
        var antennas = new[] { (92.0, 112.0, 44.0, 156.0), (98.0, 118.0, 62.0, 166.0), (106.0, 120.0, 90.0, 172.0), (86.0, 104.0, 36.0, 124.0) };
        foreach (var (stroke, width) in new[] { (Ink, 6.0), (Yellow, 3.0) })
            foreach (var (x1, y1, x2, y2) in antennas)
                sat.Children.Add(new Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Stroke = stroke, StrokeThickness = width, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
        var sphere = new Ellipse { Width = 60, Height = 60, Fill = Cream, Stroke = Ink, StrokeThickness = 4 };
        Canvas.SetLeft(sphere, 82); Canvas.SetTop(sphere, 64); sat.Children.Add(sphere);
        var shine = new Ellipse { Width = 14, Height = 14, Fill = Shine };
        Canvas.SetLeft(shine, 94); Canvas.SetTop(shine, 76); sat.Children.Add(shine);
        c.Children.Add(sat);
        return new Viewbox { Width = size, Height = size * 200 / 210, Child = c, Opacity = opacity };
    }

    /// <summary>Pulso: anillos que salen cada 0,8 s y giro lento del satélite (una vuelta cada 9 s). Off: quieto y sin anillos.</summary>
    public static void Spin(FrameworkElement pulso, bool on)
    {
        if (pulso is not Viewbox { Child: Canvas c }) return;
        var rings = (Canvas)c.Children[0]; var sat = (Canvas)c.Children[1];
        rings.Opacity = on ? 1 : 0;
        for (var i = 0; i < rings.Children.Count; i++)
        {
            var ring = (Ellipse)rings.Children[i]; var scale = (ScaleTransform)ring.RenderTransform;
            var begin = TimeSpan.FromSeconds(0.8 * i);
            var grow = on ? new DoubleAnimation(0.45, 1.35, TimeSpan.FromSeconds(2.4)) { BeginTime = begin, RepeatBehavior = RepeatBehavior.Forever } : null;
            var fade = on ? new DoubleAnimation(0.95, 0, TimeSpan.FromSeconds(2.4)) { BeginTime = begin, RepeatBehavior = RepeatBehavior.Forever } : null;
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow); scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            ring.BeginAnimation(UIElement.OpacityProperty, fade);
            if (!on) ring.Opacity = 0;
        }
        var rotate = (RotateTransform)sat.RenderTransform;
        rotate.BeginAnimation(RotateTransform.AngleProperty, on ? new DoubleAnimation(0, 360, TimeSpan.FromSeconds(9)) { RepeatBehavior = RepeatBehavior.Forever } : null);
    }

    /// <summary>Marca de agua: con los anillos visibles y quietos (sin animación), muy tenue.</summary>
    public static FrameworkElement Watermark(double size, double opacity)
    {
        var w = Build(size, opacity);
        var rings = (Canvas)((Canvas)((Viewbox)w).Child).Children[0];
        rings.Opacity = 1;
        for (var i = 0; i < rings.Children.Count; i++)
        {
            var ring = (Ellipse)rings.Children[i]; var s = 0.6 + 0.35 * i;
            ring.RenderTransform = new ScaleTransform(s, s); ring.Opacity = 0.9 - 0.3 * i;
        }
        return w;
    }
}
