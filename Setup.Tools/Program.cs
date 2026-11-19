// Herramientas de quien publica (no van con Setup).
//   claves <privada.pem> <publica.txt>                      crea el par ECDSA P-256 (no sobrescribe una clave existente)
//   firmar <manifiesto.json> <privada.pem>                   escribe <manifiesto.json>.firma (antes valida si es un catálogo)
//   validar <catalogo.json>                                  comprueba un catálogo antes de firmarlo (formato 1 = el que lee la 0.3.x)
//   comparar <catalogo.json> <id> <versión> <paquete> [carpeta]
//       descarga la versión aprobada y la nueva de un mod, y comprueba que la nueva encaja con su receta: archivos, rutas,
//       64 bits, firmante y funciones exportadas. Imprime la huella SHA-256 nueva para el catálogo. No toca ningún juego.
#pragma warning disable CA1416
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Ilion.Client;
using Ilion.Setup;

return args.ElementAtOrDefault(0) switch
{
    "claves" => Keys(args[1], args[2]),
    "firmar" => SignFile(args[1], args[2]),
    "validar" => ValidateCatalog(args[1], out _) ? 0 : 1,
    "comparar" => await Compare(args[1], args[2], args[3], args[4], args.ElementAtOrDefault(5)),
    _ => Usage(),
};

static int Usage()
{
    Console.WriteLine("Uso: claves <privada.pem> <publica.txt> | firmar <manifiesto.json> <privada.pem> | validar <catalogo.json> | comparar <catalogo.json> <id> <versión> <paquete> [carpeta]");
    return 2;
}

