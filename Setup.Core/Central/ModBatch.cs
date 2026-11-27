// ILION CENTRAL-01 — Instalación coordinada de uno o varios mods en un juego.
// Fase 1 (cancelable, sin escribir nada en el juego): descargar con su huella, verificar, construir (HelixSR), planificar y resolver los
// archivos oficiales que necesitan. Fase 2 (sin cancelación, corta): escribir. Primero una operación con las dependencias compartidas
// (una sola copia aunque las pidan varios mods) y después una operación por mod, todas con el mismo identificador de lote. Si una
// escritura falla, se deshacen las ya hechas de ese lote: el juego queda como estaba.
// Restaurar: un mod suelto (su operación) o el lote entero. Las dependencias compartidas solo se retiran si ningún mod instalado las usa.
using System.Text.RegularExpressions;

namespace Ilion.Client;

/// <summary>Estado de la instalación para la ventana de progreso. Fraction/Done/Total son null cuando no se conocen (no se inventan).</summary>
public sealed record ModProgress(string Mod, string Stage, double? Fraction, long? Done, long? Total, int Completed, int Count, string Message, string? Detail = null);

/// <summary>Un mod pedido, con el nombre de carga elegido si es un mod proxy. Updating = sustituye una versión anterior del mismo mod
/// (un mod proxy conserva su nombre de carga aunque ya exista: es el suyo).</summary>
public sealed record BatchRequest(InstallableMod Mod, string? Proxy = null, bool Updating = false);

/// <summary>Algo que comprobar antes de instalar. Blocking = ese mod no se instala.</summary>
public sealed record BatchIssue(string Mod, string Message, bool Blocking);

public sealed record BatchResult(string BatchId, string[] Installed, (string Mod, string Error)[] Failed, RequirementState[] Pending,
    OperationManifest[] Operations, string[] Sources, bool Cancelled = false);

public static class ModBatch
{
    public const string Kind = "Instalar mod";
    public const string DependencyKind = "Instalar dependencias";
    /// <summary>Al actualizar un mod: retirada (restaurable) de lo que solo tenía la versión anterior. Va en el mismo lote que la versión nueva.</summary>
    public const string RetireKind = "Retirar versión anterior";

