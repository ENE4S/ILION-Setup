// Pruebas de ILION Setup en un laboratorio aislado. Nunca escribe en juegos reales: el «juego» es una carpeta del laboratorio.
// Uso: Setup.Tests <carpeta-de-laboratorio> <paquete-ILION-de-referencia> [--sin-red]
#pragma warning disable CA1416
using System.Net;
using System.Text.Json;
using Ilion.Client;
using Ilion.Setup;

var lab = Path.GetFullPath(args[0]);
var reference = Path.GetFullPath(args[1]);
var noNetwork = args.Contains("--sin-red");
var fuente = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
var fails = 0; var count = 0;
void Check(bool ok, string what) { count++; Console.WriteLine($"[{(ok ? "OK" : "FALLO")}] {what}"); if (!ok) fails++; }
string[] Snap(string d) => Directory.Exists(d) ? [.. Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(d, f) + "|" + ComponentUpdateService.Sha256(f)).Order()] : [];

var baseJson = File.ReadAllText(Path.Combine(fuente, "Manifiestos", "base-congelada.json"));
var catalogJson = File.ReadAllText(Path.Combine(fuente, "Setup", "Assets", "catalogo.json"));
var baseManifest = Manifests.Parse<BaseManifest>(baseJson);
var catalog = Manifests.Parse<Catalog>(catalogJson);
Manifests.PublicKey = File.ReadAllText(Path.Combine(fuente, "Setup", "Assets", "clave-publica.txt")).Trim();
var referenceBefore = Snap(reference);

// Diagnóstico: Setup.Tests <lab> <paquete> --lote <carpeta-de-datos> id1,id2,…  → instala esos mods en un juego del laboratorio e imprime cada fallo
if (Array.IndexOf(args, "--lote") is var li and > 0)
{
    var loteData = Path.GetFullPath(args[li + 1]);
    var ids = args[li + 2].Split(',');
    var loteEngine = new SetupEngine(loteData, new HttpClient(), baseManifest, catalog);
    var loteRoot = Path.Combine(lab, "JuegoLote");
    if (Directory.Exists(loteRoot)) Directory.Delete(loteRoot, true);
    var loteDir = Directory.CreateDirectory(Path.Combine(loteRoot, "bin", "x64")).FullName;
    var loteExe = Path.Combine(loteDir, "JuegoLote.exe");
    File.Copy(Path.Combine(reference, "OptiScaler", "plugins", "OptiPatcher.asi"), loteExe);
    var loteManual = new ManualSource(Path.Combine(loteData, "manual-lote.json"));
    loteManual.Add(loteExe);
    var loteGame = loteManual.Scan().Games.Single(x => x.Executable == loteExe);
    var archs = Gpu.Load(File.OpenRead(Path.Combine(fuente, "Setup", "Assets", "arquitecturas.json")));
    var reqs = ids.Select(id => new BatchRequest(SetupEngine.RecipeFor(catalog.Mods.Single(m => m.Id == id))!)).ToList();
    var r = await loteEngine.NewInstallAsync(loteGame, loteDir, loteEngine.RemoteEngine, "dxgi.dll", new HashSet<string> { "streamline", "variantes" }, reqs,
        archs.Arquitecturas.Single(a => a.Id == "rdna3"), [reference], new Sync<ModProgress>(p => { if (p.Stage is not "Descargando") Console.WriteLine($"   · {p.Mod} {p.Stage} {p.Message} {p.Detail}"); }), default);
    Console.WriteLine($"ERROR GENERAL: {r.Error ?? "-"}");
    foreach (var ok in r.Mods?.Installed ?? []) Console.WriteLine($"INSTALADO: {ok}");
    foreach (var (mod, why) in r.Mods?.Failed ?? []) Console.WriteLine($"FALLO: {mod}: {why}");
    foreach (var p in r.Mods?.Pending ?? []) Console.WriteLine($"PENDIENTE: {p}");
    return 0;
}

