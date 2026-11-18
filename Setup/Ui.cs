using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Ilion.Setup.App;

public enum Tone { Ok, Warn, Error, Neutral, Accent }

/// <summary>Fábrica de elementos con el estilo central. Las vistas dinámicas sólo usan estas piezas.</summary>
internal static class Ui
{
    /// <summary>Escala de la interfaz elegida por el usuario (Ajustes\escala.txt).</summary>
    public static readonly double[] Scales = [0.8, 0.9, 1.0];
    public static double Scale { get; set; } = 1.0;
    public static double LoadScale(string dataDirectory)
    {
        try { var file = System.IO.Path.Combine(dataDirectory, "Ajustes", "escala.txt"); if (System.IO.File.Exists(file) && double.TryParse(System.IO.File.ReadAllText(file), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) && Scales.Contains(v)) return Scale = v; }
        catch (System.IO.IOException) { }
        return Scale = 1.0;
    }
    public static void SaveScale(string dataDirectory)
    {
        try { var file = System.IO.Path.Combine(dataDirectory, "Ajustes", "escala.txt"); System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!); System.IO.File.WriteAllText(file, Scale.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
        catch (System.IO.IOException) { }
    }

    public static Style S(string key) => (Style)Application.Current.FindResource(key);
    public static Brush B(string key) => (Brush)Application.Current.FindResource(key);

    public static Brush ToneBrush(Tone tone) => tone switch
    {
        Tone.Ok => B("B.Ok"), Tone.Warn => B("B.Warn"), Tone.Error => B("B.Error"), Tone.Accent => B("B.Accent"), _ => B("B.TextDim")
    };
    public static Brush ToneSoft(Tone tone) => tone switch
    {
        Tone.Ok or Tone.Accent => B("B.AccentSoft"), Tone.Warn => B("B.WarnSoft"), Tone.Error => B("B.ErrorSoft"), _ => Brushes.Transparent
    };

    public static TextBlock Text(string text, string style, string? tip = null)
    {
        var block = new TextBlock { Text = text, Style = S(style) };
        if (tip is not null) Tip(block, tip);
        return block;
    }

    /// <summary>Ayuda emergente junto al cursor, desplazada para no tapar los botones; se cierra al retirar el cursor.</summary>
    public static T Tip<T>(T element, string tip) where T : FrameworkElement
    {
        element.ToolTip = Short(tip);
        ToolTipService.SetPlacement(element, System.Windows.Controls.Primitives.PlacementMode.Mouse);
        ToolTipService.SetVerticalOffset(element, 22);
        ToolTipService.SetHorizontalOffset(element, 8);
        ToolTipService.SetInitialShowDelay(element, 350);
        ToolTipService.SetShowDuration(element, 30000);
        return element;
    }

    /// <summary>Pastilla de estado: el mismo componente en las tres pestañas.</summary>
    public static Border Pill(string text, Tone tone, string? tip = null) => new Border
    {
        CornerRadius = new CornerRadius(3), Padding = new Thickness(8, 3, 8, 3), BorderThickness = new Thickness(1),
        BorderBrush = ToneBrush(tone), Background = ToneSoft(tone), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = text, Foreground = ToneBrush(tone), FontSize = 12, FontFamily = (FontFamily)Application.Current.FindResource("F.Strong"), TextTrimming = TextTrimming.CharacterEllipsis }
    }.WithTip(tip);
    /// <summary>Las ayudas emergentes son cortas: frases completas hasta ~170 caracteres (el detalle va en el panel o en la confirmación).</summary>
    public static string Short(string tip)
    {
        const int max = 170;
        tip = tip.Trim();
        if (tip.Length <= max) return tip;
        var cut = -1;
        for (var i = 0; i < max; i++)
            if ((tip[i] is '.' or '\n') && (i + 1 == tip.Length || char.IsWhiteSpace(tip[i + 1]))) cut = i;
        return cut > 20 ? tip[..(cut + 1)].Trim() : tip[..max].TrimEnd() + "…";
    }

    private static Border WithTip(this Border b, string? tip) { if (tip is not null) Tip(b, tip); return b; }

    public static Button Button(string text, string style, RoutedEventHandler click, double minWidth = 110, string? tip = null)
    {
        var button = new Button { Content = text, Style = S(style), MinWidth = minWidth, ToolTip = tip is null ? null : Short(tip) };
        button.Click += click;
        return button;
    }