static int Keys(string privateFile, string publicFile)
{
    if (File.Exists(privateFile)) { Console.WriteLine("Ya existe una clave privada: no se sobrescribe."); return 1; }
    using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    File.WriteAllText(privateFile, key.ExportECPrivateKeyPem());
    File.WriteAllText(publicFile, Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
    Console.WriteLine("Clave creada."); return 0;
}

static int SignFile(string file, string privateKey)
{
    // Un catálogo con errores no se firma: un Setup lo rechazaría o, peor, la 0.3.x no arrancaría
    if (Path.GetFileName(file).StartsWith("catalogo", StringComparison.OrdinalIgnoreCase) && !ValidateCatalog(file, out _))
    { Console.WriteLine("No se firma: corrige los errores del catálogo."); return 1; }
    File.WriteAllText(file + ".firma", Manifests.Sign(File.ReadAllBytes(file), File.ReadAllText(privateKey)));
    Console.WriteLine("Firmado: " + file + ".firma"); return 0;
}

/// <summary>Recetas integradas en ILION Setup 0.3.x: un catálogo de formato 1 (el que leen) solo puede nombrar estas. Un nombre
/// desconocido hace que la 0.3.x falle al arrancar, así que nunca se añaden aquí sin publicar antes el Setup que las trae.</summary>
static string[] RecipesIn03x() =>
[
    "XeMFG XMX", "DLSSG-XeFG (FakeMichau)", "Apex Neural Rendering FG (ANR)", "MFG-Unlocker", "DLSS for SM86", "Multi Frame Generation Enabler (Artur)",
    "Nukem · dlssg-to-fsr3", "HelixSR 1.9 (GPU antiguas)", "HLX FSR 4 (HelixSR 3.3)", "FG Lite (HelixSR 2.0.0)", "Runtime NR de danielblnc", "Runtime NR de lmxxf",
];

static bool ValidateCatalog(string file, out Catalog? catalog)
{
    catalog = null;
    var problems = new List<string>(); var notes = new List<string>();
    try { catalog = Manifests.Parse<Catalog>(File.ReadAllText(file)); }
    catch (Exception e) { Console.WriteLine($"[ERROR] No se puede leer: {e.Message}"); return false; }
    if (catalog.Format is not (1 or 2)) problems.Add($"formato {catalog.Format}: debe ser 1 (Setup 0.3.x) o 2 (Setup 0.4+)");
    if (catalog.Format == 1 && Path.GetFileName(file).Equals("catalogo-2.json", StringComparison.OrdinalIgnoreCase)) problems.Add("catalogo-2.json debe ser de formato 2");
    if (catalog.Format == 2 && Path.GetFileName(file).Equals("catalogo.json", StringComparison.OrdinalIgnoreCase)) problems.Add("catalogo.json lo lee la 0.3.x: debe seguir en formato 1");
    if (!Regex.IsMatch(catalog.Date, @"^\d{4}-\d{2}-\d{2}")) problems.Add($"fecha «{catalog.Date}»: debe empezar por AAAA-MM-DD (se compara como texto)");
    foreach (var dup in catalog.Mods.GroupBy(m => m.Id).Where(g => g.Count() > 1)) problems.Add($"id repetido «{dup.Key}»");
    if (catalog.Texts is { } texts)
    {
        if (catalog.Format == 1) notes.Add("«textos» solo lo usa Setup 0.4+: en catalogo.json no hace falta.");
        foreach (var (es, values) in texts.Where(x => x.Value.Length != 3 || x.Value.Any(string.IsNullOrWhiteSpace)))
            problems.Add($"textos: «{es}» debe tener 3 traducciones (portugués, ruso, inglés)");
    }
    var legacy = RecipesIn03x();
    foreach (var m in catalog.Mods)
    {
        var who = $"{m.Id} ({m.Name})";
        if (catalog.Format == 1)
        {
            if (m.Definition is not null) problems.Add($"{who}: «definicion» solo vale en el catálogo 2 (la 0.3.x no la entiende)");
            if (m.Recipe is { } r && !legacy.Contains(r)) problems.Add($"{who}: la receta «{r}» no existe en la 0.3.x: haría que no arrancara");
        }
        else
        {
            if (m.Definition is { } d && Recipes.Validate(m, d) is { } bad) problems.Add($"{who}: {bad}");
            if (m.Definition is null && m.Recipe is { } r && !ModInstaller.Known.Any(k => k.Name == r))
                (m.MinSetup is null ? problems : notes).Add($"{who}: la receta «{r}» no existe en este Setup" + (m.MinSetup is null ? "; añade «setupMinimo»" : $" (setupMinimo {m.MinSetup})"));
        }
        if (m.Recipe is not null || m.Definition is not null)
        {
            if (m.Version is not { } v || !Regex.IsMatch(v, @"^[\w.\-]{1,40}$")) problems.Add($"{who}: versión aprobada no válida");
            if (m.Sha256 is not { } s || !Regex.IsMatch(s, "^[0-9a-fA-F]{64}$")) problems.Add($"{who}: falta la huella SHA-256");
            if (m.Asset is not { } a || !Regex.IsMatch(a, @"^[\w.\-]{1,120}$")) problems.Add($"{who}: falta el nombre exacto del paquete");
        }
    }
    // Regla de publicación: Setup 0.4 usa el más nuevo de los dos catálogos; el 2 debe ir siempre igual o por delante del de la 0.3.x
    var sibling = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(file))!, catalog.Format == 2 ? "catalogo.json" : "catalogo-2.json");
    if (File.Exists(sibling))
        try
        {
            var other = Manifests.Parse<Catalog>(File.ReadAllText(sibling));
            var (v1, v2) = catalog.Format == 2 ? (other, catalog) : (catalog, other);
            if (string.CompareOrdinal(v1.Date, v2.Date) > 0)
                notes.Add($"catalogo.json ({v1.Date}) es más nuevo que catalogo-2.json ({v2.Date}): Setup 0.4 usaría el de la 0.3.x y no vería las recetas por datos. Publica también un catalogo-2.json con fecha igual o posterior.");
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException or InvalidDataException) { notes.Add($"No se pudo leer {Path.GetFileName(sibling)} para comparar fechas."); }
    foreach (var n in notes) Console.WriteLine($"[AVISO] {n}");
    foreach (var p in problems) Console.WriteLine($"[ERROR] {p}");
    Console.WriteLine(problems.Count == 0 ? $"Catálogo válido: formato {catalog.Format}, {catalog.Mods.Length} entradas, fecha {catalog.Date}." : $"{problems.Count} errores.");
    return problems.Count == 0;
}

