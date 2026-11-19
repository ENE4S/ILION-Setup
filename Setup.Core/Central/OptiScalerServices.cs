// ILION CENTRAL-01 0.5 — Servicios de OptiScaler: esquema de opciones derivado del INI, guardado con respaldo,
// identificación de edición e instalación/desinstalación con manifiesto de propiedad.
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SharpCompress.Archives;
using SharpCompress.Common;

namespace Ilion.Client;

// ------------------------------------------------------------------ Esquema
/// <summary>Opción de un desplegable. «Off» = se ve en gris y no se puede elegir (p. ej. no compatible con la gráfica elegida).</summary>
public sealed record IniChoice(string Value, string Label) { public bool Off { get; init; } public override string ToString() => Label; }
public sealed record IniOption(string Section, string Key, IniChoice[] Choices, string Help);

/// <summary>Deriva los valores válidos de cada clave de los comentarios que la propia edición escribe encima de ella.</summary>
public static class IniSchema
{
    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["auto"] = "Automático", ["true"] = "Activado", ["false"] = "Desactivado",
        ["xess"] = "XeSS", ["xess_12"] = "XeSS (vía DX12)", ["dlss"] = "DLSS", ["ffx"] = "FSR 3.1 / FSR 4", ["ffx_12"] = "FSR 3.1 / FSR 4 (vía DX12)",
        ["fsr21"] = "FSR 2.1", ["fsr22"] = "FSR 2.2", ["fsr31"] = "FSR 3.1", ["fsr21_12"] = "FSR 2.1 (vía DX12)", ["fsr22_12"] = "FSR 2.2 (vía DX12)",
        ["nofg"] = "Sin generación", ["dlssg"] = "DLSS Frame Generation", ["nvngxfg"] = "NVNGX FG", ["fsrfg"] = "FSR Frame Generation",
        ["upscaler"] = "Desde el escalador", ["fsrfg30"] = "FSR 3.0 Frame Generation", ["xefg"] = "Intel XeFG",
    };
    public static string Friendly(string value) => Names.TryGetValue(value, out var name) ? name : value;

    public static IReadOnlyDictionary<(string Section, string Key), IniOption> Parse(string text)
    {
        var result = new Dictionary<(string, string), IniOption>(new KeyComparer());
        var section = ""; var comments = new List<string>();
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) { comments.Clear(); continue; }
            if (line.StartsWith('[') && line.EndsWith(']')) { section = line[1..^1].Trim(); comments.Clear(); continue; }
            if (line.StartsWith(';') || line.StartsWith('#')) { comments.Add(line.TrimStart(';', '#').Trim()); continue; }
            var eq = line.IndexOf('=');
            if (eq > 0 && section.Length > 0)
            {
                var key = line[..eq].Trim();
                var choices = Choices(comments);
                if (choices.Length > 0) result[(section, key)] = new(section, key, choices, string.Join(" ", comments.Where(c => !c.StartsWith("---"))));
            }
            comments.Clear();
        }
        return result;
    }

    private static IniChoice[] Choices(List<string> comments)
    {
        var auto = new IniChoice("auto", "Automático");
        // 1) "1 = 2X | 2 = 3X" o "0 = Default | 1 = A"
        var pairs = comments.Where(c => Regex.IsMatch(c, @"^\d+\s*=")).SelectMany(c => c.Split('|'))
            .Select(part => Regex.Match(part.Trim(), @"^(\d+)\s*=\s*(.+)$")).Where(m => m.Success)
            .Select(m => new IniChoice(m.Groups[1].Value, m.Groups[2].Value.Trim())).DistinctBy(c => c.Value).ToArray();
        if (pairs.Length >= 2) return [auto, .. pairs];
        // 2) "xess, fsr21, ffx (FSR 2.3; 3.1), dlss - Default (auto) is ..."
        foreach (var comment in comments)
        {
            var body = Regex.Replace(comment, @"\([^)]*\)", "");
            var cut = body.IndexOf(" - ", StringComparison.Ordinal);
            if (cut >= 0) body = body[..cut];
            var tokens = body.Split(',').Select(t => t.Trim()).ToArray();
            if (tokens.Length >= 2 && tokens.All(t => Regex.IsMatch(t, @"^[a-z][a-z0-9_]*$")))
                return [auto, .. tokens.Select(t => new IniChoice(t, Friendly(t)))];
        }
        // 3) "From 0 to 5"
        foreach (var comment in comments)
            if (Regex.Match(comment, @"From (-?\d+) to (-?\d+)") is { Success: true } range &&
                int.Parse(range.Groups[2].Value) - int.Parse(range.Groups[1].Value) is >= 1 and <= 16)
                return [auto, .. Enumerable.Range(int.Parse(range.Groups[1].Value), int.Parse(range.Groups[2].Value) - int.Parse(range.Groups[1].Value) + 1)
                    .Select(v => new IniChoice(v.ToString(), v.ToString()))];
        // 4) "true or false"
        if (comments.Any(c => c.StartsWith("true or false", StringComparison.OrdinalIgnoreCase)))
            return [auto, new("true", "Activado"), new("false", "Desactivado")];
        return [];
    }

    private sealed class KeyComparer : IEqualityComparer<(string, string)>
    {
        public bool Equals((string, string) a, (string, string) b) =>
            string.Equals(a.Item1, b.Item1, StringComparison.OrdinalIgnoreCase) && string.Equals(a.Item2, b.Item2, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((string, string) v) => HashCode.Combine(v.Item1.ToLowerInvariant(), v.Item2.ToLowerInvariant());
    }
}