// ---------------------------------------------------------------- 1. Base congelada
Check(baseManifest.Files.Length >= 30 && baseManifest.Files.All(f => f.Sha256.Length == 64), $"Manifiesto de la base: {baseManifest.Files.Length} archivos con SHA-256");
var pkgMatches = baseManifest.Files.Where(f => FrozenBase.Group(f) is "core" or "streamline")
    .Select(f => (f, path: Path.Combine(reference, f.Destination))).Select(x => (x.f, ok: File.Exists(x.path) && ComponentUpdateService.Sha256(x.path) == x.f.Sha256)).ToArray();
Check(pkgMatches.All(x => x.ok), $"Correspondencia: los {pkgMatches.Length} archivos de base y streamline son idénticos en el paquete funcional de referencia"
    + (pkgMatches.Any(x => !x.ok) ? " · distintos: " + string.Join(", ", pkgMatches.Where(x => !x.ok).Select(x => x.f.Destination)) : ""));
Check(File.Exists(Path.Combine(reference, "dxgi.dll")) && ComponentUpdateService.Sha256(Path.Combine(reference, "dxgi.dll")) == baseManifest.Engine.Sha256,
    $"Motor aprobado = dxgi.dll del paquete de referencia ({baseManifest.Engine.Sha256[..12]}…)");

using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
http.DefaultRequestHeaders.UserAgent.ParseAdd("ILION-Setup-Tests");
using var offline = new HttpClient(new NoNetwork());
var dataDir = Path.Combine(lab, "Datos");
if (Directory.Exists(dataDir)) Directory.Delete(dataDir, true);

// Sin conexión: base y streamline salen del paquete local por su huella
{
    var fb = new FrozenBase(offline, Path.Combine(lab, "DatosSinRed"), baseManifest);
    var staging = Path.Combine(lab, "staging-sin-red");
    if (Directory.Exists(staging)) Directory.Delete(staging, true);
    var sources = await fb.PrepareAsync(staging, new HashSet<string> { "streamline" }, [reference], null, default);
    Check(sources.All(s => s.EndsWith("paquete local")) && fb.Verify(staging, new HashSet<string> { "streamline" }).All(c => c.State == BaseState.Ok),
        $"Sin conexión: base + streamline preparados desde el paquete local por SHA-256 ({sources.Length} archivos, sin red)");
    Directory.Delete(staging, true);
    var failed = false;
    try { await fb.PrepareAsync(Path.Combine(lab, "staging-x"), new HashSet<string> { "agility" }, [reference], null, default); } catch (HttpRequestException) { failed = true; }
    Check(failed, "Sin conexión: lo que el paquete local no tiene (D3D12Core) falla con un error de red claro, sin instalar nada a medias");
}
// Con red: la compilación oficial de f6f75dd y FidelityFX 2.4.0 de AMD, verificados
if (!noNetwork)
{
    var fb = new FrozenBase(http, dataDir, baseManifest);
    var staging = Path.Combine(lab, "staging-red");
    if (Directory.Exists(staging)) Directory.Delete(staging, true);
    var sources = await fb.PrepareAsync(staging, new HashSet<string> { "agility", "ini" }, [], null, default);
    Check(sources.Any(s => s.Contains("nightly-20261009")) && sources.Any(s => s.Contains("FidelityFX-SDK/v2.4.0")) && fb.Verify(staging, new HashSet<string> { "agility" }).All(c => c.State == BaseState.Ok),
        $"Descarga real: nightly-20261009 (f6f75dd) + FidelityFX SDK v2.4.0, todos con su SHA-256 ({sources.Length} archivos)");
    // Huella falsa: se rechaza y no queda nada
    var bad = baseManifest with { Files = [baseManifest.Files.First(f => f.Url is not null) with { Sha256 = new string('0', 64) }] };
    var rejected = false;
    try { await new FrozenBase(http, Path.Combine(lab, "DatosHuella"), bad).PrepareAsync(Path.Combine(lab, "staging-huella"), new HashSet<string>(), [], null, default); }
    catch (InvalidDataException e) { rejected = e.Message.Contains("huella"); }
    Check(rejected && !Directory.EnumerateFiles(Path.Combine(lab, "DatosHuella"), "*", SearchOption.AllDirectories).Any(f => !f.EndsWith(".part")), "Una huella que no coincide se rechaza y la descarga se descarta");
    Directory.Delete(staging, true);
}