    /// <summary>Panel con encabezado (barra de acento + título + dato a la derecha) y contenido.</summary>
    public static Border Panel(string title, string? right, UIElement content, string style = "Panel", bool compact = false, string? headerColor = null)
    {
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, compact ? 8 : 10) };
        var bar = new Border { Style = S("AccentBar") };
        Brush? brand = headerColor is null ? null : (SolidColorBrush)new BrushConverter().ConvertFromString(headerColor)!;
        if (brand is not null) bar.Background = brand;
        header.Children.Add(bar);
        if (right is not null) { var r = Text(right, "T.Kicker"); r.VerticalAlignment = VerticalAlignment.Center; DockPanel.SetDock(r, Dock.Right); header.Children.Add(r); }
        var t = Text(title, "T.Section"); t.TextTrimming = TextTrimming.CharacterEllipsis; t.ToolTip = title;
        if (compact) t.FontSize = 15.5;
        if (brand is not null) { t.Foreground = brand; t.FontSize = 17; }
        header.Children.Add(t);
        var dock = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        dock.Children.Add(header);
        dock.Children.Add(content);
        var border = new Border { Style = S(style), Margin = new Thickness(6), Child = dock };
        if (compact) border.Padding = new Thickness(14, 12, 12, 10);
        return border;
    }

    public static Grid KeyValue(string key, string value, Tone? tone = null, string? tip = null, double keyWidth = 112, bool wrap = false)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 7) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(keyWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var k = Text(key, "T.Note"); k.TextWrapping = TextWrapping.NoWrap; k.TextTrimming = TextTrimming.CharacterEllipsis; k.Margin = new Thickness(0, 0, 12, 0); k.ToolTip = key;
        var v = Text(value, "T.Value", tip ?? value); Grid.SetColumn(v, 1);
        if (wrap) { v.TextWrapping = TextWrapping.Wrap; v.TextTrimming = TextTrimming.None; }
        if (tone is { } t) v.Foreground = ToneBrush(t);
        grid.Children.Add(k); grid.Children.Add(v);
        return grid;
    }

    public static Border Fact(string label, string value, Brush? valueBrush = null) => new()
    {
        Margin = new Thickness(0, 0, 8, 6), Padding = new Thickness(10, 4, 10, 4), CornerRadius = new CornerRadius(3),
        Background = B("B.Bg"), BorderBrush = B("B.Line"), BorderThickness = new Thickness(1),
        Child = new TextBlock
        {
            Inlines =
            {
                new System.Windows.Documents.Run(label + "  ") { Foreground = B("B.TextDim"), FontSize = 12 },
                new System.Windows.Documents.Run(value) { Foreground = valueBrush ?? B("B.Text"), FontSize = 13, FontFamily = (FontFamily)Application.Current.FindResource("F.Strong") }
            }
        }
    };
}

/// <summary>Diálogo de confirmación con la misma estética. Devuelve true sólo si el usuario confirma.</summary>
internal static class Confirm
{
    public static string? CaptureFile;
    public static bool Ask(Window owner, string title, (string Key, string Value)[] lines, string? warning, string accept, bool danger = false, bool infoOnly = false) =>
        Choose(owner, title, lines, warning, infoOnly ? [accept] : ["Cancelar", accept], danger) == (infoOnly ? 0 : 1);