// ------------------------------------------------------------------ Guardado del INI
public sealed record IniBackup(string Id, string IniPath, string BackupFile, string OriginalSha256, string SavedSha256, DateTimeOffset When, bool Restored);

public sealed class IniConfigStore(string dataDirectory)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>Leer → validar → respaldar → modificar sólo las claves pedidas → verificar → sustituir.</summary>
    public IniBackup Save(string gameId, string iniPath, IReadOnlyDictionary<(string Section, string Key), string> changes)
    {
        var original = File.ReadAllText(iniPath);
        var before = IniDocument.Parse(original).Settings;
        var document = IniDocument.Parse(original);
        foreach (var ((section, key), value) in changes) document.Set(section, key, value);
        var after = IniDocument.Parse(document.Text).Settings;
        foreach (var ((section, key), value) in changes)
            if (!after.Any(s => Same(s.Section, section) && Same(s.Key, key) && s.Value == value))
                throw new InvalidDataException($"No se pudo fijar {key} en [{section}].");
        var untouched = before.Where(s => !changes.Keys.Any(k => Same(k.Section, s.Section) && Same(k.Key, s.Key))).ToArray();
        if (untouched.Any(s => !after.Contains(s))) throw new InvalidDataException("La escritura alteraría claves ajenas. Cancelado.");

        var id = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6];
        var folder = Path.Combine(dataDirectory, "Backups", Safe(gameId), "ini-" + id);
        Directory.CreateDirectory(folder);
        var backup = Path.Combine(folder, "OptiScaler.ini");
        var originalSha = ComponentUpdateService.Sha256(iniPath);
        File.Copy(iniPath, backup, overwrite: true); // copia exacta de bytes
        if (ComponentUpdateService.Sha256(backup) != originalSha) throw new IOException("El respaldo del INI no es idéntico.");

        var staging = iniPath + ".ilion-new";
        File.WriteAllText(staging, document.Text, new UTF8Encoding(false));
        var savedSha = ComponentUpdateService.Sha256(staging);
        var manifest = new IniBackup(id, Path.GetFullPath(iniPath), backup, originalSha, savedSha, DateTimeOffset.Now, false);
        File.WriteAllText(Path.Combine(folder, "manifest.json"), JsonSerializer.Serialize(manifest, Json));
        File.Move(staging, iniPath, overwrite: true);
        if (ComponentUpdateService.Sha256(iniPath) != savedSha) throw new IOException("Verificación posterior del INI fallida. Usa Restaurar.");
        return manifest;
    }

    public IniBackup? Latest(string gameId, string iniPath) => All(gameId)
        .FirstOrDefault(b => !b.Restored && b.IniPath.Equals(Path.GetFullPath(iniPath), StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<IniBackup> All(string gameId)
    {
        var folder = Path.Combine(dataDirectory, "Backups", Safe(gameId));
        if (!Directory.Exists(folder)) return [];
        return Directory.EnumerateDirectories(folder, "ini-*").Select(d => Path.Combine(d, "manifest.json")).Where(File.Exists)
            .Select(f => JsonSerializer.Deserialize<IniBackup>(File.ReadAllText(f))!).OrderByDescending(b => b.When).ToArray();
    }

    public IniBackup Restore(IniBackup backup)
    {
        if (ComponentUpdateService.Sha256(backup.BackupFile) != backup.OriginalSha256) throw new IOException("El respaldo del INI está dañado.");
        var current = File.Exists(backup.IniPath) ? ComponentUpdateService.Sha256(backup.IniPath) : null;
        if (current != backup.SavedSha256 && current != backup.OriginalSha256)
            throw new InvalidOperationException("El INI cambió después de guardarlo (otro programa o el menú de OptiScaler). Restauración bloqueada para no perder esos cambios.");
        File.Copy(backup.BackupFile, backup.IniPath, overwrite: true);
        if (ComponentUpdateService.Sha256(backup.IniPath) != backup.OriginalSha256) throw new IOException("Verificación de la restauración fallida.");
        var done = backup with { Restored = true };
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(backup.BackupFile)!, "manifest.json"), JsonSerializer.Serialize(done, Json));
        return done;
    }

    private static bool Same(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);
    public static string Safe(string value) => string.Concat(value.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_'));
}