// ---------------------------------------------------------------- 2. GPU y catálogo
Check(Gpu.Classify("AMD Radeon RX 7900 XTX") == "rdna3" && Gpu.Classify("NVIDIA GeForce RTX 3080") == "rtx30" && Gpu.Classify("Intel(R) Arc(TM) B580 Graphics") == "arc_b"
      && Gpu.Classify("NVIDIA GeForce GTX 1660 SUPER") == "nv_gtx" && Gpu.Classify("AMD Radeon RX 9070 XT") == "rdna4", "Clasificación de GPU (RDNA 3/4, RTX 30, GTX, Arc B)");
using var archStream = File.OpenRead(Path.Combine(fuente, "Setup", "Assets", "arquitecturas.json"));
var table = Gpu.Load(archStream);
var detected = Gpu.Detect(table, Gpu.Installed());
Console.WriteLine($"   GPU de este equipo: {detected.Name} → {detected.Arch?.Id ?? "no reconocida"}");
Check(detected.Arch is not null, "Detección de la GPU de este equipo");
var engine = new SetupEngine(dataDir, noNetwork ? offline : http, baseManifest, catalog);
var rdna3 = table.Arquitecturas.Single(a => a.Id == "rdna3"); var rtx30 = table.Arquitecturas.Single(a => a.Id == "rtx30");
var r3 = engine.Resolve(rdna3, "DirectX 12", "1.4.1.2 beta");
Check(r3.Where(i => i.Preselect).Select(i => i.Entry.Id).Order().SequenceEqual(["helix19", "xmx"]), "RDNA 3 + DX12: se preseleccionan XeMFG XMX y HelixSR 1.9 (combinación verificada)");
Check(r3.Single(i => i.Entry.Id == "sm86").Compat == Compat.Incompatible && r3.Single(i => i.Entry.Id == "anr").Compat == Compat.Warning, "RDNA 3: DLSS for SM86 no compatible; ANR con aviso (experimental)");
var vk = engine.Resolve(rdna3, "Vulkan", "1.4.1.2 beta");
Check(vk.Single(i => i.Entry.Id == "xmx").Compat == Compat.Incompatible && !vk.Any(i => i.Preselect && i.Entry.Id == "xmx") && vk.Single(i => i.Entry.Id == "fakemichau").Compat == Compat.Compatible,
    "Juego Vulkan: XMX no se ofrece (solo DX12) y DLSSG-XeFG sí");
Check(engine.Resolve(rtx30, "DirectX 12", "1.4.1.2 beta").Single(i => i.Entry.Id == "sm86").Compat == Compat.Warning
      && engine.Resolve(rdna3, "DirectX 12", "1.4.1").Single(i => i.Entry.Id == "fakemichau").Compat == Compat.Incompatible, "RTX 30: SM86 disponible (sin verificar); con ILION 1.4.1, DLSSG-XeFG se bloquea (necesita 1.4.1.2)");
Check(catalog.Mods.Select(m => m.Id).Distinct().Count() == catalog.Mods.Length && catalog.Mods.Where(m => m.Recipe is not null).All(m => m.Sha256?.Length == 64 && m.Version is not null)
      && !catalog.Mods.Any(m => m.Name.Contains("Nightly", StringComparison.OrdinalIgnoreCase) || m.Name.Contains("Ray R", StringComparison.OrdinalIgnoreCase)),
    "Catálogo: sin repetidos, cada mod instalable con versión y SHA-256 aprobados, sin Nightly ni Ray Reconstruction");

