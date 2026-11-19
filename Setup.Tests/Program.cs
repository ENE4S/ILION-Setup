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

// Diagnóstico: Setup.Tests <lab> <paquete> --reparar <carpeta-de-datos> → instala como un usuario, cambia archivos de la base y prueba «Sustituir lo distinto»
if (Array.IndexOf(args, "--reparar") is var ri and > 0)
{
    var rData = Path.GetFullPath(args[ri + 1]);
    var rEngine = new SetupEngine(rData, new HttpClient(), baseManifest, catalog);
    var rRoot = Path.Combine(lab, "JuegoReparar");
    if (Directory.Exists(rRoot)) { foreach (var f in Directory.EnumerateFiles(rRoot, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal); Directory.Delete(rRoot, true); }
    var rDir = Directory.CreateDirectory(Path.Combine(rRoot, "bin", "x64")).FullName;
    var rExe = Path.Combine(rDir, "JuegoReparar.exe");
    File.Copy(Path.Combine(reference, "OptiScaler", "plugins", "OptiPatcher.asi"), rExe);
    var rManual = new ManualSource(Path.Combine(rData, "manual-reparar.json")); rManual.Add(rExe);
    var rGame = rManual.Scan().Games.Single(x => x.Executable == rExe);
    var rArch = Gpu.Load(File.OpenRead(Path.Combine(fuente, "Setup", "Assets", "arquitecturas.json"))).Arquitecturas.Single(a => a.Id == "rdna3");
    var inst = await rEngine.NewInstallAsync(rGame, rDir, rEngine.RemoteEngine, "dxgi.dll", new HashSet<string> { "streamline", "variantes" }, [], rArch, [reference], null, default);
    Console.WriteLine($"Instalación: {(inst.Error ?? "correcta")}");
    // Diferencias típicas: un plugin de otra versión, una variante de FSR 4 cambiada, un archivo de Streamline y uno de la base
    foreach (var rel in new[] { @"OptiScaler\plugins\OptiPatcher.asi", @"ILION\Escaladores\FSR4\Lossy\amd_fidelityfx_upscaler_dx12.dll", @"OptiScaler\streamline\sl.common.dll", @"OptiScaler\libxess.dll" })
        File.AppendAllText(Path.Combine(rDir, rel), "x");
    File.Delete(Path.Combine(rDir, @"OptiScaler\libxell.dll"));
    if (args.Contains("--solo-lectura")) foreach (var rel in new[] { @"OptiScaler\libxess.dll", @"OptiScaler\streamline\sl.common.dll" })
        File.SetAttributes(Path.Combine(rDir, rel), FileAttributes.ReadOnly); // como los que salen de algunos .rar/.7z o de copias antiguas
    var before = rEngine.Verify(rGame, rDir, OptiScalerDetection.Inspect(rRoot, rExe));
    Console.WriteLine($"Antes: {before.Count(i => i.Area == "Base" && i.State == Level.Warn)} distintos, {before.Count(i => i.Area == "Base" && i.Detail == "Falta.")} faltan");
    foreach (var replace in new[] { false, true })
        try
        {
            var op = await rEngine.RepairBaseAsync(rGame, rDir, replace, [reference], null, default);
            Console.WriteLine($"Reparar (sustituir distinto: {replace}): {op?.Label ?? "nada que hacer"}");
        }
        catch (Exception e) { Console.WriteLine($"EXCEPCIÓN ({replace}): {e.GetType().FullName}: {e.Message}\n{e.StackTrace}"); }
    var after = rEngine.Verify(rGame, rDir, OptiScalerDetection.Inspect(rRoot, rExe));
    Console.WriteLine($"Después: {after.Count(i => i.Area == "Base" && i.State != Level.Ok)} sin corregir: {string.Join(", ", after.Where(i => i.Area == "Base" && i.State != Level.Ok).Select(i => i.What))}");
    return 0;
}

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
Check(picks.All(p => ModRequirements.Missing(p.Mod, exeDir).Length == 0), "Todos completos (XMX con su propia libxess_fg: prefiere la 1.3.4 firmada del paquete local)");
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
    Check(remoteNotes.All(n => n.Contains("firmado") || n.StartsWith("catalogo-2.json: aún no publicado")) && File.Exists(Path.Combine(remoteDir, "catalogo.json.firma")) && fromGitHub.Engine.Sha256 == baseManifest.Engine.Sha256,
        "Manifiestos firmados de GitHub (rama setup-manifiestos): descargados, firma válida, ILION " + fromGitHub.Engine.Version);
    var tamperedDir = Path.Combine(lab, "ManifiestosAlterados");
    if (Directory.Exists(tamperedDir)) Directory.Delete(tamperedDir, true);
    var tamperedNotes = await RemoteManifests.RefreshAsync(new HttpClient(new Tamper(http)), tamperedDir, default);
    Check(tamperedNotes.Where(n => !n.StartsWith("catalogo-2.json: aún no publicado")).All(n => n.Contains("firma no es válida")) && !File.Exists(Path.Combine(tamperedDir, "base-congelada.json")),
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

// ---------------------------------------------------------------- 10. ILION Setup 0.4: catálogo 2, recetas por datos, actualizaciones y API
// Un catálogo con una receta que este Setup no trae no lo tumba (en la 0.3.x lo cerraba): la entrada queda informativa y explica por qué
var unknown = catalog with { Mods = [.. catalog.Mods, catalog.Mods.Single(m => m.Id == "xmx") with { Id = "futuro", Name = "Mod del futuro", Recipe = "Receta que no existe" }] };
var tolerant = new SetupEngine(Path.Combine(lab, "DatosV04"), http, baseManifest, unknown);
var futureItem = tolerant.Resolve(rdna3, "DirectX 12", "1.4.1.2 beta").Single(i => i.Entry.Id == "futuro");
Check(SetupEngine.RecipeFor(unknown.Mods[^1]) is null && futureItem.Recipe is null && futureItem.Why?.Contains("actualiza ILION Setup") == true,
    "Catálogo con una receta desconocida: Setup arranca y la muestra como no instalable, con el motivo");
var needsNewer = catalog.Mods.Single(m => m.Id == "xmx") with { Id = "x2", MinSetup = "9.0" };
Check(SetupEngine.RecipeFor(needsNewer) is null && SetupEngine.Unusable(needsNewer)!.Contains("9.0"), "Entrada con «setupMinimo» más alto que este Setup: no se ofrece y lo dice");

// Receta por datos: la misma de Apex ANR que trae Setup, descrita en el catálogo 2
var anrEntry = catalog.Mods.Single(m => m.Id == "anr");
var anrData = new RecipeData("apex-compute/ANR", [new("Apex Neural Rendering FG/**", @"OptiScaler\custom\Apex Neural Rendering FG")],
    [@"OptiScaler\custom\Apex Neural Rendering FG\dlssg\nvngx_dlssg.dll", @"OptiScaler\custom\Apex Neural Rendering FG\dlssg\path_table.json"],
    @"OptiScaler\custom\Apex Neural Rendering FG\dlssg\nvngx_dlssg.dll", KeepIfPresent: [@"OptiScaler\custom\Apex Neural Rendering FG\dlssg\nr_fg.ini"],
    MinIlion: "1.4.1.1", Slot: "Implementación DLSS FG", Dx12Only: true);
var anrByData = anrEntry with { Recipe = null, Definition = anrData };
Check(SetupEngine.Unusable(anrByData) is null && SetupEngine.RecipeFor(anrByData) is { } dataRecipe && dataRecipe.Tag == anrEntry.Version && dataRecipe.ApprovedSha256 == anrEntry.Sha256,
    "Receta por datos válida: se acepta con la versión y la huella aprobadas");
string? Bad(RecipeData d) => Recipes.Validate(anrEntry, d);
Check(Bad(anrData with { Files = [new("x.dll", @"..\fuera.dll")] }) is not null && Bad(anrData with { Files = [new("x.exe", "x.exe")] }) is not null
      && Bad(anrData with { Files = [new("x.dll", "dxgi.dll")] }) is not null && Recipes.Validate(anrEntry with { Sha256 = null }, anrData) is not null
      && Bad(anrData with { Marker = "otro.dll" }) is not null && Bad(anrData with { Dependencies = ["no-existe"] }) is not null,
    "Recetas por datos peligrosas o incompletas se rechazan: fuera de la carpeta, ejecutables, archivos de ILION, sin huella, marcador ajeno, dependencia desconocida");

// API: DirectX 12 por el Agility SDK aunque el ejecutable no lo importe; en Unity no se adivina
var apiRoot = Path.Combine(lab, "JuegoApi");
if (Directory.Exists(apiRoot)) Directory.Delete(apiRoot, true);
Directory.CreateDirectory(Path.Combine(apiRoot, "D3D12"));
var apiExe = Path.Combine(apiRoot, "JuegoApi.exe");
File.Copy(Path.Combine(reference, "OptiScaler", "plugins", "OptiPatcher.asi"), apiExe);
File.WriteAllText(Path.Combine(apiRoot, "D3D12", "D3D12Core.dll"), "prueba");
var unityRoot = Path.Combine(lab, "JuegoUnity");
if (Directory.Exists(unityRoot)) Directory.Delete(unityRoot, true);
Directory.CreateDirectory(Path.Combine(unityRoot, "JuegoUnity_Data"));
var unityExe = Path.Combine(unityRoot, "JuegoUnity.exe");
File.WriteAllBytes(unityExe, [.. File.ReadAllBytes(apiExe), .. System.Text.Encoding.ASCII.GetBytes("d3d12.dll")]);
Check(ApiDetection.InspectGame(apiExe).Any(a => a.Name == "DirectX 12") && !ApiDetection.InspectGame(unityExe).Any(a => a.Name == "DirectX 12"),
    "API: DirectX 12 por el Agility SDK junto al ejecutable; en un juego Unity no se deduce de un texto del ejecutable");

if (!noNetwork)
{
    // Actualizar un mod: Apex ANR FG-V.B15 (aprobada) → FG-V.B19 (encaja con la receta: comprobado con Setup.Tools comparar)
    var upData = Path.Combine(lab, "DatosActualizar");
    if (Directory.Exists(upData)) Directory.Delete(upData, true);
    var upRoot = Path.Combine(lab, "JuegoActualizar");
    if (Directory.Exists(upRoot)) { foreach (var f in Directory.EnumerateFiles(upRoot, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal); Directory.Delete(upRoot, true); }
    var upDir = Directory.CreateDirectory(Path.Combine(upRoot, "bin")).FullName;
    var upExe = Path.Combine(upDir, "JuegoActualizar.exe");
    File.Copy(Path.Combine(reference, "OptiScaler", "plugins", "OptiPatcher.asi"), upExe);
    var upManual = new ManualSource(Path.Combine(upData, "manual.json")); upManual.Add(upExe);
    var upGame = upManual.Scan().Games.Single(x => x.Executable == upExe);
    var oldEngine = new SetupEngine(upData, http, baseManifest, catalog);
    var anrOld = SetupEngine.RecipeFor(anrEntry)!;
    var first = await ModBatch.RunAsync(new ModInstaller(http, upData, oldEngine.Operations), oldEngine.Operations, oldEngine.Updates, SetupEngine.GameId(upGame), upDir, [new BatchRequest(anrOld)], [], null, default);
    var dlssg = Path.Combine(upDir, @"OptiScaler\custom\Apex Neural Rendering FG\dlssg\nvngx_dlssg.dll");
    var shaB15 = File.Exists(dlssg) ? ComponentUpdateService.Sha256(dlssg) : "";
    File.WriteAllText(Path.Combine(upDir, @"OptiScaler\custom\Apex Neural Rendering FG\dlssg\nr_fg.ini"), "ajuste del usuario");
    var cleanB15 = Snap(upRoot);
    var b19 = catalog with { Mods = [.. catalog.Mods.Select(m => m.Id == "anr" ? m with { Version = "FG-V.B19", Sha256 = "70107373e0f9744875fcbb7f46133432202ce7f132fdfff14259b8a85fba3f13", Asset = "anr-fg-V.B19-addon.zip" } : m)] };
    var newEngine = new SetupEngine(upData, http, baseManifest, b19);
    var pending = newEngine.ModUpdates(upGame, upDir);
    Check(first.Installed.Length == 1 && oldEngine.ModUpdates(upGame, upDir).Length == 0 && pending.Length == 1 && pending[0].Installed == "FG-V.B15" && pending[0].Approved == "FG-V.B19",
        "Actualizaciones: con la versión aprobada no hay aviso; al aprobarse otra, el juego la ofrece (FG-V.B15 → FG-V.B19)");
    var up = await newEngine.UpdateModAsync(upGame, upDir, pending[0], [], null, default);
    Check(up.Ok && ComponentUpdateService.Sha256(dlssg) != shaB15 && newEngine.ModUpdates(upGame, upDir).Length == 0
          && File.ReadAllText(Path.Combine(upDir, @"OptiScaler\custom\Apex Neural Rendering FG\dlssg\nr_fg.ini")) == "ajuste del usuario",
        $"Actualizar: {up.Message} Sin aviso pendiente y el nr_fg.ini del usuario se conserva");
    ModBatch.RestoreBatch(newEngine.Operations, SetupEngine.GameId(upGame), up.Batch!.BatchId);
    Check(Snap(upRoot).SequenceEqual(cleanB15) && newEngine.ModUpdates(upGame, upDir).Length == 1, "Deshacer la actualización: vuelve la versión anterior byte a byte");
    var wrong = b19 with { Mods = [.. b19.Mods.Select(m => m.Id == "anr" ? m with { Sha256 = new string('0', 64) } : m)] };
    var wrongEngine = new SetupEngine(upData, http, baseManifest, wrong);
    var failedUp = await wrongEngine.UpdateModAsync(upGame, upDir, wrongEngine.ModUpdates(upGame, upDir).Single(), [], null, default);
    Check(!failedUp.Ok && Snap(upRoot).SequenceEqual(cleanB15), "Actualización con un paquete que no es el aprobado: no se instala y el juego queda igual");
    // Dos actualizaciones seguidas del mismo mod (FG-V.B15 → FG-V.B18 → FG-V.B19): la segunda parte de la primera, y deshacer la
    // segunda devuelve exactamente la intermedia
    Catalog WithAnr(string tag, string sha) => catalog with { Mods = [.. catalog.Mods.Select(m => m.Id == "anr" ? m with { Version = tag, Sha256 = sha, Asset = $"anr-fg-V.{tag[5..]}-addon.zip" } : m)] };
    var e18 = new SetupEngine(upData, http, baseManifest, WithAnr("FG-V.B18", "6dfce1ce2240f6ce8d820a7eb66b4d7d349655e9238c30df820297d70b9963f3"));
    var up18 = await e18.UpdateModAsync(upGame, upDir, e18.ModUpdates(upGame, upDir).Single(), [], null, default);
    var clean18 = Snap(upRoot);
    var e19 = new SetupEngine(upData, http, baseManifest, b19);
    var second = e19.ModUpdates(upGame, upDir);
    var up19 = second.Length == 1 ? await e19.UpdateModAsync(upGame, upDir, second[0], [], null, default) : null;
    var afterBoth = e19.ModUpdates(upGame, upDir).Length;
    if (up19?.Batch is { } b2) ModBatch.RestoreBatch(e19.Operations, SetupEngine.GameId(upGame), b2.BatchId);
    Check(up18.Ok && second.Length == 1 && second[0].Installed == "FG-V.B18" && up19 is { Ok: true } && afterBoth == 0 && Snap(upRoot).SequenceEqual(clean18),
        "Dos actualizaciones seguidas (B15 → B18 → B19): la segunda parte de la versión intermedia y deshacerla devuelve la B18 byte a byte");
    // La receta por datos produce exactamente el mismo plan que la integrada
    var dl = await new ModInstaller(http, upData, oldEngine.Operations).DownloadAsync(anrOld, default);
    var planBuiltIn = ModInstaller.Plan(anrOld, dl, upDir, null, updating: true).Select(i => i.Relative).Order().ToArray();
    var planData = ModInstaller.Plan(SetupEngine.RecipeFor(anrByData)!, dl, upDir, null, updating: true).Select(i => i.Relative).Order().ToArray();
    Check(planBuiltIn.Length > 0 && planBuiltIn.SequenceEqual(planData, StringComparer.OrdinalIgnoreCase), $"Receta por datos = receta integrada: mismo plan de {planData.Length} archivos");
    // Una receta por datos nunca escribe encima de ILION ni de su base congelada (aunque pase la validación de rutas)
    var intruder = anrByData with { Definition = anrData with { Files = [new("Apex Neural Rendering FG/dlssg/nvngx_dlssg.dll", @"OptiScaler\libxess.dll")],
        Required = [@"OptiScaler\libxess.dll"], Marker = @"OptiScaler\libxess.dll", KeepIfPresent = [] } };
    var refused = false;
    try { ModInstaller.Plan(SetupEngine.RecipeFor(intruder)!, dl, upDir, null, updating: true); }
    catch (InvalidDataException e) when (e.Message.Contains("de ILION o de su base")) { refused = true; }
    Check(SetupEngine.Unusable(intruder) is null && refused, "Receta por datos que escribiría un archivo de la base de ILION (OptiScaler\\libxess.dll): se rechaza antes de escribir");
}

// Catálogo: el más nuevo entre el integrado (formato 2) y los externos firmados (formato 1 de la 0.3.x y formato 2)
if (File.Exists(privateKey))
{
    var folder2 = Path.Combine(lab, "Manifiestos2");
    Directory.CreateDirectory(folder2);
    var pem = File.ReadAllText(privateKey);
    void Signed(string name, Catalog c) { var f = Path.Combine(folder2, name); File.WriteAllText(f, JsonSerializer.Serialize(c)); File.WriteAllText(f + ".firma", Manifests.Sign(File.ReadAllBytes(f), pem)); }
    var embedded2 = JsonSerializer.Serialize(catalog with { Format = 2 });
    Signed("catalogo.json", catalog with { Date = "2099-01-02" });
    Signed("catalogo-2.json", catalog with { Format = 2, Date = "2099-01-01" });
    var pick1 = Manifests.ChooseCatalog(embedded2, (Path.Combine(folder2, "catalogo-2.json"), "externo"), (Path.Combine(folder2, "catalogo.json"), "externo"));
    Signed("catalogo-2.json", catalog with { Format = 2, Date = "2099-01-03" });
    var pick2 = Manifests.ChooseCatalog(embedded2, (Path.Combine(folder2, "catalogo-2.json"), "externo"), (Path.Combine(folder2, "catalogo.json"), "externo"));
    File.AppendAllText(Path.Combine(folder2, "catalogo-2.json"), " ");
    var pick3 = Manifests.ChooseCatalog(embedded2, (Path.Combine(folder2, "catalogo-2.json"), "externo"), (Path.Combine(folder2, "catalogo.json"), "externo"));
    Check(pick1.Catalog.Date == "2099-01-02" && pick2.Catalog is { Date: "2099-01-03", Format: 2 } && pick3.Catalog.Date == "2099-01-02",
        "Catálogo: gana el más nuevo firmado (formato 1 o 2); uno alterado se ignora");
}
// XeMFG XMX: su propia libxess_fg de Intel (1.3.1 o 1.3.4, nunca 1.4 o más). Sin 1.3.4 en el PC usa la 1.3.1 de OptiScaler del propio
// juego; sin ninguna, la 1.3.1 del SDK oficial de Intel (v3.0.2). Prueba de laboratorio previa: XMX build-10 arranca con las dos en AMD
{
    var dep = ModInstaller.XmxXeFg;
    var v131 = Path.Combine(reference, "OptiScaler", "libxess_fg.dll");
    var v134 = Path.Combine(reference, "OptiScaler", "custom", "XeMFG XMX", "dlssg", "libxess_fg.dll");
    Check(ModRequirements.Problem(dep, v131) is null && ModRequirements.Problem(dep, v134) is null, "XMX acepta su libxess_fg 1.3.1 oficial y la 1.3.4");
    Check(ModRequirements.Problem(dep with { BelowVersion = "1.3.2" }, v134) is { } why && why.Contains("anterior a la 1.3.2"), "Una versión por encima del techo no se da por buena");
    var xRoot = Path.Combine(lab, "JuegoXmx");
    if (Directory.Exists(xRoot)) Directory.Delete(xRoot, true);
    var xDir = Path.Combine(xRoot, "bin");
    Directory.CreateDirectory(Path.Combine(xDir, "OptiScaler"));
    File.Copy(v131, Path.Combine(xDir, "OptiScaler", "libxess_fg.dll"));
    var xmxMod = ModInstaller.Known.Single(m => m.Name == "XeMFG XMX");
    var own = await DependencyResolver.PlanAsync(xmxMod, xDir, null, [], default);
    Check(own.Pending.Length == 0 && own.Items.Length == 1 && own.Items[0].Relative == dep.Relative
          && Path.GetFullPath(own.Items[0].Source) == Path.GetFullPath(Path.Combine(xDir, "OptiScaler", "libxess_fg.dll")),
        "XMX sin 1.3.4 en el PC: usa la 1.3.1 de OptiScaler del propio juego, sin descargar");
    var games = await DependencyResolver.PlanAsync(xmxMod, xDir, null, [reference], default);
    Check(games.Items.Length == 1 && ComponentUpdateService.VersionOf(games.Items[0].Source).StartsWith("1.3.4"), "XMX con una 1.3.4 firmada en el PC: la prefiere");
    if (!noNetwork)
    {
        File.Delete(Path.Combine(xDir, "OptiScaler", "libxess_fg.dll"));
        var official = await DependencyResolver.PlanAsync(xmxMod, xDir, engine.Updates, [], default);
        Check(official.Pending.Length == 0 && official.Items.Length == 1 && ComponentUpdateService.Sha256(official.Items[0].Source) == ComponentUpdateService.Sha256(v131),
            "XMX sin ninguna copia: descarga la 1.3.1 del SDK oficial de Intel v3.0.2 (idéntica a la de ILION) · " + string.Join(" | ", official.Sources));
    }
    Directory.Delete(xRoot, true);
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
