// ILION Setup 0.4 — actualizaciones de mods, órdenes de ILION Central (--mods, --accion) y aviso de un Setup más nuevo.
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Ilion.Client;
using Level = Ilion.Setup.Level;

namespace Ilion.Setup.App;

public partial class MainWindow
{
    private const string SetupRepository = "ENE4S/ILION-Setup";
    private string? newerSetup;                                                      // versión publicada más nueva que esta (null = al día)
    private readonly HashSet<string> centralMods = new(StringComparer.OrdinalIgnoreCase); // ids que Central pidió marcar
    private string? centralAction;                                                   // comprobar | reponer | sustituir | deshacer | actualizar
    private string? centralGameId;                                                   // juego que pidió Central (sus mods marcados valen solo para él)
    private Border? undoSection;

    // ---------------------------------------------------------------- Setup más nuevo
    /// <summary>Última publicación de ILION Setup (redirección de releases/latest, sin la API). Solo avisa; nunca se actualiza solo.</summary>
    private async Task CheckNewerSetup(HttpClient noRedirect)
    {
        var tag = await RemoteManifests.LatestTagAsync(noRedirect, SetupRepository, CancellationToken.None);
        if (tag is null || Regex.Match(tag, @"\d+(\.\d+){1,3}") is not { Success: true } m) return;
        if (VersionUtil.Compare(m.Value, SetupEngine.SetupVersion) <= 0) return;
        newerSetup = m.Value;
        await Dispatcher.InvokeAsync(() =>
        {
            BaseInfo.Inlines.Add(NewerSetupLink());
            Log($"Hay una versión más nueva de ILION Setup: {newerSetup} (esta es {SetupEngine.SetupVersion}).");
        });
    }