// ---------------------------------------------------------------- 3. Juegos
var scan = LibraryDiscovery.Scan(WindowsLibraryLocations.DefaultSources());
Console.WriteLine($"   Juegos de este PC (solo lectura): {string.Join(", ", scan.Games.GroupBy(g => g.Platform).Select(g => $"{g.Key} {g.Count()}"))}");
Check(scan.Games.Length > 0, "Detección de juegos (Steam, Epic, GOG, Xbox, EA, Ubisoft, Battle.net, Rockstar)");
var gameRoot = Path.Combine(lab, "JuegoSetup");
if (Directory.Exists(gameRoot)) Directory.Delete(gameRoot, true);
var exeDir = Directory.CreateDirectory(Path.Combine(gameRoot, "bin", "x64")).FullName;
var exe = Path.Combine(exeDir, "JuegoSetup.exe");
File.Copy(Path.Combine(reference, "OptiScaler", "plugins", "OptiPatcher.asi"), exe); // un PE cualquiera hace de ejecutable
File.WriteAllText(Path.Combine(exeDir, "partida-del-usuario.sav"), "mío");
var manual = new ManualSource(Path.Combine(lab, "Datos", "manual.json"));
manual.Add(exe);
var game = manual.Scan().Games.Single();
Check(game.Executable == exe, "Ruta manual: ejecutable elegido a mano");
var clean = Snap(gameRoot);

// ---------------------------------------------------------------- 4. Instalación nueva
var src = engine.InspectEngine(reference);
Check(src.Problem is null && src.Approved && src.Version.StartsWith("1.4.1.2"), $"Motor desde el paquete local: ILION {src.Version}, aprobado");
var picks = new[] { "xmx", "mfg-unlocker", "fakemichau", "helix19" }.Select(id => new BatchRequest(SetupEngine.RecipeFor(catalog.Mods.Single(m => m.Id == id))!)).ToList();
// Cancelación en la preparación: nada escrito
using (var cts = new CancellationTokenSource())
{
    cts.Cancel();
    var c = await engine.NewInstallAsync(game, exeDir, src, "dxgi.dll", new HashSet<string> { "streamline" }, picks, rdna3, [reference], null, cts.Token);
    Check(c.Cancelled && Snap(gameRoot).SequenceEqual(clean), "Cancelar durante la preparación: el juego queda intacto");
}
if (noNetwork)
{
    // Sin conexión y sin caché de mods: con mods falla antes de escribir; sin mods, base + motor salen del paquete local
    var withMods = await engine.NewInstallAsync(game, exeDir, src, "dxgi.dll", new HashSet<string> { "streamline" }, picks, rdna3, [reference], null, default);
    Console.WriteLine($"   sin red, con mods: {withMods.Error}");
    var untouched = Snap(gameRoot).SequenceEqual(clean);
    var afterMods = withMods.Mods is { } m ? m.Installed.Length + "/" + picks.Count : "-";
    Check(withMods.Error is not null && withMods.Engine is null && untouched, $"Sin conexión con mods sin caché: falla al preparar, antes de escribir nada (juego intacto: {untouched})");
    if (withMods.Engine is not null) engine.Installer.Uninstall(SetupEngine.GameId(game), keepIni: false);
    var bare = await engine.NewInstallAsync(game, exeDir, src, "dxgi.dll", new HashSet<string> { "streamline" }, [], rdna3, [reference], null, default);
    Console.WriteLine($"   sin red, sin mods: {bare.Error}");
    Check(bare.Error is null && bare.Engine is not null && engine.Frozen.Verify(exeDir, new HashSet<string> { "streamline" }).All(c => c.State == BaseState.Ok)
          && ComponentUpdateService.Sha256(Path.Combine(exeDir, "dxgi.dll")) == baseManifest.Engine.Sha256, "Sin conexión, sin mods: base + Streamline + motor instalados solo desde el paquete local");
    engine.Installer.Uninstall(SetupEngine.GameId(game), keepIni: false);
    Check(Snap(gameRoot).SequenceEqual(clean), "Sin conexión: desinstalar deja el juego como estaba");
    Console.WriteLine(fails == 0 ? $"ILION SETUP (sin red): {count}/{count} CORRECTO" : $"ILION SETUP (sin red): {fails} FALLOS de {count}");
    return fails == 0 ? 0 : 1;
}
var stages = new List<ModProgress>();
var started = DateTime.Now;
var result = await engine.NewInstallAsync(game, exeDir, src, "dxgi.dll", new HashSet<string> { "streamline", "variantes" }, picks, rdna3, [reference], new Sync<ModProgress>(stages.Add), default);
Console.WriteLine($"   instalación nueva en {(DateTime.Now - started).TotalSeconds:0} s · error: {result.Error} · fallos: {string.Join(" | ", result.Mods?.Failed.Select(f => f.Mod + ": " + f.Error) ?? [])}");
Check(result.Engine is not null && result.Error is null, "Instalación nueva: base congelada + motor ILION escritos");
Check(ComponentUpdateService.Sha256(Path.Combine(exeDir, "dxgi.dll")) == baseManifest.Engine.Sha256, "dxgi.dll del juego = DLL aprobada");
Check(engine.Frozen.Verify(exeDir, new HashSet<string> { "streamline" }).All(c => c.State == BaseState.Ok), "Base del juego idéntica a la congelada (incluida Streamline)");
Check(result.Mods is { } mr && mr.Installed.Length == 4 && mr.Failed.Length == 0, "Mods del lote instalados: " + string.Join(", ", result.Mods?.Installed ?? []));
Check(picks.All(p => ModRequirements.Missing(p.Mod, exeDir).Length == 0), "Todos completos (XMX con su libxess_fg 1.3.4 tomada firmada del paquete local)");
Check(File.Exists(Path.Combine(exeDir, "ILION", "Escaladores", "HelixSR", "helix1.9", "helixsr_upscaler.dll")) && File.Exists(Path.Combine(exeDir, "LmxxfNrRuntime.dll"))
      && Directory.Exists(Path.Combine(exeDir, "ILION", "Escaladores", "DLSS4")) && File.Exists(Path.Combine(exeDir, "OptiScaler", "plugins", "XeMFG", "Clasica", "XeFGUnlock.asi")),
    "Del paquete: ILION\\ (d4r en RDNA 3), runtime NR de lmxxf y variantes XeMFG");
