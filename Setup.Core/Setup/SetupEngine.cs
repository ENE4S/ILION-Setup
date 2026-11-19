// ILION Setup — los tres modos sobre las piezas verificadas de ILION CENTRAL-01:
//   Instalación nueva : base congelada + motor ILION + mods elegidos, en dos fases (preparar sin tocar el juego, después escribir).
//   Actualizar ILION  : sustituye solo la DLL del motor si la base instalada es la congelada; si no, explica qué falta.
//   Verificar/reparar : base, motor, mods y conflictos; repara lo que falta sin tocar archivos personales sin permiso.
using System.Security.Cryptography;
using System.Text;
using Ilion.Client;

namespace Ilion.Setup;

public enum Compat { Compatible, Warning, Incompatible, Informative }

/// <summary>Un mod del catálogo ya resuelto para un juego y una GPU.</summary>
public sealed record CatalogItem(CatalogEntry Entry, InstallableMod? Recipe, Compat Compat, string? Why, bool Preselect);

/// <summary>El motor ILION elegido: la publicación oficial (Remote, se descarga al instalar), un paquete completo local o una DLL suelta.</summary>
public sealed record EngineSource(string? PackageFolder, string Dll, string Version, bool Approved, string? Problem, bool Remote = false);

public sealed record NewInstallResult(OptiInstallManifest? Engine, BatchResult? Mods, string[] BaseSources, string? Error, bool Cancelled);

/// <summary>Un mod instalado por Setup cuya versión (la de su registro) no es la aprobada ahora en el catálogo.</summary>
public sealed record ModUpdate(CatalogEntry Entry, InstallableMod Recipe, string Installed, string Approved, OperationManifest Last);

/// <summary>Resultado de actualizar un mod: lo que cambió, lo que se retiró de la versión anterior y lo que se dejó por haberlo cambiado el usuario.</summary>
public sealed record ModUpdateResult(bool Ok, string Message, string[] Removed, string[] KeptModified, bool RolledBack, BatchResult? Batch);

public sealed record VerifyItem(string Area, string What, Level State, string Detail, Func<Task>? Repair = null);

public enum Level { Ok, Warn, Error, Info }

public sealed class SetupEngine
{
    public string Data { get; }
    public HttpClient Http { get; }
    public BaseManifest Base { get; }
    public Catalog Catalog { get; }
    public FrozenBase Frozen { get; }
    public OperationEngine Operations { get; }
    public OptiScalerInstaller Installer { get; }
    public ComponentUpdateService Updates { get; }

    public SetupEngine(string data, HttpClient http, BaseManifest baseManifest, Catalog catalog)
    {
        Data = data; Http = http; Base = baseManifest; Catalog = catalog;
        Directory.CreateDirectory(data);
        Frozen = new FrozenBase(http, data, baseManifest);
        Operations = new OperationEngine(data);
        Installer = new OptiScalerInstaller(http, data);
        Updates = new ComponentUpdateService(http, data);
        // Lo que instala Setup como ILION y su base: ninguna receta descrita por datos puede sustituirlo
        foreach (var f in baseManifest.Files) ModInstaller.ProtectedFiles.Add(f.Destination);
        foreach (var p in OptiScalerInstaller.Proxies) ModInstaller.ProtectedFiles.Add(p);
    }

    public static string GameId(LibraryGame g) =>
        g.Platform + "_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(g.Platform + "\n" + g.Id + "\n" + g.Root)))[..12];

    // ---------------------------------------------------------------- Catálogo
    /// <summary>Versión de este Setup (la pone la aplicación al arrancar): las entradas con «setupMinimo» más alto no se ofrecen.</summary>
    public static string SetupVersion { get; set; } = "0.4.0";

    /// <summary>Por qué una entrada del catálogo no se puede instalar con esta versión de Setup (null = se puede o es solo informativa).</summary>
    public static string? Unusable(CatalogEntry e)
    {
        if (e.MinSetup is { } min && VersionUtil.Compare(SetupVersion, min) < 0) return $"Necesita ILION Setup {min} o posterior.";
        if (e.Definition is { } d) return Recipes.Validate(e, d) is { } bad ? $"Receta del catálogo no válida ({bad})." : null;
        if (e.Recipe is { } name && !ModInstaller.Known.Any(r => r.Name == name)) return "Esta versión de Setup no trae su receta: actualiza ILION Setup.";
        return null;
    }