// ------------------------------------------------------------------ Paquetes e instalación
public enum OptiEdition { Unknown, Ilion, Official }
public sealed record OptiPackage(string Folder, string MainBinary, OptiEdition Edition, string Version, string Origin, string? Sha256Verified);
public sealed record OwnedFile(string RelativePath, string Sha256);
public sealed record OptiInstallManifest(string GameId, string Directory, string Proxy, OptiEdition Edition, string Version, string Origin,
    OwnedFile[] Files, string[] CreatedDirectories, string[] Skipped, DateTimeOffset When, bool Adopted = false);

public sealed partial class OptiScalerInstaller(HttpClient http, string dataDirectory)
{
    public static readonly string[] Proxies = ["dxgi.dll", "winmm.dll", "version.dll", "dbghelp.dll", "d3d12.dll", "wininet.dll", "winhttp.dll"];
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly Regex Excluded = new(@"(^|[\\/])(setup_.*\.(bat|sh)|.*\.(md|txt|log)|OptiScaler\.log)$", RegexOptions.IgnoreCase);

    private readonly OperationEngine operations = new(dataDirectory);
    private string DataDir => dataDirectory;
    private HttpClient HttpClient => http;

    /// <summary>Versión pública de ILION escrita en el binario («ILION x.y.z NR experimental» o la cadena que sigue a la de compilación).</summary>
    public static string? PublicVersion(string binary)
    {
        try
        {
            var text = System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(binary));
            var m = Regex.Match(text, @"ILION (\d+\.\d+[0-9A-Za-z.\-]*) NR experimental");
            if (m.Success) return m.Groups[1].Value;
            m = Regex.Match(text, @"\(\d{8}_\d{6}\)\x00+(\d+\.\d+\.\d+[0-9A-Za-z.\-]*)\x00");
            if (m.Success) return m.Groups[1].Value;
            // ILION Setup: la serie 1.4.1.x escribe su versión en la versión de archivo («1.4.1.2 beta …»): solo número y fase, sin etiquetas
            var file = FileVersionInfo.GetVersionInfo(binary).FileVersion?.Trim() ?? "";
            m = Regex.Match(file, @"^(\d+(?:\.\d+){1,3})(?:\s+(beta|alpha|rc|exp)\b)?", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value + (m.Groups[2].Success ? " " + m.Groups[2].Value.ToLowerInvariant() : "") : null;
        }
        catch (IOException) { return null; }
    }