Check(File.ReadAllText(Path.Combine(exeDir, "partida-del-usuario.sav")) == "mío", "Archivos del usuario intactos");
Check(stages.All(s => s.Fraction is null or (>= 0 and <= 1)) && stages.Any(s => s.Stage == "Instalando"), "Progreso: etapas reales, porcentajes siempre entre 0 y 100");
var again = await engine.NewInstallAsync(game, exeDir, src, "dxgi.dll", new HashSet<string>(), [], rdna3, [], null, default);
Check(again.Error?.Contains("Actualizar") == true, "Una segunda instalación nueva en el mismo juego se rechaza y remite a Actualizar");

var variants = engine.Frozen.Verify(exeDir, new HashSet<string> { "variantes", "lmxxf" }).Where(c => FrozenBase.Group(c.File) is "variantes" or "lmxxf").ToArray();
Check(variants.Length == 11 && variants.All(c => c.State == BaseState.Ok),
    $"Variantes de ILION instaladas y verificadas: FSR 4 (oficial, 4.1.1B, Exact, Lossy, Lossy2), XeMFG Clásica/WMMA, OptiPatcher v0.42, Instalar_ReShade.bat y runtime lmxxf ({variants.Count(c => c.State == BaseState.Ok)}/{variants.Length})");

// ---------------------------------------------------------------- 4b. Motor desde la publicación oficial de GitHub + DLSS 4 local
{
    var remote = engine.RemoteEngine;
    Check(remote is { Remote: true, Problem: null, Approved: true }, $"Motor por defecto: publicación oficial ILION {remote.Version} en GitHub");
    var downloaded = await engine.DownloadEngineAsync([], null, default);
    var iniSha = baseManifest.Files.Single(f => f.GroupName == "motor" && f.Destination == "OptiScaler.ini").Sha256;
    Check(downloaded.Approved && downloaded.PackageFolder is { } mf && ComponentUpdateService.Sha256(Path.Combine(mf, "OptiScaler.ini")) == iniSha,
        "Descarga real de ilion-v1.4.1.2-beta: dxgi.dll e INI con su SHA-256");
    var gitRoot = Path.Combine(lab, "JuegoGitHub");
    if (Directory.Exists(gitRoot)) Directory.Delete(gitRoot, true);
    var gitDir = Directory.CreateDirectory(Path.Combine(gitRoot, "Binaries", "Win64")).FullName;
    var gitExe = Path.Combine(gitDir, "JuegoGitHub.exe");
    File.Copy(Path.Combine(reference, "OptiScaler", "plugins", "OptiPatcher.asi"), gitExe);
    manual.Add(gitExe);
    var gitGame = manual.Scan().Games.Single(x => x.Executable == gitExe);
    var gitClean = Snap(gitRoot);
    var r = await engine.NewInstallAsync(gitGame, gitDir, remote, "dxgi.dll", new HashSet<string>(), [], rdna3, [], null, default);
    Check(r.Error is null && r.Engine is not null && ComponentUpdateService.Sha256(Path.Combine(gitDir, "dxgi.dll")) == baseManifest.Engine.Sha256
          && ComponentUpdateService.Sha256(Path.Combine(gitDir, "OptiScaler.ini")) == iniSha,
        "Instalación nueva con el motor de GitHub: DLL e INI publicados en el juego" + (r.Error is null ? "" : " · " + r.Error));
    var d4r = SetupEngine.FindD4r(reference);
    Check(d4r is not null && !SetupEngine.Dlss4Installed(gitDir), "DLSS 4 Modelo K: se encuentra en la copia local (d4r.ini)");
    var d4op = engine.InstallDlss4Local(gitGame, gitDir, d4r!);
    Check(SetupEngine.Dlss4Installed(gitDir) && File.Exists(Path.Combine(gitDir, "ILION", "Escaladores", "DLSS4", "Modelo_K", "nvngx_dlss.dll")), "DLSS 4 Modelo K: instalación local desde la copia del usuario");
    engine.Operations.Restore(d4op);
    Check(!SetupEngine.Dlss4Installed(gitDir), "DLSS 4 Modelo K: restaurable");
    engine.Installer.Uninstall(SetupEngine.GameId(gitGame), keepIni: false);
    Check(Snap(gitRoot).SequenceEqual(gitClean), "Motor de GitHub: desinstalar deja el juego como estaba");
}