    /// <summary>Receta con la versión aprobada fijada (etiqueta y SHA-256 del paquete): una receta integrada (por nombre) o descrita
    /// por datos en el catálogo (formato 2). Null si la entrada es informativa o esta versión de Setup no puede usarla. Nunca lanza:
    /// un catálogo más nuevo que este Setup no debe impedir que arranque (en la 0.3.x, un nombre de receta desconocido lo cerraba).</summary>
    public static InstallableMod? RecipeFor(CatalogEntry e)
    {
        if (Unusable(e) is not null) return null;
        InstallableMod recipe;
        if (e.Definition is { } data) recipe = Recipes.Build(e, data);
        else if (e.Recipe is null) return null;
        else
        {
            var known = ModInstaller.Known.First(r => r.Name == e.Recipe);
            recipe = known with { Name = e.Name, Tag = e.Version ?? known.Tag, ApprovedSha256 = e.Sha256, ApprovedAsset = e.Asset };
        }
        // Dos versiones del mismo mod (MFG-Unlocker 1.7.0 y 1.7.1) se distinguen por su carpeta de versión
        if (recipe.Marker.Contains("{any}") && e.Version is { } v) recipe = recipe with { Marker = recipe.Marker.Replace("{any}", v) };
        return recipe;
    }

    /// <summary>Recetas instalables del catálogo actual.</summary>
    public InstallableMod[] Installable() => [.. Catalog.Mods.Select(RecipeFor).OfType<InstallableMod>()];

    /// <summary>El catálogo para un juego y una GPU: compatibilidad fundada en la tabla de cada mod y la API del juego.</summary>
    public CatalogItem[] Resolve(GpuArch? arch, string? api, string? ilionVersion)
    {
        return [.. Catalog.Mods.Select(e =>
        {
            var recipe = RecipeFor(e);
            if (recipe is null) return new CatalogItem(e, null, Compat.Informative, Unusable(e), false);
            string? why = null; var compat = Compat.Compatible;
            if (arch is not null && !Gpu.Fits(e.Archs, arch)) { compat = Compat.Incompatible; why = $"No está pensado para {Gpu.BrandName(arch.Marca)} {arch.Nombre} (según su autor y la tabla de ILION)."; }
            else if (recipe.Dx12Only && api is "Vulkan" or "DirectX 11") { compat = Compat.Incompatible; why = $"Solo DirectX 12; el juego parece {api}."; }
            else if (recipe.MinIlion is { } min && ilionVersion is { } v && VersionUtil.Compare(v, min) < 0) { compat = Compat.Incompatible; why = $"Necesita ILION {min} o posterior (motor elegido: {v})."; }
            else if (arch is null) { compat = Compat.Warning; why = "No se reconoció la GPU: revisa la compatibilidad tú mismo."; }
            else if (e.State is "experimental" or "sin verificar") { compat = Compat.Warning; why = e.State == "experimental" ? "Experimental." : "Sin verificar con ILION en juego."; }
            var preselect = compat == Compat.Compatible && arch is not null && e.Recommended?.Contains(arch.Id) == true && api is not ("Vulkan" or "DirectX 11");
            return new CatalogItem(e, recipe, compat, why, preselect);
        })];
    }

    // ---------------------------------------------------------------- Motor ILION
    /// <summary>Un paquete local (carpeta con dxgi.dll) o una DLL suelta: edición ILION y si es la aprobada para esta base.</summary>
    public EngineSource InspectEngine(string path)
    {
        string dll; string? folder = null;
        if (Directory.Exists(path))
        {
            var info = IlionPackageStore.Inspect(path);
            if (info.Problem is not null || info.Binary is null) return new(path, "", "", false, info.Problem ?? "No contiene la DLL de ILION.");
            folder = path; dll = info.Binary;
        }
        else if (File.Exists(path)) dll = path;
        else return new(null, path, "", false, "No existe.");
        if (OptiScalerInstaller.EditionOf(dll) != OptiEdition.Ilion) return new(folder, dll, "", false, "No es la edición ILION de OptiScaler.");
        // La versión pública de ILION (en la serie 1.4.1.x sale de la versión de archivo: solo número y fase)
        var version = OptiScalerInstaller.PublicVersion(dll) ?? "versión no identificada";
        var approved = ComponentUpdateService.Sha256(dll) == Base.Engine.Sha256;
        return new(folder, dll, version, approved, null);
    }