static async Task<int> Compare(string catalogFile, string id, string newTag, string newAsset, string? folder)
{
    if (!ValidateCatalog(catalogFile, out var catalog) || catalog is null) return 1;
    var entry = catalog.Mods.FirstOrDefault(m => m.Id == id);
    if (entry is null || SetupEngine.RecipeFor(entry) is not { } approved) { Console.WriteLine($"[ERROR] «{id}» no es un mod instalable del catálogo."); return 1; }
    if (!Regex.IsMatch(newTag, @"^[\w.\-]{1,40}$") || !Regex.IsMatch(newAsset, @"^[\w.\-]{1,120}\.(zip|7z|rar)$", RegexOptions.IgnoreCase)) { Console.WriteLine("[ERROR] Versión o paquete no válidos."); return 1; }
    var work = Path.GetFullPath(folder ?? Path.Combine(Path.GetTempPath(), "ilion-comparar"));
    Directory.CreateDirectory(work);
    using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
    http.DefaultRequestHeaders.UserAgent.ParseAdd("ILION-Setup-Tools/0.4");
    var installer = new ModInstaller(http, work, new OperationEngine(Path.Combine(work, "sin-uso")));

    Console.WriteLine($"Aprobada: {approved.Tag} ({approved.ApprovedAsset})");
    var oldDownload = await installer.DownloadAsync(approved, CancellationToken.None);
    // La nueva: descarga directa de la publicación del autor; su huella se calcula aquí y es la que irá al catálogo
    var newFolder = Path.Combine(work, "nueva", newTag);
    Directory.CreateDirectory(newFolder);
    var package = Path.Combine(newFolder, "paquete" + Path.GetExtension(newAsset).ToLowerInvariant());
    var url = $"https://github.com/{approved.Repository}/releases/download/{Uri.EscapeDataString(newTag)}/{Uri.EscapeDataString(newAsset)}";
    Console.WriteLine($"Nueva: {newTag} ({newAsset}) desde {url}");
    await using (var net = await http.GetStreamAsync(url)) await using (var file = File.Create(package)) await net.CopyToAsync(file);
    var sha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(package))).ToLowerInvariant();
    var newDownload = ModInstaller.Extract(package, newFolder, newTag);
    var candidate = approved with { Tag = newTag, ApprovedAsset = newAsset, ApprovedSha256 = sha };
    if (approved.Build is not null) Console.WriteLine("[AVISO] Esta receta se construye con el setup de su autor (HelixSR): compruébala también instalándola en el laboratorio.");

    Dictionary<string, string> Planned(InstallableMod mod, ModDownload d)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in d.Files) if (mod.Map(f, d.Tag) is { } to) map.TryAdd(to, Path.Combine(d.Folder, f));
        return map;
    }
    var before = Planned(approved, oldDownload);
    var after = Planned(candidate, newDownload);
    var problems = new List<string>(); var notes = new List<string>();

    // 1) La receta encaja: todo lo imprescindible sale del paquete nuevo, en su sitio
    var missing = candidate.Required.Select(r => r.Replace("{tag}", newTag)).Where(r => !after.ContainsKey(r)).ToArray();
    if (missing.Length > 0) problems.Add("El paquete nuevo no trae lo imprescindible de la receta: " + string.Join(", ", missing) + ". Hay que adaptar la receta (catálogo 2) antes de aprobarlo.");
    if (candidate.ProxyNames.Length > 0)
        foreach (var name in candidate.ProxyNames)
            if (!File.Exists(Path.Combine(newDownload.Folder, candidate.ProxySource!(name).Replace('/', Path.DirectorySeparatorChar)))) problems.Add($"El paquete nuevo no trae la compilación para el nombre de carga {name}.");

    // 2) Qué cambia en la carpeta del juego
    static string Norm(string path, string tag) => path.Replace(tag, "{tag}", StringComparison.OrdinalIgnoreCase);
    var oldKeys = before.Keys.ToDictionary(k => Norm(k, approved.Tag!), k => k, StringComparer.OrdinalIgnoreCase);
    var newKeys = after.Keys.ToDictionary(k => Norm(k, newTag), k => k, StringComparer.OrdinalIgnoreCase);
    var added = newKeys.Keys.Except(oldKeys.Keys, StringComparer.OrdinalIgnoreCase).ToArray();
    var removed = oldKeys.Keys.Except(newKeys.Keys, StringComparer.OrdinalIgnoreCase).ToArray();
    var common = newKeys.Keys.Intersect(oldKeys.Keys, StringComparer.OrdinalIgnoreCase).ToArray();
    if (added.Length > 0) notes.Add($"Archivos nuevos ({added.Length}): {string.Join(", ", added.Take(12))}{(added.Length > 12 ? "…" : "")}");
    if (removed.Length > 0) notes.Add($"Ya no vienen ({removed.Length}; Setup los retirará al actualizar si el usuario no los cambió): {string.Join(", ", removed.Take(12))}{(removed.Length > 12 ? "…" : "")}");
    var changed = common.Count(k => Sha(before[oldKeys[k]]) != Sha(after[newKeys[k]]));
    notes.Add($"Archivos que siguen ({common.Length}); con contenido distinto: {changed}.");

    // 3) Las DLL siguen siendo de 64 bits, del mismo firmante y con las mismas funciones exportadas
    foreach (var k in newKeys.Keys.Where(k => k.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || k.EndsWith(".asi", StringComparison.OrdinalIgnoreCase)))
    {
        var path = after[newKeys[k]];
        var (machine, exports) = Pe(path);
        if (machine is not null && machine != Machine.Amd64) problems.Add($"{k}: no es de 64 bits ({machine}).");
        if (!oldKeys.TryGetValue(k, out var oldKey)) continue;
        var oldPath = before[oldKey];
        var (_, oldExports) = Pe(oldPath);
        var lost = oldExports.Except(exports).ToArray();
        if (lost.Length > 0) problems.Add($"{k}: ya no exporta {string.Join(", ", lost.Take(8))} (ILION u OptiScaler pueden usarlas).");
        var oldSigner = ComponentClassifier.SignerOf(oldPath); var newSigner = ComponentClassifier.SignerOf(path);
        if (oldSigner is not null && oldSigner != newSigner) problems.Add($"{k}: el firmante cambió ({oldSigner} → {newSigner ?? "sin firma"}).");
    }

    foreach (var n in notes) Console.WriteLine("  · " + n);
    foreach (var p in problems) Console.WriteLine("[PROBLEMA] " + p);
    Console.WriteLine();
    Console.WriteLine(problems.Count == 0
        ? "Encaja con la receta. Antes de aprobar: instálalo en un juego del laboratorio y comprueba en el registro de ILION que lo carga."
        : "NO se puede aprobar tal cual: revisa los problemas.");
    Console.WriteLine($"Para el catálogo: \"version\": \"{newTag}\", \"sha256\": \"{sha}\", \"paquete\": \"{newAsset}\"");
    return problems.Count == 0 ? 0 : 1;
}