    /// <summary>Diálogo de terminal: devuelve el índice del botón pulsado (−1 si se cierra). El último botón es el principal.</summary>
    /// <summary>Ventana de diálogo común (borde, cabecera con barra de acento, arrastrable, Esc cierra).</summary>
    internal static (Window Dialog, StackPanel Stack) Shell(Window owner, string title, bool danger, double width = 600)
    {
        var dialog = new Window
        {
            Owner = owner, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, SizeToContent = SizeToContent.Height, Width = width,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = System.Windows.Media.Brushes.Transparent, ShowInTaskbar = false, AllowsTransparency = true,
            FontFamily = (FontFamily)Application.Current.FindResource("F.Ui"), Foreground = Ui.B("B.Text")
        };
        var stack = new StackPanel { Margin = new Thickness(26, 22, 26, 22) };
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
        head.Children.Add(new Border { Style = Ui.S("AccentBar"), Background = danger ? Ui.B("B.Error") : Ui.B("B.Accent") });
        head.Children.Add(Ui.Text(title, "T.Section"));
        stack.Children.Add(head);
        dialog.Width = width * Ui.Scale;
        dialog.Content = new Border
        {
            BorderBrush = danger ? Ui.B("B.Error") : Ui.B("B.LineStrong"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            Background = Ui.B("B.PanelFace"), Child = stack,
            LayoutTransform = Ui.Scale == 1.0 ? Transform.Identity : new ScaleTransform(Ui.Scale, Ui.Scale)
        };
        dialog.KeyDown += (_, e) => { if (e.Key == Key.Escape) dialog.Close(); };
        dialog.MouseLeftButtonDown += (_, e) => { if (e.OriginalSource is not TextBox) try { dialog.DragMove(); } catch (InvalidOperationException) { } };
        return (dialog, stack);
    }

    /// <summary>Formulario: el contenido lo aporta quien llama; «accept» valida (null = correcto) antes de cerrar.</summary>
    public static bool Form(Window owner, string title, UIElement body, string accept, Func<string?> validate, double width = 620)
    {
        var (dialog, stack) = Shell(owner, title, false, width);
        stack.Children.Add(body);
        var error = new TextBlock { Foreground = Ui.B("B.Error"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
        stack.Children.Add(error);
        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var ok = false;
        bar.Children.Add(Ui.Button("Cancelar", "Btn", (_, _) => dialog.Close(), 120));
        var acceptButton = Ui.Button(accept, "Btn.Primary", (_, _) =>
        {
            if (validate() is { } problem) { error.Text = problem; error.Visibility = Visibility.Visible; return; }
            ok = true; dialog.Close();
        }, 150);
        acceptButton.Margin = new Thickness(8, 0, 0, 0); acceptButton.IsDefault = true;
        bar.Children.Add(acceptButton);
        stack.Children.Add(bar);
        CaptureOnShow(dialog);
        dialog.ShowDialog();
        return ok;
    }

    /// <summary>Ventana de solo lectura con un contenido libre y «Cerrar».</summary>
    public static void View(Window owner, string title, UIElement body, double width = 980)
    {
        var (dialog, stack) = Shell(owner, title, false, width);
        stack.Children.Add(body);
        var close = Ui.Button("Cerrar", "Btn.Primary", (_, _) => dialog.Close(), 150);
        close.HorizontalAlignment = HorizontalAlignment.Right; close.Margin = new Thickness(0, 16, 0, 0); close.IsDefault = true;
        stack.Children.Add(close);
        CaptureOnShow(dialog);
        dialog.ShowDialog();
    }

    /// <summary>Prueba visual: si hay un archivo de captura pendiente, renderiza el diálogo real a PNG al mostrarse y lo cierra.</summary>
    private static void CaptureOnShow(Window dialog)
    {
        if (CaptureFile is not { } capture) return;
            dialog.ContentRendered += (_, _) =>
            {
                var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(dialog);
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(dialog.ActualWidth * dpi.DpiScaleX), (int)(dialog.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, System.Windows.Media.PixelFormats.Pbgra32);
                bitmap.Render(dialog);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using (var stream = System.IO.File.Create(capture)) encoder.Save(stream);
                CaptureFile = null; dialog.Close();
            };
    }

    public static int Choose(Window owner, string title, (string Key, string Value)[] lines, string? warning, string[] buttons, bool danger = false)
    {
        var (dialog, stack) = Shell(owner, title, danger);
        var keyWidth = lines.Length == 0 ? 112 : Math.Clamp(lines.Max(l => l.Key.Length) * 8.2 + 16, 112, 210);
        foreach (var (key, value) in lines) stack.Children.Add(Ui.KeyValue(key, value, keyWidth: keyWidth, wrap: true));
        if (warning is not null)
            stack.Children.Add(new Border
            {
                Margin = new Thickness(0, lines.Length == 0 ? 0 : 10, 0, 0), Padding = new Thickness(14, 10, 14, 10), CornerRadius = new CornerRadius(6),
                BorderBrush = danger ? Ui.B("B.Error") : Ui.B("B.Warn"), BorderThickness = new Thickness(3, 0, 0, 0), Background = Ui.B("B.Raised"),
                Child = new TextBlock { Text = warning, TextWrapping = TextWrapping.Wrap, Foreground = Ui.B("B.Text"), FontSize = 13.5, LineHeight = 20 }
            });
        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        var result = -1;
        for (var i = 0; i < buttons.Length; i++)
        {
            var index = i; var last = i == buttons.Length - 1;
            var b = Ui.Button(buttons[i], last ? (danger ? "Btn.Danger" : "Btn.Primary") : "Btn", (_, _) => { result = index; dialog.Close(); }, last ? 150 : 120);
            b.Margin = new Thickness(i == 0 ? 0 : 8, 0, 0, 0); if (last) b.IsDefault = true;
            bar.Children.Add(b);
        }
        stack.Children.Add(bar);
        CaptureOnShow(dialog);
        dialog.ShowDialog();
        return result;
    }

    public static void Info(Window owner, string title, string message, bool error = false) =>
        Choose(owner, title, [], message, ["Entendido"], danger: error);
}