    /// <summary>La publicación oficial de ILION del manifiesto (grupo «motor»): se descarga y verifica al instalar.</summary>
    public EngineSource RemoteEngine => new(null, "", Base.Engine.Version, true, Base.Files.Any(f => FrozenBase.Group(f) == "motor") ? null : "El manifiesto no tiene una publicación oficial del motor.", Remote: true);

    /// <summary>Descarga (o toma de la caché o de un paquete local idéntico) la DLL y el INI publicados y los deja en Datos\Motor.</summary>
    public async Task<EngineSource> DownloadEngineAsync(IEnumerable<string> localFolders, IProgress<ModProgress>? progress, CancellationToken token)
    {
        var folder = Path.Combine(Data, "Motor", Base.Engine.Sha256[..16]);
        var dll = Path.Combine(folder, "dxgi.dll");
        if (!File.Exists(dll) || ComponentUpdateService.Sha256(dll) != Base.Engine.Sha256)
        {
            progress?.Report(new("OptiScaler ILION", "Descargando", null, null, null, 0, 0, $"Descargando ILION {Base.Engine.Version} de su publicación oficial"));
            await Frozen.PrepareAsync(folder, new HashSet<string> { "motor" }, localFolders, progress, token, withCore: false);
        }
        return InspectEngine(dll) with { PackageFolder = folder };
    }