// ---------------------------------------------------------------- 5. Descarga no aprobada
var tampered = catalog.Mods.Single(m => m.Id == "anr") with { Sha256 = new string('a', 64), Name = "ANR no aprobado" };
var batchBad = await ModBatch.RunAsync(new ModInstaller(noNetwork ? offline : http, dataDir, engine.Operations), engine.Operations, engine.Updates, SetupEngine.GameId(game), exeDir,
    [new(SetupEngine.RecipeFor(tampered)!)], [], null, default);
Check(batchBad.Installed.Length == 0 && batchBad.Failed.Single().Error.Contains(noNetwork ? "conectar" : "aprobada"), "Un paquete que no es la versión aprobada no se instala: " + batchBad.Failed.FirstOrDefault().Error);

// ---------------------------------------------------------------- 6. Actualizar ILION (solo la DLL)
var info = OptiScalerDetection.Inspect(gameRoot, exe);
var other = Environment.GetEnvironmentVariable("ILION_SETUP_OTRA_DLL") ?? ""; // otra DLL de ILION (no aprobada) para probar «Actualizar»
if (File.Exists(other))
{
    var otherSrc = engine.InspectEngine(other);
    var (ok, notes) = engine.CheckUpdate(exeDir, info, otherSrc);
    Check(ok && notes.Any(n => n.Contains("no es la aprobada")), "Actualizar: base completa → se puede cambiar solo la DLL; avisa de que esa DLL no es la aprobada");
    var op = engine.UpdateEngine(game, exeDir, info, otherSrc);
    Check(ComponentUpdateService.Sha256(Path.Combine(exeDir, "dxgi.dll")) == ComponentUpdateService.Sha256(other) && engine.Frozen.Verify(exeDir, new HashSet<string> { "streamline" }).All(c => c.State == BaseState.Ok),
        "Actualizar: cambia solo dxgi.dll; base y mods intactos");
    engine.Operations.Restore(op);
    Check(ComponentUpdateService.Sha256(Path.Combine(exeDir, "dxgi.dll")) == baseManifest.Engine.Sha256, "Actualizar: restaurable (vuelve la DLL aprobada)");
}