    /// <summary>Comprobaciones antes de descargar nada: repetidos, mods incompatibles ya presentes, nombres de carga repetidos e
    /// implementaciones que ILION usa de una en una.</summary>
    public static BatchIssue[] Analyze(IReadOnlyList<BatchRequest> items, string exeDir)
    {
        var issues = new List<BatchIssue>();
        foreach (var dup in items.GroupBy(i => i.Mod.Key).Where(g => g.Count() > 1))
            issues.Add(new(dup.First().Mod.Name, "Está elegido dos veces: se instala una sola.", false));
        foreach (var item in items.DistinctBy(i => i.Mod.Key))
            if (ModInstaller.ConflictsIn(item.Mod, exeDir) is { Length: > 0 } clash)
                issues.Add(new(item.Mod.Name, $"El juego ya tiene {string.Join(", ", clash)}, que no funciona junto a {item.Mod.Name}. Quítalo primero.", true));
        foreach (var proxy in items.Where(i => i.Proxy is not null).GroupBy(i => i.Proxy!, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            foreach (var item in proxy.Skip(1))
                issues.Add(new(item.Mod.Name, $"Usa el mismo nombre de carga ({proxy.Key}) que {proxy.First().Mod.Name}.", true));
        foreach (var slot in items.DistinctBy(i => i.Mod.Key).Where(i => i.Mod.Slot is not null).GroupBy(i => i.Mod.Slot!).Where(g => g.Count() > 1))
            issues.Add(new(string.Join(", ", slot.Select(i => i.Mod.Short ?? i.Mod.Name)),
                $"{slot.Key}: pueden estar instalados a la vez, pero ILION usa uno solo. Elígelo después en OptiScaler.", false));
        return [.. issues];
    }

    public static async Task<BatchResult> RunAsync(ModInstaller installer, OperationEngine operations, ComponentUpdateService? updates, string gameId, string exeDir,
        IReadOnlyList<BatchRequest> requests, IEnumerable<string> gameRoots, IProgress<ModProgress>? progress, CancellationToken token)
    {
        var items = requests.DistinctBy(r => r.Mod.Key).Where(r => !Analyze(requests, exeDir).Any(i => i.Blocking && i.Mod == r.Mod.Name)).ToArray();
        var failed = new List<(string, string)>();
        foreach (var blocked in Analyze(requests, exeDir).Where(i => i.Blocking)) failed.Add((blocked.Mod, blocked.Message));
        var prepared = new List<(BatchRequest Request, ModDownload Download, ModPlanItem[] Plan)>();
        var count = items.Length;
        void Report(string mod, string stage, double? fraction, string message, long? done = null, long? total = null, string? detail = null) =>
            progress?.Report(new(mod, stage, fraction, done, total, prepared.Count, count, message, detail));
        var cancelled = false;

        // ---- Fase 1: preparar (nada se escribe en el juego)
        try
        {
            foreach (var request in items)
            {
                token.ThrowIfCancellationRequested();
                var mod = request.Mod;
                try
                {
                    Report(mod.Name, "Descargando", null, "Consultando la publicación oficial…");
                    var bytes = new Progress<(long Done, long? Total)>(b =>
                        Report(mod.Name, "Descargando", b.Total is > 0 ? (double)b.Done / b.Total.Value : null, "Descargando el paquete oficial", b.Done, b.Total));
                    var download = await installer.DownloadAsync(mod, token, bytes);
                    Report(mod.Name, "Verificando", null, "Huella SHA-256 publicada por GitHub: coincide");
                    if (mod.Build is not null)
                    {
                        var build = new Progress<string>(line => Report(mod.Name, line.Contains("Descargando") ? "Descargando" : "Compilando",
                            BuildFraction(line), line.StartsWith("HelixSR:") ? "Construyendo con el setup de su autor" : line, detail: line));
                        Report(mod.Name, "Compilando", null, "Construyendo con el setup de su autor (varios minutos la primera vez)");
                        download = await Task.Run(() => installer.PrepareAsync(mod, download, build, token), token);
                    }
                    Report(mod.Name, "Preparando", null, "Comprobando qué archivos van y dónde");
                    var plan = ModInstaller.Plan(mod, download, exeDir, request.Proxy, request.Updating);
                    prepared.Add((request, download, plan));
                }
                catch (Exception error) when (error is not OperationCanceledException && IsExpected(error))
                {
                    failed.Add((mod.Name, Friendly(error)));
                }
            }

            // Dos mods que quieren escribir el mismo archivo con contenido distinto: el segundo no se instala
            var owners = new Dictionary<string, (string Mod, string Sha)>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in prepared.ToArray())
            {
                var clash = p.Plan.Select(i => (i.Relative, Sha: ComponentUpdateService.Sha256(i.Source)))
                    .FirstOrDefault(i => owners.TryGetValue(i.Relative, out var o) && o.Sha != i.Sha);
                if (clash.Relative is not null)
                {
                    prepared.Remove(p);
                    failed.Add((p.Request.Mod.Name, $"Escribiría {clash.Relative}, que también instala {owners[clash.Relative].Mod} con otro contenido."));
                    continue;
                }
                foreach (var i in p.Plan) owners.TryAdd(i.Relative, (p.Request.Mod.Name, ComponentUpdateService.Sha256(i.Source)));
            }

            // Archivos oficiales: una sola copia por destino, aunque los pidan varios mods
            Report("Archivos oficiales", "Verificando", null, "Comprobando los archivos de NVIDIA, Intel y AMD que necesitan los mods");
            var depItems = new Dictionary<string, ModPlanItem>(StringComparer.OrdinalIgnoreCase);
            var sources = new List<string>(); var pending = new List<RequirementState>();
            foreach (var p in prepared)
            {
                var deps = await DependencyResolver.PlanAsync(p.Request.Mod, exeDir, updates, gameRoots, token);
                foreach (var d in deps.Items) if (depItems.TryAdd(d.Relative, d)) sources.Add(deps.Sources[Array.IndexOf(deps.Items, d)]);
                pending.AddRange(deps.Pending.Where(x => !pending.Any(y => y.Relative.Equals(x.Relative, StringComparison.OrdinalIgnoreCase))));
            }
            foreach (var d in depItems.Keys.ToArray())
                if (owners.ContainsKey(d)) depItems.Remove(d); // un mod ya lo trae

            token.ThrowIfCancellationRequested();

            // ---- Fase 2: escribir (corta; no se cancela a medias)
            var batch = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..4];
            var applied = new List<OperationManifest>();
            var installed = new List<string>();
            try
            {
                if (depItems.Count > 0)
                {
                    Report("Archivos oficiales", "Instalando", null, $"Colocando {depItems.Count} archivos oficiales");
                    var writes = depItems.Values.Select(i => new PlannedWrite(i.Relative, i.Source, Current(exeDir, i.Relative), ComponentUpdateService.VersionOf(i.Source))).ToList();
                    applied.Add(operations.Apply(gameId, exeDir, DependencyKind, "Archivos oficiales: " + string.Join(", ", depItems.Keys.Select(Path.GetFileName).Distinct()),
                        writes, "oficial", "", batch));
                }
                foreach (var p in prepared)
                {
                    Report(p.Request.Mod.Name, "Instalando", null, $"Copiando {p.Plan.Length} archivos al juego");
                    applied.Add(installer.Install(gameId, exeDir, p.Request.Mod, p.Download, p.Request.Proxy, null, batch, p.Request.Updating));
                    installed.Add(p.Request.Mod.Name);
                    progress?.Report(new(p.Request.Mod.Name, "Finalizando", 1, null, null, installed.Count, count, "Instalado"));
                }
            }
            catch (Exception error) when (IsExpected(error))
            {
                // Deshacer lo ya escrito de este lote, en orden inverso
                foreach (var op in Enumerable.Reverse(applied))
                    try { operations.Restore(op); } catch (Exception undo) when (IsExpected(undo)) { failed.Add((op.Label, "No se pudo deshacer: " + undo.Message)); }
                failed.Add(("Instalación", "Falló al escribir y se deshizo todo el lote: " + Friendly(error)));
                return new(batch, [], [.. failed], [.. pending], [], [.. sources]);
            }
            Report("", "Finalizando", 1, "Instalación terminada");
            return new(batch, [.. installed], [.. failed], [.. pending], [.. applied], [.. sources]);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
            return new("", [], [.. failed], [], [], [], cancelled);
        }
    }

