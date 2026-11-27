using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Ilion.Client;

namespace Ilion.Setup.App;

/// <summary>Prueba visual: --captura carpeta [--juego "Nombre"] [--paquete carpeta] [--escala 125] → PNG de cada modo y de los diálogos, y cierre.
/// Solo lee: no instala ni escribe nada en ningún juego.</summary>
public partial class MainWindow
{
    private async Task CaptureIfRequested()
    {
        var shot = Array.IndexOf(args, "--captura");
        if (shot < 0 || shot + 1 >= args.Length) return;
        var folder = args[shot + 1];
        Directory.CreateDirectory(folder);
        var gi = Array.IndexOf(args, "--juego");
        if (gi > 0 && gi + 1 < args.Length && games.FirstOrDefault(g => g.Name.Equals(args[gi + 1], StringComparison.OrdinalIgnoreCase)) is { } g)
        { SelectGame(g); updateGame = verifyGame = g; }
        var pi = Array.IndexOf(args, "--paquete");
        if (pi > 0 && pi + 1 < args.Length) engineSource = engine.InspectEngine(args[pi + 1]);
        // --setup-nuevo x.y.z: muestra la placa de «Setup disponible» como si GitHub tuviera esa versión (solo para ver el diseño)
        var si = Array.IndexOf(args, "--setup-nuevo");
        if (si > 0 && si + 1 < args.Length) { newerSetup = args[si + 1]; ShowNewerSetupBadge(); }
        ModeNew.IsChecked = true; ShowMode();
        await Snap(this, Path.Combine(folder, "1-instalacion-nueva.png"));
        Scroll.ScrollToEnd(); await Snap(this, Path.Combine(folder, "1b-instalacion-nueva-final.png"));
        ModeUpdate.IsChecked = true;
        await Snap(this, Path.Combine(folder, "2-actualizar.png"));
        ModeVerify.IsChecked = true;
        if (verifyGame is not null) { var i = Info(verifyGame); verifyItems = engine.Verify(verifyGame, i.ExeDir, i.Opti); ShowMode(); }
        await Snap(this, Path.Combine(folder, "3-verificar.png"));
        // Ventana de progreso con datos de ejemplo (solo para ver el diseño)
        var demo = InstallProgress.Open(this, 3, () => true);
        demo.Update(new ModProgress("Base congelada", "Descargando", 0.41, 23_400_000, 56_951_645, 0, 3, "Descargando la base oficial"));
        await Task.Delay(600); await Snap(demo.View, Path.Combine(folder, "4a-progreso.png"));
        demo.Finish(new BatchResult("demo", ["OptiScaler ILION 1.4.1.2 beta y la base congelada (52 archivos)", "XeMFG XMX", "HelixSR 1.9"], [], [], [], []));
        await Task.Delay(600); await Snap(demo.View, Path.Combine(folder, "4b-progreso-final.png"));
        demo.ForceClose();
        Close();
    }

    private static async Task Snap(Window window, string file)
    {
        await Task.Delay(700);
        window.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(window);
        var bitmap = new RenderTargetBitmap((int)(window.ActualWidth * dpi.DpiScaleX), (int)(window.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(file);
        encoder.Save(stream);
    }
}
