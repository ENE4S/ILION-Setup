using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Ilion.Client;

namespace Ilion.Setup.App;

/// <summary>Ventana de progreso de la instalación de componentes, con la estética de CENTRAL. No es modal: se puede seguir usando
/// CENTRAL (cambiar de pestaña) mientras instala, ocultarla y volver a verla. Nada de trazas en la vista principal: el detalle técnico
/// queda en «Detalles». El porcentaje solo aparece cuando es real; si no se conoce, la barra muestra actividad.</summary>
internal sealed class InstallProgress
{
    private readonly Window dialog;
    private readonly TextBlock counter, modName, message, percent, size;
    private readonly Border stagePill, track, fill;
    private readonly TranslateTransform slide = new();
    private readonly StackPanel results;
    private readonly TextBox details;
    private readonly Button cancel, hide, close, detailsToggle;
    private readonly Func<bool> canCancel;
    private bool running = true, indeterminate;
    private double fraction;

    public event Action? CancelRequested;
    public event Action? Retry;
    public bool IsRunning => running;
    private readonly FrameworkElement spinner;

    private InstallProgress(Window owner, int count, Func<bool> canCancel)
    {
        this.canCancel = canCancel;
        var (window, stack) = Confirm.Shell(owner, "Instalación de componentes", false, 620);
        dialog = window;
        dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        counter = Ui.Text($"Preparando {count} {(count == 1 ? "componente" : "componentes")}…", "T.Note"); counter.Margin = new Thickness(0, 0, 0, 6);
        stack.Children.Add(counter);
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        stagePill = Ui.Pill("Preparando", Tone.Accent); stagePill.Margin = new Thickness(12, 0, 0, 0);
        DockPanel.SetDock(stagePill, Dock.Right); head.Children.Add(stagePill);
        spinner = Sputnik.Build(42); spinner.Margin = new Thickness(0, 0, 12, 0);
        DockPanel.SetDock(spinner, Dock.Left); head.Children.Add(spinner); Sputnik.Spin(spinner, true);
        modName = new TextBlock { FontSize = 18, Foreground = Ui.B("B.Text"), FontFamily = (FontFamily)Application.Current.FindResource("F.Strong"), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        head.Children.Add(modName);
        stack.Children.Add(head);

        // Barra propia: pista con el trazo de CENTRAL y relleno de acento (o un tramo que se desplaza si el progreso no se conoce)
        fill = new Border { Background = Ui.B("B.Accent"), CornerRadius = new CornerRadius(4), HorizontalAlignment = HorizontalAlignment.Left, RenderTransform = slide };
        track = new Border { Height = 10, CornerRadius = new CornerRadius(5), Background = Ui.B("B.Bg"), BorderBrush = Ui.B("B.Line"), BorderThickness = new Thickness(1), ClipToBounds = true, Child = fill };
        track.SizeChanged += (_, _) => Layout();
        stack.Children.Add(track);
        var numbers = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        size = new TextBlock { FontSize = 12.5, Foreground = Ui.B("B.TextDim") }; DockPanel.SetDock(size, Dock.Right); numbers.Children.Add(size);
        percent = new TextBlock { FontSize = 12.5, Foreground = Ui.B("B.Accent"), FontFamily = (FontFamily)Application.Current.FindResource("F.Strong") };
        numbers.Children.Add(percent);
        stack.Children.Add(numbers);
        message = new TextBlock { FontSize = 13.5, Foreground = Ui.B("B.Text"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), MinHeight = 20 };
        stack.Children.Add(message);

        results = new StackPanel { Margin = new Thickness(0, 12, 0, 0), Visibility = Visibility.Collapsed };
        stack.Children.Add(results);

        details = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Height = 130, Margin = new Thickness(0, 8, 0, 0),
            FontFamily = (FontFamily)Application.Current.FindResource("F.Mono"), FontSize = 11.5, Background = Ui.B("B.Bg"), Foreground = Ui.B("B.TextDim"),
            BorderBrush = Ui.B("B.Line"), Padding = new Thickness(8, 6, 8, 6), Visibility = Visibility.Collapsed };

        var bar = new DockPanel { Margin = new Thickness(0, 18, 0, 0), LastChildFill = false };
        detailsToggle = Ui.Button("Ver detalles", "Btn", (_, _) => ToggleDetails(), 120);
        DockPanel.SetDock(detailsToggle, Dock.Left); bar.Children.Add(detailsToggle);
        close = Ui.Button("Cerrar", "Btn.Primary", (_, _) => dialog.Hide(), 140); close.Visibility = Visibility.Collapsed; close.Margin = new Thickness(8, 0, 0, 0);
        cancel = Ui.Button("Cancelar", "Btn", (_, _) => RequestCancel(), 120); cancel.Margin = new Thickness(8, 0, 0, 0);
        hide = Ui.Button("Ocultar", "Btn", (_, _) => dialog.Hide(), 120);
        Ui.Tip(hide, "La instalación sigue. Vuelve a verla con el enlace de la barra inferior.");
        foreach (var b in new[] { close, cancel, hide }) { DockPanel.SetDock(b, Dock.Right); bar.Children.Add(b); }
        stack.Children.Add(bar);
        stack.Children.Add(details);

        // Esc o cerrar mientras trabaja = ocultar (la operación sigue); terminada, se cierra de verdad
        dialog.Closing += (_, e) => { if (running) { e.Cancel = true; dialog.Hide(); } };
        SetIndeterminate(true);
    }

    public static InstallProgress Open(Window owner, int count, Func<bool> canCancel)
    {
        var p = new InstallProgress(owner, count, canCancel);
        p.dialog.Show();
        return p;
    }

    public void Reveal() { if (!dialog.IsVisible) dialog.Show(); dialog.Activate(); }