    /// <summary>Texto de versión para la interfaz: pública para ILION, de producto para la oficial; nunca se inventa.</summary>
    public static string DisplayVersion(string binary, OptiEdition edition) => edition == OptiEdition.Ilion
        ? PublicVersion(binary) ?? "Versión no identificada"
        : VersionUtil.Display(FileVersionInfo.GetVersionInfo(binary).ProductVersion ?? FileVersionInfo.GetVersionInfo(binary).FileVersion);

    /// <summary>«OptiScaler ILION 1.4.0» · «ILION · Versión no identificada» · «OptiScaler oficial 0.9.4».</summary>
    public static string Title(string binary, OptiEdition edition)
    {
        if (edition == OptiEdition.Ilion) return PublicVersion(binary) is { } v ? $"OptiScaler ILION {v}" : "ILION · Versión no identificada";
        return $"{EditionName(edition)} {DisplayVersion(binary, edition)}";
    }

    public static OptiEdition EditionOf(string binary)
    {
        try
        {
            var bytes = File.ReadAllBytes(binary);
            // Marcas propias de ILION: «IlionPalette» (1.2+) y el título «OptiScaler - ILION» (también en 1.1)
            return bytes.AsSpan().IndexOf("IlionPalette"u8) >= 0 || bytes.AsSpan().IndexOf("OptiScaler - ILION"u8) >= 0 ? OptiEdition.Ilion :
                FileVersionInfo.GetVersionInfo(binary).ProductName?.Contains("OptiScaler", StringComparison.OrdinalIgnoreCase) == true ? OptiEdition.Official : OptiEdition.Unknown;
        }
        catch (IOException) { return OptiEdition.Unknown; }
    }

    public static string EditionName(OptiEdition edition) => edition switch
    { OptiEdition.Ilion => "OptiScaler ILION", OptiEdition.Official => "OptiScaler oficial", _ => "Edición sin identificar" };

    /// <summary>Paquete local (carpeta) — se usa para la edición ILION.</summary>
    public static OptiPackage InspectFolder(string folder, string origin)
    {
        var main = new[] { "OptiScaler.dll", "dxgi.dll", "winmm.dll", "version.dll" }.Select(n => Path.Combine(folder, n)).FirstOrDefault(IsOptiBinary)
            ?? throw new InvalidDataException("La carpeta no contiene un binario de OptiScaler x64 (OptiScaler.dll o dxgi.dll).");
        var edition = EditionOf(main);
        return new(folder, main, edition, DisplayVersion(main, edition), origin, null);
    }