    private Hyperlink NewerSetupLink()
    {
        var link = new Hyperlink(new Run("  ·  ")) { Foreground = Ui.B("B.Yellow"), NavigateUri = new Uri($"https://github.com/{SetupRepository}/releases/latest"), ToolTip = "Descarga la versión nueva (un solo archivo, sin instalación)." };
        link.Inlines.Add(new Run($"ILION Setup {newerSetup} disponible"));
        link.RequestNavigate += (_, e) => { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); } catch (System.ComponentModel.Win32Exception) { } };
        return link;
    }

    // ---------------------------------------------------------------- Juego abierto
    /// <summary>¿Está el juego en marcha? (un proceso con su mismo ejecutable). Con el juego abierto sus DLL están en uso:
    /// escribir fallaría a medias y se desharía; mejor decirlo antes.</summary>
    private bool GameOpen(LibraryGame g)
    {
        var exe = Info(g).Exe;
        if (exe is null) return false;
        var name = Path.GetFileNameWithoutExtension(exe);
        foreach (var p in System.Diagnostics.Process.GetProcessesByName(name))
            using (p)
                try { if (p.MainModule?.FileName is { } path && path.Equals(exe, StringComparison.OrdinalIgnoreCase)) return true; }
                catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { return true; } // sin acceso: por prudencia, abierto
        return false;
    }

    /// <summary>True (y lo avisa) si el juego está abierto.</summary>
    private bool RefuseIfOpen(LibraryGame g)
    {
        if (!GameOpen(g)) return false;
        Confirm.Info(this, "Juego abierto", $"{g.Name} está abierto. Ciérralo y vuelve a intentarlo: con el juego en marcha sus archivos están en uso.", error: true);
        return true;
    }

    // ---------------------------------------------------------------- Actualizaciones de mods
    /// <summary>Mods instalados con una versión aprobada distinta: uno a uno o todos. Antes se dice qué cambia y que es reversible.</summary>
    private StackPanel UpdatesBox(LibraryGame g, string dir, ModUpdate[] updates)
    {
        var box = new StackPanel();
        var intro = Note("La versión aprobada para ILION cambió. Al actualizar se descarga y comprueba el paquete nuevo antes de tocar el juego, " +
            "se guarda copia de lo que se sustituye y, si el mod no queda completo, se vuelve solo a la versión anterior. Todo se puede deshacer.", null, 12.5);
        intro.Margin = new Thickness(0, 0, 0, 10); box.Children.Add(intro);
        foreach (var u in updates)
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            var go = Key("Actualizar", () => RunUpdates(g, dir, [u]), 140, primary: true);
            go.VerticalAlignment = VerticalAlignment.Top; DockPanel.SetDock(go, Dock.Right); row.Children.Add(go);
            var text = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
            var title = Ui.Text($"{u.Entry.Name}:  {u.Installed}  →  {u.Approved}", "T.Label"); title.TextWrapping = TextWrapping.Wrap; text.Children.Add(title);
            text.Children.Add(Note($"{u.Entry.Author} · {u.Entry.State} · {u.Entry.Apis}", null, 12));
            row.Children.Add(text);
            box.Children.Add(row);
        }
        if (updates.Length > 1)
        {
            var all = Key($"Actualizar todos ({updates.Length})", () => RunUpdates(g, dir, updates), 220);
            all.HorizontalAlignment = HorizontalAlignment.Left; all.Margin = new Thickness(0, 4, 0, 0); box.Children.Add(all);
        }
        return box;
    }

    private async Task RunUpdates(LibraryGame g, string dir, IReadOnlyList<ModUpdate> updates)
    {
        if (busy || updates.Count == 0 || RefuseIfOpen(g)) return;
        var lines = updates.Select(u => (u.Entry.Name, $"{u.Installed} → {u.Approved}")).ToArray();
        if (!Confirm.Ask(this, updates.Count == 1 ? "Actualizar mod" : "Actualizar mods", [("Juego", g.Name), .. lines],
            "Se descarga y comprueba cada paquete antes de tocar el juego. Lo que se sustituye o se retira queda guardado: «Deshacer cambios» devuelve la versión anterior. " +
            "Si un mod no queda completo, se vuelve solo a la versión anterior. Cierra el juego antes de seguir.", "Actualizar")) return;
        busy = true; writing = false; cts = new CancellationTokenSource();
        var token = cts.Token;
        var window = InstallProgress.Open(this, updates.Count, () => !writing);
        progressWindow = window; window.CancelRequested += () => cts.Cancel();
        var progress = new Progress<ModProgress>(p => { if (p.Stage == "Instalando") writing = true; window.Update(p); });
        var done = new List<string>(); var failed = new List<(string, string)>();
        try
        {
            var roots = games.Select(x => x.Root).Where(r => !r.Equals(g.Root, StringComparison.OrdinalIgnoreCase)).ToArray();
            foreach (var u in updates)
            {
                if (token.IsCancellationRequested) break;
                try
                {
                    var r = await Task.Run(() => engine.UpdateModAsync(g, dir, u, roots, progress, token));
                    Log($"Actualizar {u.Entry.Name} en {g.Name}: {r.Message} · retirados {r.Removed.Length} · conservados (cambiados por el usuario) {string.Join(", ", r.KeptModified)}");
                    if (r.Ok) done.Add(r.Message + (r.KeptModified.Length > 0 ? $" Se dejaron {r.KeptModified.Length} archivos de la versión anterior que habías cambiado tú." : ""));
                    else failed.Add((u.Entry.Name, r.Message));
                }
                catch (Exception e) when (e is not OutOfMemoryException) { LogError($"Actualizar {u.Entry.Name}", e); failed.Add((u.Entry.Name, ModBatch.Friendly(e))); }
            }
        }
        finally { busy = false; writing = false; }
        window.Finish(new BatchResult("", [.. done], [.. failed], [], [], [], token.IsCancellationRequested));
        lock (gameInfo) gameInfo.Remove(SetupEngine.GameId(g));
        verifyItems = null; RunVerify(g); ShowMode();
    }

    // ---------------------------------------------------------------- Órdenes de ILION Central
    /// <summary>--mods id1,id2 y --accion comprobar|reponer|sustituir|deshacer (además de --abrir y --ruta-juego).</summary>
    private void ReadCentralOrders()
    {
        var mi = Array.IndexOf(args, "--mods");
        if (mi > 0 && mi + 1 < args.Length)
            foreach (var id in args[mi + 1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) centralMods.Add(id);
        var ai = Array.IndexOf(args, "--accion");
        if (ai > 0 && ai + 1 < args.Length && args[ai + 1].ToLowerInvariant() is var a && a is "comprobar" or "reponer" or "sustituir" or "deshacer" or "actualizar") centralAction = a;
    }

    /// <summary>Lo que Central pidió: en una instalación nueva, los mods quedan marcados; en un juego con ILION, se confirma una vez y se
    /// instala; una acción de «Verificar» se ejecuta directamente (las que cambian archivos, con su confirmación de siempre).</summary>
    private async Task RunCentralOrders()
    {
        if (centralMods.Count == 0 && centralAction is null) return;
        if (ModeNew.IsChecked == true)
        {
            if (centralMods.Count > 0) Say($"ILION Central marcó {centralMods.Count} {(centralMods.Count == 1 ? "mod" : "mods")} abajo (5 · MODS ADICIONALES). Revisa y pulsa «Continuar».", Level.Info);
            return; // ModsBox los marca
        }
        if (verifyGame is not { } g) return;
        var info = Info(g);
        if (info.Opti.Binary is null)
        {
            centralMods.Clear(); centralAction = null;
            Say($"{g.Name} no tiene ILION: instálalo primero en «Instalación nueva».", Level.Warn);
            return;
        }
        if (centralMods.Count > 0)
        {
            var items = engine.Resolve(gpu?.Arch, info.Api, engine.Base.Engine.Version).Where(i => i.Recipe is not null && centralMods.Contains(i.Entry.Id)).ToArray();
            var already = items.Where(i => ModInstaller.Installed(i.Recipe!, info.ExeDir)).Select(i => i.Entry.Name).ToArray();
            var blocked = items.Where(i => i.Compat == Compat.Incompatible && !allowOtherArch).ToArray();
            var chosen = items.Where(i => !ModInstaller.Installed(i.Recipe!, info.ExeDir) && !blocked.Contains(i)).ToList();
            var unknown = centralMods.Where(id => !items.Any(i => i.Entry.Id.Equals(id, StringComparison.OrdinalIgnoreCase))).ToArray();
            centralMods.Clear();
            if (chosen.Count == 0)
            {
                Confirm.Info(this, "Mods desde ILION Central", string.Join("\n", new[]
                {
                    already.Length > 0 ? $"Ya están instalados: {string.Join(", ", already)}." : null,
                    blocked.Length > 0 ? $"No compatibles con este juego o tu GPU: {string.Join(", ", blocked.Select(b => $"{b.Entry.Name} ({b.Why})"))}." : null,
                    unknown.Length > 0 ? $"Este Setup no los conoce: {string.Join(", ", unknown)}." : null,
                }.OfType<string>().DefaultIfEmpty("No hay nada que instalar.")));
                return;
            }
            if (Requests(chosen, info.ExeDir, null) is not { Count: > 0 } requests) return;
            var notes = new List<string> { "Mods elegidos en ILION Central. Se descargan y comprueban antes de tocar el juego; todo se puede deshacer." };
            foreach (var issue in ModBatch.Analyze(requests, info.ExeDir)) notes.Add((issue.Blocking ? "No se instala " : "") + issue.Mod + ": " + issue.Message);
            if (already.Length > 0) notes.Add($"Ya instalados (se dejan como están): {string.Join(", ", already)}.");
            if (blocked.Length > 0) notes.Add($"No compatibles (no se instalan): {string.Join(", ", blocked.Select(b => b.Entry.Name))}.");
            if (NrWarning(requests.Select(r => r.Mod.Name)) is { } nr) notes.Add(nr);
            if (!Confirm.Ask(this, "Instalar mods de ILION Central", [("Juego", g.Name), ("Mods", string.Join(", ", requests.Select(r => r.Mod.Name)))], string.Join("\n\n", notes), "Instalar"))
            { Say("Instalación cancelada.", Level.Info); return; }
            await RunMods(g, info.ExeDir, requests);
            verifyItems = null; RunVerify(g); ShowMode();
            return;
        }
        var action = centralAction; centralAction = null;
        if (verifyItems is null) { RunVerify(g); ShowMode(); }
        switch (action)
        {
            case "reponer":
                if (verifyItems!.Any(i => i.Area == "Base" && i.Detail == "Falta.")) await RepairBase(g, info.ExeDir, false);
                else Say("No falta nada de la base: no hay nada que reponer.", Level.Ok);
                break;
            case "sustituir":
                if (verifyItems!.Any(i => i.Area == "Base" && i.State == Level.Warn)) await RepairBase(g, info.ExeDir, true);
                else Say("Todos los archivos de la base son los congelados: no hay nada que sustituir.", Level.Ok);
                break;
            case "actualizar":
                var updates = engine.ModUpdates(g, info.ExeDir);
                if (updates.Length > 0) await RunUpdates(g, info.ExeDir, updates); else Say("Los mods de este juego ya tienen la versión aprobada.", Level.Ok);
                break;
            case "deshacer":
                _ = Dispatcher.BeginInvoke(() => undoSection?.BringIntoView(), System.Windows.Threading.DispatcherPriority.Loaded);
                Say("Abajo, «Deshacer cambios»: elige qué deshacer.", Level.Info);
                break;
            default: Say($"Comprobado {g.Name}: el resultado está abajo.", Level.Info); break;
        }
    }
}