    /// <summary>Solo para la prueba visual (capturas): la ventana y su cierre.</summary>
    internal Window View => dialog;
    internal void ForceClose() { running = false; dialog.Close(); }

    public void Update(ModProgress p)
    {
        if (p.Mod.Length > 0) modName.Text = p.Mod;
        counter.Text = p.Count == 0 ? "" : $"Componente {Math.Min(p.Completed + 1, p.Count)} de {p.Count} · {p.Completed} {(p.Completed == 1 ? "terminado" : "terminados")}";
        ((TextBlock)stagePill.Child).Text = p.Stage;
        message.Text = p.Message;
        if (p.Fraction is { } f) { SetIndeterminate(false); fraction = Math.Clamp(f, 0, 1); Layout(); percent.Text = $"{fraction * 100:0} %"; }
        else { SetIndeterminate(true); percent.Text = ""; }
        size.Text = p.Done is { } done ? (p.Total is { } total and > 0 ? $"{Mb(done)} de {Mb(total)}" : Mb(done)) : "";
        if (p.Detail is { } d) Log(d);
        else Log($"{p.Mod} · {p.Stage} · {p.Message}");
        cancel.IsEnabled = running && canCancel();
        Ui.Tip(cancel, cancel.IsEnabled ? "Detiene la instalación sin escribir nada en el juego." : "Se están copiando los archivos: no se puede cancelar a medias.");
    }

    /// <summary>Fin: resumen con lo instalado, lo que falló (con «Reintentar») y lo que queda pendiente.</summary>
    public void Finish(BatchResult result)
    {
        running = false;
        Sputnik.Spin(spinner, false);
        SetIndeterminate(false);
        var ok = result.Failed.Length == 0 && !result.Cancelled;
        fraction = result.Installed.Length > 0 || ok ? 1 : 0; Layout();
        percent.Text = ""; size.Text = "";
        modName.Text = result.Cancelled ? "Instalación cancelada" : ok ? "Instalación terminada" : result.Installed.Length > 0 ? "Instalación terminada con avisos" : "No se pudo instalar";
        ((TextBlock)stagePill.Child).Text = result.Cancelled ? "Cancelada" : ok ? "Completada" : "Revisar";
        stagePill.BorderBrush = Ui.ToneBrush(ok ? Tone.Ok : Tone.Warn); ((TextBlock)stagePill.Child).Foreground = Ui.ToneBrush(ok ? Tone.Ok : Tone.Warn);
        counter.Text = $"{result.Installed.Length} {(result.Installed.Length == 1 ? "componente instalado" : "componentes instalados")}";
        message.Text = result.Cancelled ? "No se ha escrito nada en el juego." : ok ? "Todo se instaló y se puede restaurar desde «Verificar y reparar»." : "Lo instalado se puede restaurar desde «Verificar y reparar». Revisa lo que no se instaló:";
        results.Children.Clear();
        foreach (var name in result.Installed) results.Children.Add(Line("✓ " + name, Tone.Ok));
        foreach (var (mod, error) in result.Failed) results.Children.Add(Line($"✗ {mod}: {error}", Tone.Error));
        foreach (var pending in result.Pending) results.Children.Add(Line((pending.Dependency?.WarnOnly == true ? "! " : "⚠ ") + DependencyResolver.Advice(pending), Tone.Warn));
        results.Visibility = results.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        cancel.Visibility = Visibility.Collapsed; hide.Visibility = Visibility.Collapsed; close.Visibility = Visibility.Visible; close.IsDefault = true;
        if (result.Failed.Length > 0 && !result.Cancelled)
        {
            var retry = Ui.Button("Reintentar", "Btn", (_, _) => { dialog.Close(); Retry?.Invoke(); }, 120);
            retry.Margin = new Thickness(8, 0, 0, 0); DockPanel.SetDock(retry, Dock.Right);
            ((DockPanel)close.Parent).Children.Insert(((DockPanel)close.Parent).Children.IndexOf(close) + 1, retry);
        }
        close.Click += (_, _) => dialog.Close();
        Reveal();
    }

    private static TextBlock Line(string text, Tone tone) => new() { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 13, Foreground = Ui.ToneBrush(tone), Margin = new Thickness(0, 0, 0, 5) };

    private void RequestCancel()
    {
        if (!canCancel()) return;
        cancel.IsEnabled = false; message.Text = "Cancelando… no se escribirá nada en el juego.";
        CancelRequested?.Invoke();
    }

    private void ToggleDetails()
    {
        var show = details.Visibility != Visibility.Visible;
        details.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        detailsToggle.Content = show ? "Ocultar detalles" : "Ver detalles";
    }

    private void Log(string line)
    {
        if (details.LineCount > 400) details.Clear();
        details.AppendText($"{DateTime.Now:HH:mm:ss}  {line}\n");
        details.ScrollToEnd();
    }

    private void SetIndeterminate(bool on)
    {
        if (on == indeterminate) return;
        indeterminate = on;
        slide.BeginAnimation(TranslateTransform.XProperty, null);
        slide.X = 0;
        Layout();
    }

    private void Layout()
    {
        var width = Math.Max(0, track.ActualWidth - 2);
        if (!indeterminate) { slide.BeginAnimation(TranslateTransform.XProperty, null); slide.X = 0; fill.Width = width * fraction; return; }
        fill.Width = width * 0.28;
        if (width <= 0) return;
        slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-width * 0.28, width, TimeSpan.FromSeconds(1.4)) { RepeatBehavior = RepeatBehavior.Forever });
    }

    private static string Mb(long bytes) => bytes >= 1024 * 1024 ? $"{bytes / 1048576.0:0.0} MB" : $"{bytes / 1024.0:0} KB";
}
