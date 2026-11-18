// ILION Setup — base congelada de OptiScaler: los archivos exactos (por SHA-256) con los que funciona el motor ILION.
// Cada archivo se toma, en este orden: de la caché de Setup, de un paquete local que lo tenga idéntico (sin conexión) o de su
// origen oficial fijado en el manifiesto (paquete con huella publicada o URL fija). Nada se acepta si su SHA-256 no coincide.
using System.IO.Compression;
using Ilion.Client;
using SharpCompress.Archives;

namespace Ilion.Setup;

/// <summary>Estado de un archivo de la base en la carpeta del juego.</summary>
public enum BaseState { Ok, Missing, Different }
public sealed record BaseCheck(BaseFile File, BaseState State, string? ActualSha256);

public sealed class FrozenBase(HttpClient http, string dataDirectory, BaseManifest manifest)
{
    public BaseManifest Manifest => manifest;
    private string Cache => Path.Combine(dataDirectory, "Cache", "Base");

    /// <summary>Grupos opcionales: «streamline» (DLL de NVIDIA para entradas DLSS) y «agility» (D3D12Core para FsrAgilitySDKUpgrade).</summary>
    public static string Group(BaseFile f) =>
        f.GroupName is { Length: > 0 } g ? g // «motor» (ILION publicado), «variantes» (FSR 4, XeMFG, OptiPatcher), «lmxxf» (runtime NR)
        : f.Destination.StartsWith(@"OptiScaler\streamline\", StringComparison.OrdinalIgnoreCase) ? "streamline"
        : f.Destination.StartsWith(@"Licenses\", StringComparison.OrdinalIgnoreCase) ? "docs" // el instalador de OptiScaler no copia .txt ni .md al juego
        : f.Destination.Equals("OptiScaler.ini", StringComparison.OrdinalIgnoreCase) ? "ini"   // solo si el motor no trae la suya
        : f.Optional ? "agility" : "core";

    public IEnumerable<BaseFile> Select(ISet<string> groups, bool withCore = true) => manifest.Files.Where(f => withCore && Group(f) == "core" || groups.Contains(Group(f)));

    /// <summary>Deja en «staging» todos los archivos elegidos, verificados. Usa caché y paquetes locales antes que la red.</summary>
    public async Task<string[]> PrepareAsync(string staging, ISet<string> groups, IEnumerable<string> localFolders, IProgress<ModProgress>? progress, CancellationToken token, bool withCore = true)
    {
        Directory.CreateDirectory(Cache);
        var files = Select(groups, withCore).ToArray();
        var sources = new List<string>();
        var locals = localFolders.Where(Directory.Exists).ToArray();
        var pending = new List<BaseFile>();
        foreach (var f in files)
        {
            token.ThrowIfCancellationRequested();
            var cached = CachedFile(f.Sha256);
            if (!File.Exists(cached))
                foreach (var local in locals)
                {
                    var candidate = Path.Combine(local, f.Destination);
                    if (File.Exists(candidate) && new FileInfo(candidate).Length == f.Size && ComponentUpdateService.Sha256(candidate) == f.Sha256)
                    { File.Copy(candidate, cached, true); sources.Add($"{f.Destination}: paquete local"); break; }
                }
            if (!File.Exists(cached)) pending.Add(f);
        }
        // Lo que falte: de su origen oficial, agrupado por paquete (cada paquete se descarga una vez)
        foreach (var group in pending.GroupBy(f => f.Origin))
        {
            token.ThrowIfCancellationRequested();
            if (group.Key == "url")
                foreach (var f in group)
                {
                    var dest = CachedFile(f.Sha256);
                    await Download(new Uri(f.Url!), dest + ".part", f.Size, f.Destination, progress, token);
                    Accept(dest + ".part", dest, f.Sha256, f.Destination);
                    sources.Add($"{f.Destination}: {f.Url}");
                }
            else
            {
                var package = manifest.Packages.Single(p => p.Id == group.Key);
                var archive = Path.Combine(Cache, package.Sha256 + Path.GetExtension(new Uri(package.Url).AbsolutePath));
                if (!File.Exists(archive) || ComponentUpdateService.Sha256(archive) != package.Sha256)
                {
                    await Download(new Uri(package.Url), archive + ".part", null, package.Id, progress, token);
                    Accept(archive + ".part", archive, package.Sha256, package.Id);
                }
                progress?.Report(new(package.Id, "Verificando", null, null, null, 0, 0, "Extrayendo solo los archivos de la base"));
                await Task.Run(() => ExtractEntries(archive, group.ToArray()), token);
                foreach (var f in group) sources.Add($"{f.Destination}: {package.Url} ({f.Entry})");
            }
        }
        // Copia a la carpeta de preparación, con el nombre y la ruta finales
        foreach (var f in files)
        {
            var target = Path.Combine(staging, f.Destination);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(CachedFile(f.Sha256), target, true);
            if (ComponentUpdateService.Sha256(target) != f.Sha256) throw new InvalidDataException($"{f.Destination}: la copia no coincide con la base congelada.");
        }
        return [.. sources];
    }

    private string CachedFile(string sha) => Path.Combine(Cache, sha + ".bin");

    private void ExtractEntries(string archive, BaseFile[] wanted)
    {
        var byEntry = wanted.ToDictionary(f => f.Entry!.Replace('\\', '/'), StringComparer.OrdinalIgnoreCase);
        void Take(string entry, Func<Stream> open)
        {
            if (!byEntry.TryGetValue(entry.Replace('\\', '/'), out var f)) return;
            var part = CachedFile(f.Sha256) + ".part";
            using (var input = open()) using (var output = File.Create(part)) input.CopyTo(output);
            Accept(part, CachedFile(f.Sha256), f.Sha256, f.Destination);
        }
        if (archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            using (var zip = ZipFile.OpenRead(archive))
                foreach (var e in zip.Entries) Take(e.FullName, e.Open);
        else
            using (var a = ArchiveFactory.OpenArchive(archive))
                foreach (var e in a.Entries.Where(e => !e.IsDirectory)) Take(e.Key!, e.OpenEntryStream);
        var missing = wanted.Where(f => !File.Exists(CachedFile(f.Sha256))).Select(f => f.Entry).ToArray();
        if (missing.Length > 0) throw new InvalidDataException("El paquete oficial no contiene: " + string.Join(", ", missing));
    }

    private static void Accept(string part, string final, string sha, string what)
    {
        if (ComponentUpdateService.Sha256(part) != sha) { File.Delete(part); throw new InvalidDataException($"{what}: la huella SHA-256 no coincide con la base congelada. Descarga descartada."); }
        File.Move(part, final, true);
    }

    private async Task Download(Uri url, string target, long? size, string what, IProgress<ModProgress>? progress, CancellationToken token)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? size;
                await using var net = await response.Content.ReadAsStreamAsync(token);
                await using var file = File.Create(target);
                var buffer = new byte[1 << 16]; long done = 0; int read; var last = 0L;
                while ((read = await net.ReadAsync(buffer, token)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), token); done += read;
                    if (Environment.TickCount64 - last > 150) { progress?.Report(new(what, "Descargando", total > 0 ? (double)done / total : null, done, total, 0, 0, "Descargando la base oficial")); last = Environment.TickCount64; }
                }
                return;
            }
            catch (Exception e) when (attempt < 3 && !token.IsCancellationRequested && e is HttpRequestException or IOException)
            { await Task.Delay(1500 * attempt, token); }
        }
    }

    /// <summary>Comprueba la base en la carpeta del juego (la carpeta de OptiScaler va junto al ejecutable).</summary>
    public BaseCheck[] Verify(string exeDir, ISet<string> groups)
    {
        return [.. Select(groups).Select(f =>
        {
            var path = Path.Combine(exeDir, f.Destination);
            if (!File.Exists(path)) return new BaseCheck(f, BaseState.Missing, null);
            var sha = ComponentUpdateService.Sha256(path);
            return new BaseCheck(f, sha == f.Sha256 ? BaseState.Ok : BaseState.Different, sha);
        })];
    }

    /// <summary>¿El juego tiene la carpeta de Streamline o D3D12Core de la base? (para verificar solo lo que se instaló)</summary>
    public ISet<string> InstalledGroups(string exeDir)
    {
        var groups = new HashSet<string>();
        if (Directory.Exists(Path.Combine(exeDir, "OptiScaler", "streamline"))) groups.Add("streamline");
        if (File.Exists(Path.Combine(exeDir, "OptiScaler", "D3D12_OptiScaler", "D3D12Core.dll"))) groups.Add("agility");
        if (Directory.Exists(Path.Combine(exeDir, "ILION", "Escaladores", "FSR4"))) groups.Add("variantes");
        if (File.Exists(Path.Combine(exeDir, "LmxxfNrRuntime.dll"))) groups.Add("lmxxf");
        return groups;
    }
}
