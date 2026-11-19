// ILION Setup ⇄ ILION Central. Setup es el único que escribe en las carpetas de los juegos; Central lo abre con una orden
// y lee el estado que Setup publica en su carpeta de datos (estado-juegos.json). Así no hay dos instaladores que se pisen.
//   ILION.Setup.exe --abrir nueva|actualizar|verificar|mods --ruta-juego "<carpeta del juego o su .exe>"
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using Ilion.Client;
using Level = Ilion.Setup.Level;

namespace Ilion.Setup.App;

/// <summary>Estado de un juego tal como lo ve Setup (lo lee ILION Central).</summary>
public sealed record GameStatus(
    [property: JsonPropertyName("nombre")] string Name,
    [property: JsonPropertyName("plataforma")] string Platform,
    [property: JsonPropertyName("raiz")] string Root,
    [property: JsonPropertyName("carpeta")] string ExeDir,
    [property: JsonPropertyName("ilion")] string? Ilion,
    [property: JsonPropertyName("aprobada")] bool Approved,
    [property: JsonPropertyName("gestionadoPorSetup")] bool Managed,
    [property: JsonPropertyName("mods")] string[] Mods,
    [property: JsonPropertyName("variantes")] bool Variants,
    [property: JsonPropertyName("dlss4Local")] bool Dlss4,
    [property: JsonPropertyName("verificacion")] VerifySummary? Verification,
    [property: JsonPropertyName("modsDetalle")] ModStatus[]? ModDetails = null,
    [property: JsonPropertyName("api")] string? Api = null);

/// <summary>Un mod instalado: versión que tiene el juego (null = desconocida) y la aprobada ahora.</summary>
public sealed record ModStatus(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("nombre")] string Name,
    [property: JsonPropertyName("instalada")] string? Installed,
    [property: JsonPropertyName("aprobada")] string? Approved,
    [property: JsonPropertyName("actualizacion")] bool Update);

public sealed record VerifySummary(
    [property: JsonPropertyName("fecha")] DateTime When,
    [property: JsonPropertyName("correctos")] int Ok,
    [property: JsonPropertyName("avisos")] int Warnings,
    [property: JsonPropertyName("errores")] int Errors,
    [property: JsonPropertyName("resumen")] string Summary);

public sealed record StatusFile(
    [property: JsonPropertyName("formato")] int Format,
    [property: JsonPropertyName("setup")] string SetupVersion,
    [property: JsonPropertyName("ilionAprobada")] string ApprovedIlion,
    [property: JsonPropertyName("fecha")] DateTime When,
    [property: JsonPropertyName("juegos")] GameStatus[] Games,
    [property: JsonPropertyName("setupNuevo")] string? NewerSetup = null);