    /// <summary>Última publicación oficial: .7z con SHA-256 publicado por GitHub → verificar → extraer.</summary>
    public async Task<OptiPackage> PrepareOfficialAsync(IProgress<string>? progress, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/optiscaler/OptiScaler/releases?per_page=10");
        request.Headers.UserAgent.ParseAdd("ILION-Setup/" + Ilion.Setup.SetupEngine.SetupVersion);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await http.SendAsync(request, token);
        response.EnsureSuccessStatusCode();
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(token), cancellationToken: token);
        foreach (var release in json.RootElement.EnumerateArray().Where(r => !r.GetProperty("draft").GetBoolean() && !r.GetProperty("prerelease").GetBoolean()))
            foreach (var asset in release.GetProperty("assets").EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString()!;
                if (!Regex.IsMatch(name, @"^Optiscaler_[0-9A-Za-z._-]+\.7z$", RegexOptions.IgnoreCase)) continue;
                var digest = asset.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
                if (digest is null || !digest.StartsWith("sha256:")) throw new InvalidOperationException("La publicación oficial no tiene SHA-256 publicado; no se descarga.");
                var tag = release.GetProperty("tag_name").GetString()!;
                var url = new Uri(asset.GetProperty("browser_download_url").GetString()!);
                if (url.Host != "github.com" || !url.AbsolutePath.StartsWith("/optiscaler/OptiScaler/releases/download/", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("URL fuera del repositorio oficial.");
                var cache = Path.Combine(dataDirectory, "Packages", "OptiScaler", tag);
                Directory.CreateDirectory(cache);
                var archive = Path.Combine(cache, name);
                var sha = digest[7..].ToLowerInvariant();
                if (!File.Exists(archive) || ComponentUpdateService.Sha256(archive) != sha)
                {
                    progress?.Report($"Descargando OptiScaler {tag}…");
                    var part = archive + ".part";
                    using (var download = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token))
                    {
                        download.EnsureSuccessStatusCode();
                        await using var output = File.Create(part);
                        await (await download.Content.ReadAsStreamAsync(token)).CopyToAsync(output, token);
                    }
                    if (ComponentUpdateService.Sha256(part) != sha) { File.Delete(part); throw new IOException("El paquete descargado no coincide con el SHA-256 publicado."); }
                    File.Move(part, archive, overwrite: true);
                }
                progress?.Report("Extrayendo paquete verificado…");
                var folder = Path.Combine(cache, "extraido");
                if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
                Directory.CreateDirectory(folder);
                var root = Path.GetFullPath(folder) + Path.DirectorySeparatorChar;
                using (var open = ArchiveFactory.OpenArchive(archive))
                    foreach (var entry in open.Entries.Where(e => !e.IsDirectory))
                    {
                        var target = Path.GetFullPath(Path.Combine(folder, entry.Key!));
                        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Entrada con ruta insegura en el paquete.");
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        entry.WriteToFile(target, new ExtractionOptions { Overwrite = true });
                    }
                // Algunos paquetes traen una carpeta raíz: bajar un nivel si hace falta.
                var packageRoot = File.Exists(Path.Combine(folder, "OptiScaler.dll")) ? folder
                    : Directory.EnumerateDirectories(folder).FirstOrDefault(sub => File.Exists(Path.Combine(sub, "OptiScaler.dll"))) ?? folder;
                var package = InspectFolder(packageRoot, $"github.com/optiscaler/OptiScaler {tag}");
                return package with { Sha256Verified = sha, Version = tag.TrimStart('v') };
            }
        throw new InvalidOperationException("No se encontró una publicación oficial estable con paquete .7z.");
    }

    private OptiInstallManifest? InstalledRaw(string gameId)
    {
        var raw = ManifestPath(gameId);
        return File.Exists(raw) ? JsonSerializer.Deserialize<OptiInstallManifest>(File.ReadAllText(raw)) : null;
    }