    private static string? Current(string exeDir, string relative)
    {
        var path = Path.Combine(exeDir, relative);
        return File.Exists(path) ? ComponentUpdateService.Sha256(path) : null;
    }

    /// <summary>Progreso real que imprime el setup de HelixSR («… 80% download 45/56 MB»); null si la línea no lo dice.</summary>
    public static double? BuildFraction(string line)
    {
        var m = Regex.Match(line, @"(?<!\d)(\d{1,3})\s?%");
        if (m.Success && int.Parse(m.Groups[1].Value) is >= 0 and <= 100 and var pct) return pct / 100.0;
        m = Regex.Match(line, @"(\d+(?:\.\d+)?)\s*/\s*(\d+(?:\.\d+)?)\s*MB", RegexOptions.IgnoreCase);
        if (m.Success && double.TryParse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture, out var total) && total > 0
            && double.TryParse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out var done)) return Math.Clamp(done / total, 0, 1);
        return null;
    }

    private static bool IsExpected(Exception e) => e is HttpRequestException or IOException or InvalidDataException or InvalidOperationException
        or UnauthorizedAccessException or TaskCanceledException or System.Text.Json.JsonException;

    /// <summary>Mensaje corto para la persona (sin trazas).</summary>
    public static string Friendly(Exception e) => e switch
    {
        HttpRequestException => "No se pudo conectar con GitHub. Comprueba la conexión y vuelve a intentarlo.",
        TaskCanceledException => "La descarga tardó demasiado. Vuelve a intentarlo.",
        UnauthorizedAccessException => "Windows no permite escribir en la carpeta del juego (¿está abierto o es de solo lectura?).",
        _ => e.Message.Split('\n')[0],
    };

    // ---- Registro y restauración

    /// <summary>Última instalación completada de una receta en este juego (su versión está en Tag).</summary>
    public static OperationManifest? LastInstall(OperationEngine operations, string gameId, InstallableMod mod) =>
        operations.All(gameId).Where(o => o.Completed && o.Kind == Kind && (o.Label == mod.Name || o.Label.StartsWith(mod.Name + " "))).OrderByDescending(o => o.When).FirstOrDefault();

    /// <summary>Operaciones de un lote que siguen activas (más reciente primero).</summary>
    public static OperationManifest[] Batch(OperationEngine operations, string gameId, string batch) =>
        [.. operations.All(gameId).Where(o => o.Completed && o.Batch == batch).OrderByDescending(o => o.When)];

    /// <summary>Restaura un lote entero, del último al primero. Si algo no se puede restaurar, se detiene y lo dice.</summary>
    public static string[] RestoreBatch(OperationEngine operations, string gameId, string batch)
    {
        var done = new List<string>();
        foreach (var op in Batch(operations, gameId, batch))
        {
            if (operations.CanRestore(op) is { } why) throw new InvalidOperationException($"{op.Label}: {why}");
            operations.Restore(op);
            PruneEmptyFolders(op);
            done.Add(op.Label);
        }
        return [.. done];
    }

    /// <summary>Mods instalados (distintos de «except») que todavía usan algún archivo de una operación de dependencias.</summary>
    public static string[] StillNeeded(OperationManifest dependencies, string exeDir, IEnumerable<InstallableMod> except)
    {
        var skip = except.Select(m => m.Key).ToHashSet();
        return [.. ModInstaller.Known.Where(m => !skip.Contains(m.Key) && ModInstaller.Installed(m, exeDir)
            && m.Dependencies.Any(d => dependencies.Touches(d.Relative) || d.AlsoAccept.Any(dependencies.Touches))).Select(m => m.Name)];
    }

    /// <summary>Restaura un mod suelto y, si nadie más los usa, los archivos oficiales que se pusieron para él en su mismo lote.
    /// Después retira las carpetas que quedaron vacías al quitar sus archivos (nunca las de ILION u OptiScaler).</summary>
    public static string[] RestoreMod(OperationEngine operations, string gameId, string exeDir, OperationManifest modOp, InstallableMod mod)
    {
        if (operations.CanRestore(modOp) is { } why) throw new InvalidOperationException(why);
        operations.Restore(modOp);
        var done = new List<string> { modOp.Label };
        var restored = new List<OperationManifest> { modOp };
        if (modOp.Batch is { } batch)
            foreach (var deps in Batch(operations, gameId, batch).Where(o => o.Kind == DependencyKind))
                if (StillNeeded(deps, exeDir, [mod]).Length == 0 && operations.CanRestore(deps) is null) { operations.Restore(deps); done.Add(deps.Label); restored.Add(deps); }
        foreach (var op in restored) PruneEmptyFolders(op);
        return [.. done];
    }

    /// <summary>La instalación de Setup que puso este mod en el juego: la más reciente que escribió su archivo marcador. Null si Setup no lo instaló
    /// (vino con el paquete de ILION o lo puso el usuario). Por el marcador y no por el nombre: «MFG-Unlocker» no confunde sus versiones.</summary>
    public static OperationManifest? InstallOf(OperationEngine operations, string gameId, InstallableMod mod) =>
        mod.Marker.Length == 0 || mod.Marker.Contains("{any}") ? null
        : operations.All(gameId).Where(o => o.Completed && o.Kind == Kind && o.Touches(mod.Marker)).OrderByDescending(o => o.When).FirstOrDefault();

    /// <summary>Carpetas de ILION y OptiScaler que nunca se retiran, aunque queden vacías.</summary>
    private static readonly HashSet<string> KeptFolders = new(StringComparer.OrdinalIgnoreCase)
        { "OptiScaler", @"OptiScaler\plugins", @"OptiScaler\custom", @"OptiScaler\plugins\XeMFG", "ILION", @"ILION\Escaladores", @"ILION\Escaladores\FSR4" };

    /// <summary>Tras restaurar una operación: retira las carpetas que contenían archivos nuevos de esa operación y han quedado vacías,
    /// de la más profunda hacia arriba. Solo carpetas sin nada dentro y bajo la carpeta del juego. Devuelve las retiradas.</summary>
    public static string[] PruneEmptyFolders(OperationManifest op)
    {
        var root = Path.GetFullPath(op.GameRoot).TrimEnd('\\') + "\\";
        var removed = new List<string>();
        var folders = op.Entries.Where(e => e.OriginalSha256 is null).Select(e => Path.GetDirectoryName(e.RelativePath.Replace('/', '\\')) ?? "")
            .Where(d => d.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(d => d.Count(c => c == '\\'));
        foreach (var start in folders)
            for (var rel = start; rel.Length > 0 && !KeptFolders.Contains(rel); rel = Path.GetDirectoryName(rel) ?? "")
            {
                var full = Path.GetFullPath(Path.Combine(root, rel));
                if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(full)) break;
                try
                {
                    if (Directory.EnumerateFileSystemEntries(full).Any()) break;
                    Directory.Delete(full);
                    removed.Add(rel);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { break; }
            }
        return [.. removed];
    }
}