public partial class MainWindow
{
    private readonly Dictionary<string, (DateTime Stamp, string? Version, bool Approved)> versionCache = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, VerifySummary> verifyMemory = new(StringComparer.OrdinalIgnoreCase);
    private string StatusPath => Path.Combine(data, "estado-juegos.json");
    private static readonly JsonSerializerOptions StatusJson = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Lee la orden de ILION Central (--abrir y --ruta-juego) y deja Setup en ese modo con ese juego.</summary>
    private void ApplyLaunchRequest()
    {
        ReadCentralOrders();
        var mi = Array.IndexOf(args, "--abrir"); var gi = Array.IndexOf(args, "--ruta-juego");
        if (mi < 0 && gi < 0) return;
        var mode = mi > 0 && mi + 1 < args.Length ? args[mi + 1].ToLowerInvariant() : "nueva";
        LibraryGame? target = null;
        if (gi > 0 && gi + 1 < args.Length)
        {
            var path = Path.GetFullPath(args[gi + 1].Trim('"'));
            static bool Inside(string root, string p) => p.Equals(root, StringComparison.OrdinalIgnoreCase) || p.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
            target = games.Where(g => Inside(g.Root, path) || Inside(path, g.Root)).OrderByDescending(g => g.Root.Length).FirstOrDefault();
            if (target is null && File.Exists(path) && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    manual.Add(path);
                    target = manual.Scan().Games.FirstOrDefault(x => x.Executable?.Equals(path, StringComparison.OrdinalIgnoreCase) == true);
                    if (target is not null) games = [.. games.Where(x => x.Id != target.Id), target];
                }
                catch (IOException) { }
            }
            if (target is null) Say("ILION Central pidió un juego que Setup no encuentra: elígelo en la lista.", Level.Warn);
            centralGameId = target?.Id;
        }
        switch (mode)
        {
            case "actualizar":
                updateGame = target ?? updateGame; ModeUpdate.IsChecked = true; break;
            case "verificar" or "mods" or "reparar":
                verifyGame = target ?? verifyGame; ModeVerify.IsChecked = true;
                if (verifyGame is not null) { var i = Info(verifyGame); if (i.Opti.Binary is not null) RunVerify(verifyGame); }
                break;
            default:
                if (target is not null) SelectGame(target);
                ModeNew.IsChecked = true; break;
        }
        if (target is not null) Say(mode switch
        {
            "actualizar" => $"Abierto desde ILION Central: actualizar ILION en {target.Name}.",
            "verificar" or "reparar" => $"Abierto desde ILION Central: verificar {target.Name}.",
            "mods" => $"Abierto desde ILION Central: añadir mods a {target.Name} (abajo, «Añadir mods»).",
            _ => $"Abierto desde ILION Central: instalar ILION en {target.Name}.",
        }, Level.Info);
    }

    /// <summary>Verifica un juego y recuerda el resultado para Central.</summary>
    private void RunVerify(LibraryGame g)
    {
        lock (gameInfo) gameInfo.Remove(SetupEngine.GameId(g));
        var i = Info(g);
        verifyItems = engine.Verify(g, i.ExeDir, i.Opti);
        var errors = verifyItems.Count(x => x.State == Level.Error); var warns = verifyItems.Count(x => x.State == Level.Warn);
        var summary = errors > 0 ? $"{errors} problema(s) por reparar" : warns > 0 ? $"{warns} aviso(s)" : "Todo correcto";
        verifyMemory[g.Root] = new(DateTime.Now, verifyItems.Count(x => x.State == Level.Ok), warns, errors, summary);
    }

    /// <summary>Publica el estado de los juegos con ILION/OptiScaler (lo lee ILION Central). Barato: solo comprueba archivos.</summary>
    private void WriteStatus()
    {
        try
        {
            if (verifyMemory.Count == 0 && File.Exists(StatusPath))
                try
                {
                    var old = JsonSerializer.Deserialize<StatusFile>(File.ReadAllText(StatusPath));
                    foreach (var g in old?.Games ?? []) if (g.Verification is { } v) verifyMemory[g.Root] = v;
                }
                catch (JsonException) { }
            var recipes = engine.Installable();
            var list = new List<GameStatus>();
            foreach (var g in games)
            {
                var info = Info(g);
                if (info.Opti.Binary is not { } bin) continue;
                string? version = null; var approved = false;
                try
                {
                    var stamp = File.GetLastWriteTimeUtc(bin);
                    if (!versionCache.TryGetValue(bin, out var c) || c.Stamp != stamp)
                    {
                        var ilion = OptiScalerInstaller.EditionOf(bin) == OptiEdition.Ilion;
                        versionCache[bin] = c = (stamp, ilion ? OptiScalerInstaller.PublicVersion(bin) ?? "versión no identificada" : null, ilion && ComponentUpdateService.Sha256(bin) == engine.Base.Engine.Sha256);
                    }
                    version = c.Version; approved = c.Approved;
                }
                catch (IOException) { }
                var installed = recipes.Where(r => ModInstaller.Installed(r, info.ExeDir)).ToArray();
                var mods = installed.Select(r => r.Name).ToArray();
                // Formato 2: versión instalada y aprobada de cada mod (Central muestra «Actualizar con ILION Setup»)
                var updates = engine.ModUpdates(g, info.ExeDir).ToDictionary(u => u.Recipe.Key);
                var id = SetupEngine.GameId(g);
                var detail = installed.Select(r =>
                {
                    var entry = engine.Catalog.Mods.FirstOrDefault(e => e.Name == r.Name);
                    var tag = updates.TryGetValue(r.Key, out var u) ? u.Installed : ModBatch.LastInstall(engine.Operations, id, r)?.Tag;
                    return new ModStatus(entry?.Id ?? r.Name, r.Name, tag, r.Tag, u is not null);
                }).ToArray();
                list.Add(new(g.Name, g.Platform, g.Root, info.ExeDir, version, approved, engine.Installer.Installed(id) is not null, mods,
                    Directory.Exists(Path.Combine(info.ExeDir, "ILION", "Escaladores", "FSR4")), SetupEngine.Dlss4Installed(info.ExeDir),
                    verifyMemory.TryGetValue(g.Root, out var v) ? v : null, detail, info.Api));
            }
            var file = new StatusFile(2, SetupEngine.SetupVersion, engine.Base.Engine.Version, DateTime.Now, [.. list], newerSetup);
            File.WriteAllText(StatusPath + ".nuevo", JsonSerializer.Serialize(file, StatusJson));
            File.Move(StatusPath + ".nuevo", StatusPath, true);
        }
        catch (Exception e) when (e is not OutOfMemoryException) { LogError("Escribir estado-juegos.json", e); } // tarea de fondo: nunca molesta al usuario
    }
}