    /// <summary>DLSS 4 Modelo K (d4r) desde la copia del usuario: lleva DLL de NVIDIA, así que solo se instala en local. Restaurable.</summary>
    public OperationManifest InstallDlss4Local(LibraryGame game, string exeDir, string folder)
    {
        var source = FindD4r(folder) ?? throw new InvalidDataException("En esa carpeta no está DLSS 4 Modelo K (falta d4r.ini). Elige la carpeta Modelo_K de tu copia de ILION › Escaladores › DLSS4.");
        const string target = @"ILION\Escaladores\DLSS4\Modelo_K";
        var writes = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).Select(f =>
        {
            var rel = Path.Combine(target, Path.GetRelativePath(source, f));
            var existing = Path.Combine(exeDir, rel);
            return new PlannedWrite(rel, f, File.Exists(existing) ? ComponentUpdateService.Sha256(existing) : null);
        }).ToList();
        return Operations.Apply(GameId(game), exeDir, "DLSS 4 local", $"DLSS 4 Modelo K desde tu copia ({writes.Count} archivos)", writes, "instalación local", source);
    }

    /// <summary>La carpeta Modelo_K (la que tiene d4r.ini) dentro de la elegida.</summary>
    public static string? FindD4r(string folder)
    {
        if (!Directory.Exists(folder)) return null;
        if (File.Exists(Path.Combine(folder, "d4r.ini"))) return folder;
        return Directory.EnumerateFiles(folder, "d4r.ini", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 4, IgnoreInaccessible = true })
            .Select(Path.GetDirectoryName).FirstOrDefault();
    }

    public static bool Dlss4Installed(string exeDir) => File.Exists(Path.Combine(exeDir, "ILION", "Escaladores", "DLSS4", "Modelo_K", "d4r.ini"));

    // ---------------------------------------------------------------- Instalación nueva
    public async Task<NewInstallResult> NewInstallAsync(LibraryGame game, string exeDir, EngineSource engine, string proxy, ISet<string> baseGroups,
        IReadOnlyList<BatchRequest> mods, GpuArch? arch, IEnumerable<string> searchRoots, IProgress<ModProgress>? progress, CancellationToken token)
    {
        var gameId = GameId(game);
        if (Installer.Installed(gameId) is not null) return new(null, null, [], "Este juego ya tiene una instalación de ILION hecha con Setup: usa «Actualizar ILION» o «Verificar y reparar».", false);
        var staging = Path.Combine(Data, "Staging", Guid.NewGuid().ToString("N"));
        try
        {
            // ---- Fase 1: preparar (el juego no se toca)
            progress?.Report(new("Base congelada", "Preparando", null, null, null, 0, 0, "Reuniendo la base de OptiScaler"));
            var locals = engine.PackageFolder is null ? Array.Empty<string>() : [engine.PackageFolder];
            if (engine.Remote)
            {
                // La publicación oficial: la DLL y el INI aprobados (un paquete local idéntico evita la descarga)
                engine = await DownloadEngineAsync(searchRoots, progress, token);
                if (engine.Problem is { } bad) return new(null, null, [], bad, false);
                locals = [.. searchRoots];
            }
            // El OptiScaler.ini del nightly solo hace falta si el paquete del motor no trae el suyo (si lo trae, se usa ese y no se descarga nada)
            var groups = new HashSet<string>(baseGroups);
            if (engine.PackageFolder is not { } withIni || !File.Exists(Path.Combine(withIni, "OptiScaler.ini"))) groups.Add("ini");
            // El runtime NR de lmxxf (760 MB) solo sirve en RDNA 3 y RDNA 4
            if (groups.Contains("variantes") && arch?.Id is "rdna3" or "rdna4") groups.Add("lmxxf");
            var sources = await Frozen.PrepareAsync(staging, groups, locals, progress, token);
            File.Copy(engine.Dll, Path.Combine(staging, "dxgi.dll"), true);
            if (engine.PackageFolder is { } pkg)
            {
                // Del paquete del motor: su configuración y lo que ILION trae integrado (escaladores, d4r, runtime NR de lmxxf)
                if (File.Exists(Path.Combine(pkg, "OptiScaler.ini"))) File.Copy(Path.Combine(pkg, "OptiScaler.ini"), Path.Combine(staging, "OptiScaler.ini"), true);
                // Lo ya preparado y verificado por el manifiesto no se sobrescribe con la copia del paquete
                CopyTree(Path.Combine(pkg, "ILION"), Path.Combine(staging, "ILION"), rel => !(LeavesOutD4r(arch) && rel.StartsWith(@"Escaladores\DLSS4", StringComparison.OrdinalIgnoreCase))
                    && !File.Exists(Path.Combine(staging, "ILION", rel)));
                // Los plugins del paquete (variantes XeMFG Clásica/WMMA, OptiPatcher): su OptiScaler.ini puede elegirlos (XeMfgVariant)
                CopyTree(Path.Combine(pkg, "OptiScaler", "plugins"), Path.Combine(staging, "OptiScaler", "plugins"), rel => !File.Exists(Path.Combine(staging, "OptiScaler", "plugins", rel)));
                foreach (var nr in new[] { "LmxxfNrRuntime.dll", "LmxxfNrRuntime.pak" })
                    if (File.Exists(Path.Combine(pkg, nr)) && arch?.Id is null or "rdna3" or "rdna4") File.Copy(Path.Combine(pkg, nr), Path.Combine(staging, nr), true);
            }
            token.ThrowIfCancellationRequested();
            var package = OptiScalerInstaller.InspectFolder(staging, engine.PackageFolder is null ? "DLL local: " + engine.Dll : "Paquete local: " + engine.PackageFolder);
            var (refusal, _, _) = Installer.Plan(exeDir, package, proxy, null, keepExistingIni: true);
            if (refusal is not null) return new(null, null, sources, refusal, false);
            // Los paquetes de los mods también se descargan y verifican ahora: si alguno falla, el juego no se ha tocado
            var prefetch = new ModInstaller(Http, Data, Operations);
            for (var i = 0; i < mods.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                progress?.Report(new(mods[i].Mod.Name, "Descargando", null, null, null, i, mods.Count, "Descargando y verificando el paquete aprobado"));
                await prefetch.DownloadAsync(mods[i].Mod, token);
            }

            // ---- Fase 2: escribir el motor y la base (operación restaurable), después los mods (lote coordinado)
            progress?.Report(new("OptiScaler ILION", "Instalando", null, null, null, 0, 0, "Copiando la base y el motor al juego"));
            var installed = Installer.Install(gameId, exeDir, package, proxy, null);
            BatchResult? batch = null;
            if (mods.Count > 0)
            {
                var installer = new ModInstaller(Http, Data, Operations);
                var roots = searchRoots.Concat(locals).Where(r => !r.Equals(game.Root, StringComparison.OrdinalIgnoreCase)).ToArray();
                batch = await ModBatch.RunAsync(installer, Operations, Updates, gameId, exeDir, mods, roots, progress, token);
            }
            return new(installed, batch, sources, null, false);
        }
        catch (OperationCanceledException) { return new(null, null, [], null, true); }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException)
        { return new(null, null, [], ModBatch.Friendly(e), false); }
        finally { try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch (IOException) { } }
    }

    /// <summary>d4r (DLSS en AMD) solo sirve en RDNA 3 y RDNA 4: con otra GPU reconocida no se copia.</summary>
    public static bool LeavesOutD4r(GpuArch? arch) => arch is { } a && a.Id is not ("rdna3" or "rdna4");

    private static void CopyTree(string from, string to, Func<string, bool> keep)
    {
        if (!Directory.Exists(from)) return;
        foreach (var f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(from, f);
            if (!keep(rel)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(to, rel))!);
            File.Copy(f, Path.Combine(to, rel), true);
        }
    }

    // ---------------------------------------------------------------- Actualizar ILION
    /// <summary>¿Se puede cambiar solo la DLL? La base instalada debe ser la congelada; si falta o cambió algo, se dice qué.</summary>
    public (bool Ok, string[] Notes) CheckUpdate(string exeDir, OptiScalerInstallation current, EngineSource engine)
    {
        var notes = new List<string>();
        if (current.Binary is null) return (false, ["El juego no tiene OptiScaler instalado: usa «Instalación nueva»."]);
        if (OptiScalerInstaller.EditionOf(current.Binary) != OptiEdition.Ilion) notes.Add("La DLL actual no es ILION: se sustituye igualmente por la de ILION.");
        if (engine.Problem is { } p) return (false, [p]);
        var check = Frozen.Verify(exeDir, Frozen.InstalledGroups(exeDir));
        var missing = check.Where(c => c.State == BaseState.Missing).Select(c => c.File.Destination).ToArray();
        var different = check.Where(c => c.State == BaseState.Different).Select(c => c.File.Destination).ToArray();
        if (missing.Length > 0) notes.Add($"A la base le faltan {missing.Length} archivos (p. ej. {string.Join(", ", missing.Take(3))}). Repáralos en «Verificar y reparar».");
        if (different.Length > 0) notes.Add($"{different.Length} archivos de la base no son los congelados (p. ej. {string.Join(", ", different.Take(3))}). Puede ser una actualización tuya; «Verificar y reparar» los compara.");
        if (!engine.Approved) notes.Add($"La DLL elegida (ILION {engine.Version}) no es la aprobada para esta base ({Base.Engine.Version}): compatibilidad no verificada.");
        return (missing.Length == 0, [.. notes]);
    }

    public OperationManifest UpdateEngine(LibraryGame game, string exeDir, OptiScalerInstallation current, EngineSource engine) =>
        Installer.ReplaceBase(GameId(game), exeDir, current.Binary!, engine.Dll);

    // ---------------------------------------------------------------- Verificar y reparar
    public VerifyItem[] Verify(LibraryGame game, string exeDir, OptiScalerInstallation current)
    {
        var items = new List<VerifyItem>();
        if (current.Binary is null) { items.Add(new("Motor", "OptiScaler ILION", Level.Error, "No está instalado en este juego.")); return [.. items]; }
        var edition = OptiScalerInstaller.EditionOf(current.Binary);
        var sha = ComponentUpdateService.Sha256(current.Binary);
        items.Add(new("Motor", Path.GetFileName(current.Binary), edition != OptiEdition.Ilion ? Level.Error : sha == Base.Engine.Sha256 ? Level.Ok : Level.Warn,
            edition != OptiEdition.Ilion ? "No es la edición ILION."
            : sha == Base.Engine.Sha256 ? $"ILION {Base.Engine.Version}, la aprobada para esta base."
            : $"ILION {OptiScalerInstaller.PublicVersion(current.Binary) ?? "sin versión"}: no es la aprobada ({Base.Engine.Version})."));
        if (current.Ambiguous) items.Add(new("Motor", "Varios archivos de carga", Level.Error, "Hay más de una DLL de OptiScaler: puede cargarse dos veces o ninguna."));
        var groups = Frozen.InstalledGroups(exeDir);
        foreach (var c in Frozen.Verify(exeDir, groups).Where(c => c.File.Destination != "OptiScaler.ini"))
            items.Add(new("Base", c.File.Destination, c.State == BaseState.Ok ? Level.Ok : c.State == BaseState.Missing ? Level.Error : Level.Warn,
                c.State == BaseState.Ok ? "Idéntico a la base congelada." : c.State == BaseState.Missing ? "Falta." : "Distinto de la base congelada (¿actualizado a mano?)."));
        var installedMods = Installable().Where(r => ModInstaller.Installed(r, exeDir)).ToArray();
        var updates = ModUpdates(game, exeDir).ToDictionary(u => u.Recipe.Key);
        foreach (var r in installedMods)
        {
            var missing = ModRequirements.Missing(r, exeDir);
            var update = updates.TryGetValue(r.Key, out var u) ? $" Versión aprobada distinta: {u.Installed} → {u.Approved} (abajo, «Actualizaciones de mods»)." : "";
            items.Add(new("Mods", r.Name, missing.Length > 0 ? Level.Error : update.Length > 0 ? Level.Warn : Level.Ok,
                (missing.Length == 0 ? "Completo." : string.Join(" · ", missing.Select(m => m.Message))) + update));
        }
        foreach (var slot in installedMods.Where(r => r.Slot is not null).GroupBy(r => r.Slot).Where(g => g.Count() > 1))
            items.Add(new("Notas", $"Varias opciones de {slot.Key}", Level.Info, $"{string.Join(", ", slot.Select(r => r.Short ?? r.Name))}. Conviven sin problema: ILION usa la que elijas en su menú."));
        foreach (var r in installedMods)
            if (ModInstaller.ConflictsIn(r, exeDir) is { Length: > 0 } clash)
                items.Add(new("Conflictos", r.Name, Level.Error, $"No funciona junto a {string.Join(", ", clash)}."));
        if (!File.Exists(Path.Combine(exeDir, "OptiScaler.ini"))) items.Add(new("Configuración", "OptiScaler.ini", Level.Warn, "No hay configuración: ILION usará sus valores por defecto."));
        return [.. items];
    }

    // ---------------------------------------------------------------- Actualizar mods
    /// <summary>Mods instalados en este juego cuya versión no es la aprobada ahora. La versión instalada sale del registro de la
    /// operación con la que Setup lo instaló. Sin registro no se ofrece nada: ese archivo puede venir dentro del paquete de ILION
    /// (runtime NR de lmxxf, HelixSR del paquete) o haberlo puesto el usuario, y «actualizarlo» pisaría algo que Setup no instaló.</summary>
    public ModUpdate[] ModUpdates(LibraryGame game, string exeDir)
    {
        var id = GameId(game);
        var list = new List<ModUpdate>();
        foreach (var e in Catalog.Mods)
        {
            if (RecipeFor(e) is not { } r || r.Tag is not { } approved || !ModInstaller.Installed(r, exeDir)) continue;
            var last = ModBatch.LastInstall(Operations, id, r);
            if (last is null || last.Tag.Equals(approved, StringComparison.OrdinalIgnoreCase)) continue;
            list.Add(new(e, r, last.Tag.Length > 0 ? last.Tag : "?", approved, last));
        }
        return [.. list];
    }

    /// <summary>Actualiza un mod a la versión aprobada, con las mismas garantías que una instalación:
    /// 1) descarga, huella y comprobación de que el paquete nuevo trae todo lo que la receta espera, antes de tocar el juego;
    /// 2) escritura restaurable (copia de lo que se sustituye); 3) comprobación posterior: si al mod le falta algo que antes tenía,
    /// se deshace solo y vuelve la versión anterior; 4) lo que solo tenía la versión anterior se retira (restaurable), salvo lo que
    /// haya cambiado el usuario, que se deja. Todo queda en un mismo lote: «Deshacer» devuelve la versión anterior completa.</summary>
    public async Task<ModUpdateResult> UpdateModAsync(LibraryGame game, string exeDir, ModUpdate update, IEnumerable<string> gameRoots,
        IProgress<ModProgress>? progress, CancellationToken token)
    {
        var id = GameId(game);
        var recipe = update.Recipe;
        // Un mod proxy conserva su nombre de carga
        var proxy = recipe.ProxyNames.Length == 0 ? null
            : update.Last.Entries.Select(e => e.RelativePath).FirstOrDefault(p => recipe.ProxyNames.Contains(p, StringComparer.OrdinalIgnoreCase))
              ?? recipe.ProxyNames.FirstOrDefault(n => File.Exists(Path.Combine(exeDir, n)));
        var before = ModRequirements.Missing(recipe, exeDir).Select(m => m.Relative).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var installer = new ModInstaller(Http, Data, Operations);
        var batch = await ModBatch.RunAsync(installer, Operations, Updates, id, exeDir, [new BatchRequest(recipe, proxy, Updating: true)], gameRoots, progress, token);
        if (batch.Cancelled) return new(false, "Cancelado: no se escribió nada.", [], [], false, batch);
        if (batch.Installed.Length == 0)
            return new(false, batch.Failed.Select(f => f.Error).FirstOrDefault() ?? "No se pudo actualizar.", [], [], false, batch);

        // Comprobación posterior: el mod debe quedar al menos tan completo como estaba
        var worse = ModRequirements.Missing(recipe, exeDir).Select(m => m.Relative).Where(a => !before.Contains(a)).ToArray();
        if (!ModInstaller.Installed(recipe, exeDir) || worse.Length > 0)
        {
            var what = string.Join(", ", worse.DefaultIfEmpty("su archivo principal"));
            try { ModBatch.RestoreBatch(Operations, id, batch.BatchId); }
            catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException)
            { return new(false, $"La versión {update.Approved} no quedó completa (falta {what}) y no se pudo volver atrás sola: {e.Message} Usa «Deshacer cambios».", [], [], false, batch); }
            return new(false, $"La versión {update.Approved} no quedó completa (falta {what}): se volvió a la versión anterior.", [], [], true, batch);
        }

        // Lo que solo tenía la versión anterior: se retira si sigue tal como lo instaló Setup (si lo cambió el usuario, se deja)
        var removed = new List<string>(); var kept = new List<string>();
        {
            var last = update.Last;
            var nowWritten = batch.Operations.SelectMany(o => o.Entries).Select(e => e.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var thisBatch = batch.Operations.Select(o => o.Id).ToHashSet();
            // Un archivo que tocó después otra operación (otro mod, una reparación) ya no es solo de la versión anterior: se deja
            var touchedLater = Operations.All(id).Where(o => o.Completed && o.When > last.When && o.Id != last.Id && !thisBatch.Contains(o.Id)).ToArray();
            var writes = new List<PlannedWrite>();
            foreach (var e in last.Entries.Where(e => e.InstalledSha256 is not null && !nowWritten.Contains(e.RelativePath)))
            {
                if (touchedLater.Any(o => o.Touches(e.RelativePath))) { kept.Add(e.RelativePath); continue; }
                var path = Path.Combine(exeDir, e.RelativePath);
                if (!File.Exists(path)) continue;
                var sha = ComponentUpdateService.Sha256(path);
                if (sha.Equals(e.InstalledSha256, StringComparison.OrdinalIgnoreCase)) { writes.Add(new(e.RelativePath, null, sha)); removed.Add(e.RelativePath); }
                else kept.Add(e.RelativePath);
            }
            if (writes.Count > 0)
                try { Operations.Apply(id, exeDir, ModBatch.RetireKind, $"{recipe.Name}: retirada de la versión {last.Tag}", writes, $"github.com/{recipe.Repository}", update.Approved, batch.BatchId); }
                catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException)
                {
                    // La versión nueva ya está instalada y completa; solo quedan archivos sobrantes de la anterior (no estorban)
                    return new(true, $"{recipe.Name}: {update.Installed} → {update.Approved}. No se pudieron retirar {writes.Count} archivos que ya no usa ({e.Message}); se pueden borrar a mano.", [], [.. kept, .. removed], false, batch);
                }
        }
        return new(true, $"{recipe.Name}: {update.Installed} → {update.Approved}.", [.. removed], [.. kept], false, batch);
    }

    /// <summary>Repara la base: repone lo que falta y, solo si se pide, sustituye lo que difiere. Operación restaurable.</summary>
    public async Task<OperationManifest?> RepairBaseAsync(LibraryGame game, string exeDir, bool replaceDifferent, IEnumerable<string> localFolders, IProgress<ModProgress>? progress, CancellationToken token)
    {
        var groups = Frozen.InstalledGroups(exeDir);
        var targets = Frozen.Verify(exeDir, groups).Where(c => c.File.Destination != "OptiScaler.ini" && (c.State == BaseState.Missing || replaceDifferent && c.State == BaseState.Different)).ToArray();
        if (targets.Length == 0) return null;
        var staging = Path.Combine(Data, "Staging", Guid.NewGuid().ToString("N"));
        try
        {
            await Frozen.PrepareFilesAsync(staging, [.. targets.Select(c => c.File)], localFolders, progress, token);
            var writes = targets.Select(c => new PlannedWrite(c.File.Destination, Path.Combine(staging, c.File.Destination), c.ActualSha256)).ToList();
            return Operations.Apply(GameId(game), exeDir, "Reparar base", $"Reparar base congelada ({writes.Count} archivos)", writes, "base congelada", Base.Date);
        }
        finally { try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch (IOException) { } }
    }
}