// ---------------------------------------------------------------- 7. Verificar y reparar
var items = engine.Verify(game, exeDir, OptiScalerDetection.Inspect(gameRoot, exe));
Check(items.All(i => i.State is Level.Ok or Level.Info), "Verificar: todo correcto tras la instalación");
var libxess = Path.Combine(exeDir, "OptiScaler", "libxess.dll"); var vkdll = Path.Combine(exeDir, "OptiScaler", "amd_fidelityfx_vk.dll");
File.Delete(libxess); File.AppendAllText(vkdll, "x");
items = engine.Verify(game, exeDir, OptiScalerDetection.Inspect(gameRoot, exe));
Check(items.Any(i => i.What.EndsWith("libxess.dll") && i.State == Level.Error) && items.Any(i => i.What.EndsWith("amd_fidelityfx_vk.dll") && i.State == Level.Warn), "Verificar: detecta un archivo que falta y uno distinto");
await engine.RepairBaseAsync(game, exeDir, false, [reference], null, default);
Check(File.Exists(libxess) && ComponentUpdateService.Sha256(vkdll) != baseManifest.Files.Single(f => f.Destination.EndsWith("amd_fidelityfx_vk.dll")).Sha256, "Reparar: repone lo que falta y NO toca lo distinto sin permiso");
await engine.RepairBaseAsync(game, exeDir, true, [reference], null, default);
Check(engine.Frozen.Verify(exeDir, new HashSet<string> { "streamline" }).All(c => c.State == BaseState.Ok), "Reparar con permiso: lo distinto vuelve a la base congelada");

// ---------------------------------------------------------------- 8. Restaurar
var id = SetupEngine.GameId(game);
foreach (var repair in engine.Operations.All(id).Where(o => o.Completed && o.Kind == "Reparar base").OrderByDescending(o => o.When)) engine.Operations.Restore(repair);
var vkBase = baseManifest.Files.Single(f => f.Destination.EndsWith("amd_fidelityfx_vk.dll")).Sha256;
Check(ComponentUpdateService.Sha256(vkdll) != vkBase, "Restaurar las reparaciones: vuelve lo que había (incluido el archivo cambiado por el usuario)");
File.Copy(Path.Combine(reference, "OptiScaler", "amd_fidelityfx_vk.dll"), vkdll, true); // el «usuario» deshace su cambio
// El paquete del motor ya trae ILION\Escaladores\HelixSR\helix1.9: restaurar el mod devuelve la versión del paquete, no la borra
var helix = SetupEngine.RecipeFor(catalog.Mods.Single(m => m.Id == "helix19"))!;
var helixFile = Path.Combine(exeDir, helix.Marker); var helixPackage = Path.Combine(reference, helix.Marker);
ModBatch.RestoreMod(engine.Operations, id, exeDir, ModBatch.LastInstall(engine.Operations, id, helix)!, helix);
var helixBack = File.Exists(helixPackage) ? File.Exists(helixFile) && ComponentUpdateService.Sha256(helixFile) == ComponentUpdateService.Sha256(helixPackage) : !File.Exists(helixFile);
Check(helixBack && ModBatch.LastInstall(engine.Operations, id, helix) is null && ModInstaller.Installed(picks[0].Mod, exeDir),
    "Restaurar un mod suelto (HelixSR 1.9): se deshace solo ese (vuelve la versión del paquete); el resto del lote sigue");
ModBatch.RestoreBatch(engine.Operations, id, result.Mods!.BatchId);
engine.Installer.Uninstall(id, keepIni: false);
var leftovers = Snap(gameRoot).Except(clean).ToArray();
Check(Snap(gameRoot).SequenceEqual(clean), "Restaurar el lote y desinstalar: el juego queda byte a byte como antes" + (leftovers.Length > 0 ? " · sobran: " + string.Join(", ", leftovers.Take(5)) : ""));