    public OptiInstallManifest? Installed(string gameId)
    {
        var file = ManifestPath(gameId);
        if (!File.Exists(file)) return null;
        var manifest = JsonSerializer.Deserialize<OptiInstallManifest>(File.ReadAllText(file))!;
        // Reconciliar con el disco: una restauración puede haber deshecho un cambio de método o la propia instalación.
        if (File.Exists(Path.Combine(manifest.Directory, manifest.Proxy))) return manifest;
        var proxySha = manifest.Files.FirstOrDefault(f => f.RelativePath.Equals(manifest.Proxy, StringComparison.OrdinalIgnoreCase))?.Sha256;
        var moved = Proxies.FirstOrDefault(p => File.Exists(Path.Combine(manifest.Directory, p)) && ComponentUpdateService.Sha256(Path.Combine(manifest.Directory, p)) == proxySha);
        if (moved is not null)
        {
            var fixedManifest = manifest with { Proxy = moved, Files = [.. manifest.Files.Where(f => !f.RelativePath.Equals(manifest.Proxy, StringComparison.OrdinalIgnoreCase)), new OwnedFile(moved, proxySha!)] };
            SaveManifest(fixedManifest);
            return fixedManifest;
        }
        File.Move(file, file + "." + DateTimeOffset.Now.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N")[..6] + ".retirado"); // ya no está instalado
        return null;
    }

    /// <summary>Archivos del paquete que no se instalan (rutas relativas); lo decide la interfaz según la arquitectura elegida.</summary>
    public Func<string, bool>? LeaveOut { get; set; }

    /// <summary>Plan de instalación sin escribir nada: qué se crea, qué se omite y si el proxy está libre.</summary>
    public (string? Refusal, (string Source, string Relative)[] Copy, string[] Skip) Plan(string directory, OptiPackage package, string proxy, string? referenceIni, bool keepExistingIni)
    {
        if (!Proxies.Contains(proxy, StringComparer.OrdinalIgnoreCase)) return ("Método de carga no admitido.", [], []);
        if (File.Exists(Path.Combine(directory, proxy))) return ($"Ya existe {proxy} en la carpeta del juego (otro programa lo usa). Elige otro método de carga.", [], []);
        var copy = new List<(string, string)>(); var skip = new List<string>();
        foreach (var source in Directory.EnumerateFiles(package.Folder, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(package.Folder, source);
            if (Excluded.IsMatch(relative)) continue;
            if (LeaveOut?.Invoke(relative) == true) continue; // partes del paquete que no sirven en la gráfica elegida (p. ej. d4r fuera de AMD)
            if (source.Equals(package.MainBinary, StringComparison.OrdinalIgnoreCase)) { copy.Add((source, proxy)); continue; }
            if (File.Exists(Path.Combine(directory, relative))) { skip.Add(relative); continue; } // nunca se sobrescriben archivos del juego
            copy.Add((source, relative));
        }
        if (!copy.Any(c => c.Item2.Equals("OptiScaler.ini", StringComparison.OrdinalIgnoreCase)) && referenceIni is not null && File.Exists(referenceIni)
            && !File.Exists(Path.Combine(directory, "OptiScaler.ini")))
            copy.Add((referenceIni, "OptiScaler.ini"));
        if (keepExistingIni) copy.RemoveAll(c => c.Item2.Equals("OptiScaler.ini", StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(directory, "OptiScaler.ini")));
        return (null, [.. copy], [.. skip]);
    }

    /// <summary>Instalación como operación restaurable (Restaurar = retirar lo instalado si no cambió).</summary>
    public OptiInstallManifest Install(string gameId, string directory, OptiPackage package, string proxy, string? referenceIni)
    {
        if (Installed(gameId) is not null) throw new InvalidOperationException("Ya hay una instalación gestionada. Usa Actualizar o Desinstalar.");
        var (refusal, copy, skip) = Plan(directory, package, proxy, referenceIni, keepExistingIni: true);
        if (refusal is not null) throw new InvalidOperationException(refusal);
        var created = copy.Select(c => Path.GetDirectoryName(c.Relative)).Where(d => !string.IsNullOrEmpty(d))
            .SelectMany(d => Enumerable.Range(1, d!.Split(Path.DirectorySeparatorChar).Length).Select(i => string.Join(Path.DirectorySeparatorChar, d.Split(Path.DirectorySeparatorChar).Take(i))))
            .Distinct(StringComparer.OrdinalIgnoreCase).Where(d => !Directory.Exists(Path.Combine(directory, d))).ToArray();
        var owned = copy.Select(c => new OwnedFile(c.Relative, ComponentUpdateService.Sha256(c.Source))).ToArray();
        var manifest = new OptiInstallManifest(gameId, Path.GetFullPath(directory), proxy, package.Edition, package.Version, package.Origin, owned, created, skip, DateTimeOffset.Now);
        SaveManifest(manifest); // propiedad registrada antes de escribir
        try
        {
            operations.Apply(gameId, directory, "Instalación", $"Instalar {EditionName(package.Edition)} {package.Version}",
                copy.Select(c => new PlannedWrite(c.Relative, c.Source, null)).ToList(), package.Origin, package.Version);
        }
        catch { File.Delete(ManifestPath(gameId)); throw; }
        return manifest;
    }

    /// <summary>Retira sólo lo propio, con respaldo de todo el conjunto (Restaurar lo vuelve a dejar como estaba).</summary>
    public string[] Uninstall(string gameId, bool keepIni)
    {
        var manifest = Installed(gameId) ?? throw new InvalidOperationException("No hay instalación gestionada por ILION SETUP para este juego.");
        var kept = new List<string>(); var writes = new List<PlannedWrite>();
        foreach (var file in manifest.Files)
        {
            var path = Path.Combine(manifest.Directory, file.RelativePath);
            if (!File.Exists(path)) continue;
            var isIni = file.RelativePath.Equals("OptiScaler.ini", StringComparison.OrdinalIgnoreCase);
            if (isIni && keepIni) continue;
            var current = ComponentUpdateService.Sha256(path);
            var isBase = file.RelativePath.Equals(manifest.Proxy, StringComparison.OrdinalIgnoreCase) && IsOptiBinary(path);
            if (!isIni && !isBase && current != file.Sha256) { kept.Add(file.RelativePath); continue; } // modificado por otro: no se retira
            writes.Add(new PlannedWrite(file.RelativePath, null, current));
        }
        if (writes.Count > 0)
            operations.Apply(gameId, manifest.Directory, "Desinstalación", $"Desinstalar {EditionName(manifest.Edition)} {manifest.Version}", writes, manifest.Origin, manifest.Version);
        foreach (var dir in manifest.CreatedDirectories.OrderByDescending(d => d.Length))
        {
            var full = Path.Combine(manifest.Directory, dir);
            if (Directory.Exists(full) && !Directory.EnumerateFileSystemEntries(full).Any()) Directory.Delete(full);
        }
        File.Move(ManifestPath(gameId), ManifestPath(gameId) + "." + DateTimeOffset.Now.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N")[..6] + ".retirado");
        return [.. kept];
    }

    /// <summary>Archivos que se considerarían parte de una instalación existente (solo nombres conocidos de OptiScaler).</summary>
    public static string[] AdoptionCandidates(string directory, string binary)
    {
        var list = new List<string>();
        var dir = Path.GetFullPath(directory).TrimEnd('\\');
        if (Path.GetDirectoryName(Path.GetFullPath(binary))!.Equals(dir, StringComparison.OrdinalIgnoreCase)) list.Add(Path.GetFileName(binary));
        foreach (var name in new[] { "OptiScaler.ini", "fakenvapi.ini", "dlssg_to_fsr3_amd_is_better.dll" })
            if (File.Exists(Path.Combine(dir, name))) list.Add(name);
        var nvapi = Path.Combine(dir, "nvapi64.dll");
        if (File.Exists(nvapi) && (FileVersionInfo.GetVersionInfo(nvapi).ProductName ?? "").Contains("fakenvapi", StringComparison.OrdinalIgnoreCase)) list.Add("nvapi64.dll");
        foreach (var folder in new[] { "OptiScaler", "D3D12_Optiscaler" })
        {
            var full = Path.Combine(dir, folder);
            if (Directory.Exists(full) && (File.GetAttributes(full) & FileAttributes.ReparsePoint) == 0)
                list.AddRange(Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(dir, f)));
        }
        return [.. list];
    }

    /// <summary>Administrar una instalación manual: copia exacta de sus archivos + registro de propiedad. No toca el juego.</summary>
    public OptiInstallManifest Adopt(string gameId, string directory, string binary, string[] files)
    {
        if (Installed(gameId) is not null) throw new InvalidOperationException("La instalación ya está administrada.");
        var edition = EditionOf(binary);
        var snapshot = Path.Combine(dataDirectory, "Backups", IniConfigStore.Safe(gameId), "adopcion-" + DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss"));
        var owned = new List<OwnedFile>();
        foreach (var relative in files)
        {
            var source = PathGuard.Resolve(directory, relative);
            var copy = Path.Combine(snapshot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            File.Copy(source, copy);
            var sha = ComponentUpdateService.Sha256(source);
            if (ComponentUpdateService.Sha256(copy) != sha) throw new IOException("La copia de seguridad no es idéntica: " + relative);
            owned.Add(new(relative, sha));
        }
        var manifest = new OptiInstallManifest(gameId, Path.GetFullPath(directory), Path.GetFileName(binary), edition, DisplayVersion(binary, edition),
            "Instalación existente administrada · copia en " + snapshot, [.. owned], [], [], DateTimeOffset.Now, Adopted: true);
        SaveManifest(manifest);
        return manifest;
    }

    /// <summary>Cambiar el método de carga (renombrar el proxy) como operación restaurable.</summary>
    public static bool SameFolder(string a, string b) =>
        Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar).Equals(Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

    public OptiInstallManifest ChangeProxy(string gameId, string newProxy)
    {
        var manifest = Installed(gameId) ?? throw new InvalidOperationException("No hay instalación administrada.");
        if (!Proxies.Contains(newProxy, StringComparer.OrdinalIgnoreCase)) throw new InvalidOperationException("Método de carga no admitido.");
        if (newProxy.Equals(manifest.Proxy, StringComparison.OrdinalIgnoreCase)) return manifest;
        var old = manifest.Files.FirstOrDefault(f => f.RelativePath.Equals(manifest.Proxy, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("El binario de carga no figura en el registro.");
        var oldPath = PathGuard.Resolve(manifest.Directory, manifest.Proxy);
        if (!File.Exists(oldPath) || ComponentUpdateService.Sha256(oldPath) != old.Sha256) throw new InvalidOperationException($"{manifest.Proxy} ya no es el que instaló ILION SETUP (cambió después). Para no romper nada, no se ha tocado. Usa «Verificar y reparar».");
        if (File.Exists(Path.Combine(manifest.Directory, newProxy))) throw new InvalidOperationException($"Ya existe {newProxy} en la carpeta del juego.");
        var temp = Path.Combine(dataDirectory, "Cache", "proxy-" + Guid.NewGuid().ToString("N") + ".dll");
        Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
        File.Copy(oldPath, temp);
        try
        {
            operations.Apply(gameId, manifest.Directory, "Método de carga", $"Cambiar carga {manifest.Proxy} → {newProxy}",
                [new PlannedWrite(newProxy, temp, null), new PlannedWrite(manifest.Proxy, null, old.Sha256)], manifest.Origin, manifest.Version);
        }
        finally { File.Delete(temp); }
        var updated = manifest with { Proxy = newProxy, Files = [.. manifest.Files.Where(f => f != old), new OwnedFile(newProxy, old.Sha256)] };
        SaveManifest(updated);
        return updated;
    }

    private void SaveManifest(OptiInstallManifest manifest)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ManifestPath(manifest.GameId))!);
        File.WriteAllText(ManifestPath(manifest.GameId), JsonSerializer.Serialize(manifest, Json));
    }

    private string ManifestPath(string gameId) => Path.Combine(dataDirectory, "OptiScaler", IniConfigStore.Safe(gameId), "install.json");

    private static bool IsOptiBinary(string path)
    {
        if (!File.Exists(path)) return false;
        try
        {
            using (var stream = File.OpenRead(path))
            using (var pe = new PEReader(stream))
                if (pe.PEHeaders.CoffHeader.Machine != Machine.Amd64 || !pe.PEHeaders.IsDll) return false;
            var info = FileVersionInfo.GetVersionInfo(path);
            return info.ProductName?.Contains("OptiScaler", StringComparison.OrdinalIgnoreCase) == true ||
                   info.OriginalFilename?.Equals("OptiScaler.dll", StringComparison.OrdinalIgnoreCase) == true;
        }
        catch (Exception error) when (error is IOException or BadImageFormatException) { return false; }
    }
}