static string Sha(string path) { using var s = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(s)); }

/// <summary>Arquitectura y nombres exportados de una DLL (sin cargarla).</summary>
static (Machine? Machine, string[] Exports) Pe(string path)
{
    try
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var machine = pe.PEHeaders.CoffHeader.Machine;
        var dir = pe.PEHeaders.PEHeader?.ExportTableDirectory ?? default;
        if (dir.RelativeVirtualAddress == 0) return (machine, []);
        byte[] Bytes(int rva, int length) { var block = pe.GetSectionData(rva); return block.GetContent(0, Math.Min(block.Length, length)).ToArray(); }
        var table = Bytes(dir.RelativeVirtualAddress, 40);
        if (table.Length < 40) return (machine, []);
        var count = Math.Clamp(BitConverter.ToInt32(table, 24), 0, 20000);
        var pointers = Bytes(BitConverter.ToInt32(table, 32), count * 4);
        var names = new List<string>();
        for (var i = 0; i + 4 <= pointers.Length; i += 4)
        {
            var raw = Bytes(BitConverter.ToInt32(pointers, i), 256);
            var end = Array.IndexOf(raw, (byte)0);
            if (end > 0) names.Add(System.Text.Encoding.ASCII.GetString(raw, 0, end));
        }
        return (machine, [.. names]);
    }
    catch (Exception e) when (e is BadImageFormatException or IOException or ArgumentOutOfRangeException or ArgumentException) { return (null, []); }
}