// ---------------------------------------------------------------- 8b. Versión mostrada y manifiestos de GitHub
Check(OptiScalerInstaller.PublicVersion(Path.Combine(reference, "dxgi.dll")) == "1.4.1.2 beta", $"Versión del motor: «{OptiScalerInstaller.PublicVersion(Path.Combine(reference, "dxgi.dll"))}» (sin etiquetas añadidas)");
if (!noNetwork)
{
    var remoteDir = Path.Combine(lab, "ManifiestosGitHub");
    if (Directory.Exists(remoteDir)) Directory.Delete(remoteDir, true);
    var remoteNotes = await RemoteManifests.RefreshAsync(http, remoteDir, default);
    var fromGitHub = Manifests.Choose<BaseManifest>(baseJson, Path.Combine(remoteDir, "base-congelada.json"), m => m.Date, m => m.Format).Manifest;
    Check(remoteNotes.All(n => n.Contains("firmado")) && File.Exists(Path.Combine(remoteDir, "catalogo.json.firma")) && fromGitHub.Engine.Sha256 == baseManifest.Engine.Sha256,
        "Manifiestos firmados de GitHub (rama setup-manifiestos): descargados, firma válida, ILION " + fromGitHub.Engine.Version);
    var tamperedDir = Path.Combine(lab, "ManifiestosAlterados");
    if (Directory.Exists(tamperedDir)) Directory.Delete(tamperedDir, true);
    var tamperedNotes = await RemoteManifests.RefreshAsync(new HttpClient(new Tamper(http)), tamperedDir, default);
    Check(tamperedNotes.All(n => n.Contains("firma no es válida")) && !File.Exists(Path.Combine(tamperedDir, "base-congelada.json")),
        "Manifiesto de GitHub alterado por el camino: firma no válida, no se guarda ni se usa");
    using var noRedirect = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
    var latest = await RemoteManifests.LatestTagAsync(noRedirect, "dashdogy/MFG-Unlocker", default);
    Check(latest is { Length: > 0 }, $"Versión más nueva publicada por un autor, sin la API de GitHub: dashdogy/MFG-Unlocker → {latest}");
}

// ---------------------------------------------------------------- 9. Manifiestos firmados
var privateKey = Path.Combine(fuente, "..", "..", "Compartido", "Claves", "clave-manifiestos-PRIVADA.pem");
if (File.Exists(privateKey))
{
    var ext = Path.Combine(lab, "Manifiestos", "catalogo.json");
    Directory.CreateDirectory(Path.GetDirectoryName(ext)!);
    var newer = JsonSerializer.Serialize(catalog with { Date = "2099-01-01" });
    File.WriteAllText(ext, newer);
    File.WriteAllText(ext + ".firma", Manifests.Sign(File.ReadAllBytes(ext), File.ReadAllText(privateKey)));
    Check(Manifests.Choose<Catalog>(catalogJson, ext, c => c.Date, c => c.Format).Origin.StartsWith("externo firmado"), "Manifiesto externo firmado y más nuevo: se usa");
    File.AppendAllText(ext, " ");
    Check(Manifests.Choose<Catalog>(catalogJson, ext, c => c.Date, c => c.Format).Origin.Contains("firma válida"), "Manifiesto externo alterado: se ignora (firma no válida)");
    File.WriteAllText(ext, JsonSerializer.Serialize(catalog with { Date = "2099-01-01", Format = 9 }));
    File.WriteAllText(ext + ".firma", Manifests.Sign(File.ReadAllBytes(ext), File.ReadAllText(privateKey)));
    Check(Manifests.Choose<Catalog>(catalogJson, ext, c => c.Date, c => c.Format).Origin.Contains("más nueva de Setup"), "Manifiesto de un formato que esta versión no entiende: se ignora");
}
Check(Snap(reference).SequenceEqual(referenceBefore), "El paquete de referencia no se modificó");
Console.WriteLine(fails == 0 ? $"ILION SETUP: {count}/{count} CORRECTO" : $"ILION SETUP: {fails} FALLOS de {count}");
return fails == 0 ? 0 : 1;

/// <summary>Descarga real, pero cambia un byte de cada manifiesto (no de su firma): simula una manipulación por el camino.</summary>
internal sealed class Tamper(HttpClient real) : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        var bytes = await real.GetByteArrayAsync(request.RequestUri, token);
        if (request.RequestUri!.AbsolutePath.EndsWith(".json")) bytes[bytes.Length / 2] ^= 0x01;
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
    }
}

internal sealed class NoNetwork : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => throw new HttpRequestException("Sin conexión (prueba).");
}
internal sealed class Sync<T>(Action<T> report) : IProgress<T> { public void Report(T value) { lock (this) report(value); } }
