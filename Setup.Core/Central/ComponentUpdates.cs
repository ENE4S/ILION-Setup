// CLAUDE-RESCATE 0.4.1 — Cadena real de actualización y restauración de componentes.
// Sustituye el filtro por nombre de OfficialCatalog (que rechazaba el 100 % de los paquetes reales)
// por recetas explícitas: componente -> fuente oficial -> ubicación exacta del binario dentro de la publicación.
//
// Política de confianza (tres capas independientes, ninguna se relaja):
//   1. Integridad   : SHA-256 publicado por GitHub (campo "digest" del recurso) o SHA-1 de blob Git del árbol de la etiqueta.
//   2. Autenticidad : firma Authenticode válida (WinVerifyTrust) y firmante esperado (NVIDIA / AMD / Intel).
//   3. Identidad    : PE x64, mismo nombre de archivo, ProductName/FileDescription coherente con la receta.
// La compatibilidad con un juego concreto NO la acredita ninguna de las tres: se informa como "no comprobada".
// Tras la descarga se calcula y registra el SHA-256 del binario extraído en el manifiesto del respaldo.
using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Ilion.Client;

public enum PackageKind { RepositoryFile, ReleaseArchiveEntry, ReleaseAssetFile }

/// <summary>Cómo obtener un componente concreto desde su fuente oficial.</summary>
public sealed record ComponentRecipe(
    string FileName, string Technology, string Vendor, string Repository, PackageKind Kind,
    string PathOrEntry, string? AssetPattern, string? Signer, string IdentityPattern)
{
    /// <summary>Etiqueta fija cuando la última publicación no contiene este binario (p. ej. FSR 3.1 vive en SDK 1.1.x).</summary>
    public string? PinnedTag { get; init; }
    /// <summary>true sólo si se ha comprobado que la etiqueta coincide con la versión interna del archivo (DLSS).</summary>
    public bool TagIsFileVersion { get; init; }

    public static IReadOnlyList<ComponentRecipe> All { get; } =
    [
        // NVIDIA: binarios firmados en el árbol de la etiqueta del SDK oficial (no hay recurso suelto).
        new("nvngx_dlss.dll",  "DLSS Super Resolution",   "NVIDIA", "NVIDIA/DLSS", PackageKind.RepositoryFile, "lib/Windows_x86_64/rel/nvngx_dlss.dll",  null, "NVIDIA Corporation", "DLSS") { TagIsFileVersion = true },
        new("nvngx_dlssd.dll", "DLSS Ray Reconstruction", "NVIDIA", "NVIDIA/DLSS", PackageKind.RepositoryFile, "lib/Windows_x86_64/rel/nvngx_dlssd.dll", null, "NVIDIA Corporation", "DLSS") { TagIsFileVersion = true },
        new("nvngx_dlssg.dll", "DLSS Frame Generation",   "NVIDIA", "NVIDIA/DLSS", PackageKind.RepositoryFile, "lib/Windows_x86_64/rel/nvngx_dlssg.dll", null, "NVIDIA Corporation", "DLSS") { TagIsFileVersion = true },
        // Streamline: dentro del ZIP del SDK (≈276 MB, con SHA-256 publicado).
        .. new[] { "sl.interposer.dll", "sl.common.dll", "sl.dlss.dll", "sl.dlss_g.dll", "sl.dlss_d.dll", "sl.reflex.dll", "sl.pcl.dll", "sl.nis.dll", "sl.deepdvc.dll", "sl.directsr.dll" }
            .Select(name => new ComponentRecipe(name, StreamlineName(name), "NVIDIA", "NVIDIA-RTX/Streamline", PackageKind.ReleaseArchiveEntry,
                "bin/x64/" + name, @"^streamline-sdk-v[0-9.]+\.zip$", "NVIDIA Corporation", "Streamline|NVIDIA")),
        // Intel: dentro del ZIP del SDK de XeSS.
        new("libxess.dll",      "XeSS Super Resolution", "Intel", "intel/xess", PackageKind.ReleaseArchiveEntry, "bin/libxess.dll",      @"^XeSS_SDK_[0-9.]+\.zip$", "Intel Corporation", "XeSS"),
        new("libxess_dx11.dll", "XeSS DX11",             "Intel", "intel/xess", PackageKind.ReleaseArchiveEntry, "bin/libxess_dx11.dll", @"^XeSS_SDK_[0-9.]+\.zip$", "Intel Corporation", "XeSS"),
        new("libxess_fg.dll",   "XeSS Frame Generation", "Intel", "intel/xess", PackageKind.ReleaseArchiveEntry, "bin/libxess_fg.dll",   @"^XeSS_SDK_[0-9.]+\.zip$", "Intel Corporation", "XeSS|XeFG"),
        new("libxell.dll",      "XeLL",                  "Intel", "intel/xess", PackageKind.ReleaseArchiveEntry, "bin/libxell.dll",      @"^XeSS_SDK_[0-9.]+\.zip$", "Intel Corporation", "XeLL|Low Latency"),
        // AMD: binarios firmados del árbol de la etiqueta (FidelityFX SDK 2.x, carpeta signedbin).
        // OJO: los juegos con FSR 3.1 usan amd_fidelityfx_dx12.dll (SDK 1.1.x, PrebuiltSignedDLL). Receta pendiente: ver TRASPASO.
        .. new[] { "amd_fidelityfx_loader_dx12.dll", "amd_fidelityfx_upscaler_dx12.dll", "amd_fidelityfx_framegeneration_dx12.dll",
                   "amd_fidelityfx_loader_vk.dll", "amd_fidelityfx_upscaler_vk.dll", "amd_fidelityfx_framegeneration_vk.dll" }
            .Select(name => new ComponentRecipe(name, (name.Contains("upscaler") ? "FidelityFX Upscaler" : name.Contains("framegeneration") ? "FSR Frame Generation" : "FidelityFX Loader") + (name.EndsWith("_vk.dll") ? " Vulkan" : " DX12"),
                "AMD", "GPUOpen-LibrariesAndSDKs/FidelityFX-SDK", PackageKind.RepositoryFile, "Kits/FidelityFX/signedbin/" + name, null,
                "Advanced Micro Devices", "FidelityFX|FSR|AMD")),
        // AMD FSR 3.1 (DLL única): FidelityFX SDK v1.1.4, carpeta PrebuiltSignedDLL. Comprobado 2026-11-06 con la API de contenidos.
        new("amd_fidelityfx_dx12.dll", "FSR 3.1 (FidelityFX DX12)", "AMD", "GPUOpen-LibrariesAndSDKs/FidelityFX-SDK", PackageKind.RepositoryFile,
            "PrebuiltSignedDLL/amd_fidelityfx_dx12.dll", null, "Advanced Micro Devices", "FidelityFX|FSR|AMD") { PinnedTag = "v1.1.4" },
        new("amd_fidelityfx_vk.dll", "FSR 3.1 (FidelityFX Vulkan)", "AMD", "GPUOpen-LibrariesAndSDKs/FidelityFX-SDK", PackageKind.RepositoryFile,
            "PrebuiltSignedDLL/amd_fidelityfx_vk.dll", null, "Advanced Micro Devices", "FidelityFX|FSR|AMD") { PinnedTag = "v1.1.4" },
        // OptiPatcher: .asi suelto con SHA-256 publicado. No está firmado: Signer null => autenticidad = sólo origen + digest.
        new("OptiPatcher.asi", "OptiPatcher", "OptiScaler", "optiscaler/OptiPatcher", PackageKind.ReleaseAssetFile, "", @"^OptiPatcher_v[0-9.]+\.asi$", null, ""),
    ];

    public static string StreamlineName(string file) => "Streamline · " + (file.ToLowerInvariant() switch
    {
        "sl.interposer.dll" => "interposer", "sl.common.dll" => "común", "sl.dlss.dll" => "DLSS", "sl.dlss_g.dll" => "DLSS FG",
        "sl.dlss_d.dll" => "Ray Reconstruction", "sl.reflex.dll" => "Reflex", "sl.pcl.dll" => "PCL (latencia)", "sl.nis.dll" => "NIS",
        "sl.deepdvc.dll" => "DeepDVC", "sl.directsr.dll" => "DirectSR", "sl.nvperf.dll" => "NvPerf", _ => Path.GetFileNameWithoutExtension(file)
    });

    public static ComponentRecipe? For(string fileName) =>
        All.FirstOrDefault(recipe => recipe.FileName.Equals(Path.GetFileName(fileName), StringComparison.OrdinalIgnoreCase));
}

/// <summary>Versión publicada concreta con la referencia de integridad que la acompaña.</summary>
public sealed record ComponentCandidate(ComponentRecipe Recipe, string Tag, Uri Url, long Size, string IntegrityKind, string IntegrityValue, string? EntryPath);

/// <summary>Binario descargado, verificado y listo para instalar.</summary>
public sealed record VerifiedComponent(ComponentCandidate Candidate, string LocalPath, string Sha256, string FileVersion, string? SignerSubject, string[] Evidence);

public sealed class ComponentUpdateService(HttpClient http, string dataDirectory)
{
    private const long MaximumArchive = 300L * 1024 * 1024;

    // ---------- 1. Resolución: ¿cuál es la última publicación y dónde está el binario? ----------
    public async Task<ComponentCandidate> ResolveAsync(ComponentRecipe recipe, string? tag, CancellationToken token)
    {
        tag ??= recipe.PinnedTag;
        var releases = await GetJsonAsync($"https://api.github.com/repos/{recipe.Repository}/releases?per_page=30", token);
        var release = releases.RootElement.EnumerateArray()
            .Where(entry => !entry.GetProperty("draft").GetBoolean() && !entry.GetProperty("prerelease").GetBoolean())
            .FirstOrDefault(entry => tag is null || entry.GetProperty("tag_name").GetString() == tag);
        if (release.ValueKind != JsonValueKind.Object) throw new InvalidOperationException($"{recipe.Repository}: no hay publicación estable{(tag is null ? "" : " " + tag)}.");
        var releaseTag = release.GetProperty("tag_name").GetString()!;

        if (recipe.Kind == PackageKind.RepositoryFile)
        {
            using var content = await GetJsonAsync($"https://api.github.com/repos/{recipe.Repository}/contents/{recipe.PathOrEntry}?ref={Uri.EscapeDataString(releaseTag)}", token);
            var root = content.RootElement;
            var size = root.GetProperty("size").GetInt64();
            if (size < 4096) throw new InvalidDataException("El archivo del repositorio parece un puntero LFS, no un binario.");
            var url = new Uri($"https://raw.githubusercontent.com/{recipe.Repository}/{releaseTag}/{recipe.PathOrEntry}");
            return new(recipe, releaseTag, url, size, "git-blob-sha1", root.GetProperty("sha").GetString()!, null);
        }

        var pattern = new Regex(recipe.AssetPattern!, RegexOptions.IgnoreCase);
        foreach (var asset in release.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString()!;
            if (!pattern.IsMatch(name)) continue;
            var digest = asset.TryGetProperty("digest", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            if (digest is null || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"{name}: GitHub no publica SHA-256 para este recurso; no se descarga.");
            var url = new Uri(asset.GetProperty("browser_download_url").GetString()!);
            if (url.Host != "github.com" || !url.AbsolutePath.StartsWith($"/{recipe.Repository}/releases/download/", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("URL de recurso fuera del repositorio oficial.");
            return new(recipe, releaseTag, url, asset.GetProperty("size").GetInt64(), "sha256", digest[7..].ToLowerInvariant(),
                recipe.Kind == PackageKind.ReleaseArchiveEntry ? recipe.PathOrEntry : null);
        }
        throw new InvalidOperationException($"{recipe.Repository} {releaseTag}: ningún recurso coincide con {recipe.AssetPattern}.");
    }

    // ---------- 2. Descarga + integridad + extracción + autenticidad + identidad ----------
    public async Task<VerifiedComponent> FetchAsync(ComponentCandidate candidate, CancellationToken token)
    {
        if (candidate.Size <= 0 || candidate.Size > MaximumArchive) throw new InvalidDataException("Tamaño fuera de límites.");
        var cache = Path.Combine(dataDirectory, "Packages", candidate.Recipe.Repository.Replace('/', '_'), candidate.Tag);
        Directory.CreateDirectory(cache);
        var download = Path.Combine(cache, Path.GetFileName(candidate.Url.AbsolutePath));
        var evidence = new List<string> { $"Origen: {candidate.Url}", $"Etiqueta: {candidate.Tag}" };

        if (!File.Exists(download) || !IntegrityMatches(download, candidate))
        {
            var part = download + ".part";
            using (var response = await http.GetAsync(candidate.Url, HttpCompletionOption.ResponseHeadersRead, token))
            {
                response.EnsureSuccessStatusCode();
                await using var input = await response.Content.ReadAsStreamAsync(token);
                await using var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None);
                var buffer = new byte[1 << 16]; long written = 0; int count;
                while ((count = await input.ReadAsync(buffer, token)) > 0)
                {
                    written += count;
                    if (written > candidate.Size) throw new IOException("La descarga excede el tamaño publicado.");
                    await output.WriteAsync(buffer.AsMemory(0, count), token);
                }
            }
            if (!IntegrityMatches(part, candidate)) { File.Delete(part); throw new IOException($"Integridad rechazada ({candidate.IntegrityKind})."); }
            File.Move(part, download, overwrite: true);
        }
        evidence.Add($"Integridad: {candidate.IntegrityKind} {candidate.IntegrityValue} coincide");

        var binary = download;
        if (candidate.EntryPath is { } entryPath)
        {
            binary = Path.Combine(cache, "extracted", candidate.Recipe.FileName);
            Directory.CreateDirectory(Path.GetDirectoryName(binary)!);
            using var archive = ZipFile.OpenRead(download);
            var entry = archive.GetEntry(entryPath) ?? throw new InvalidDataException($"El paquete no contiene {entryPath}.");
            entry.ExtractToFile(binary, overwrite: true);
            evidence.Add($"Extraído: {entryPath}");
        }
        else if (candidate.Recipe.Kind == PackageKind.ReleaseAssetFile)
        {
            binary = Path.Combine(cache, "extracted", candidate.Recipe.FileName);
            Directory.CreateDirectory(Path.GetDirectoryName(binary)!);
            File.Copy(download, binary, overwrite: true);
        }

        RequirePeX64(binary);
        evidence.Add("Formato: PE x64");
        string? signer = null;
        if (candidate.Recipe.Signer is { } expectedSigner)
        {
            if (!Authenticode.IsTrusted(binary)) throw new InvalidDataException("Firma Authenticode ausente o no válida.");
            signer = Authenticode.Subject(binary);
            if (signer is null || !signer.Contains(expectedSigner, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Firmante inesperado: {signer ?? "desconocido"} (se esperaba {expectedSigner}).");
            evidence.Add($"Autenticidad: Authenticode válido · {signer}");
        }
        else evidence.Add("Autenticidad: sin firma de editor; sólo origen oficial + digest publicado");

        var info = FileVersionInfo.GetVersionInfo(binary);
        var identity = $"{info.ProductName} {info.FileDescription}";
        if (candidate.Recipe.IdentityPattern.Length > 0 && !Regex.IsMatch(identity, candidate.Recipe.IdentityPattern, RegexOptions.IgnoreCase))
            throw new InvalidDataException($"Identidad del binario no coincide: «{identity.Trim()}».");
        var sha256 = Sha256(binary);
        evidence.Add($"SHA-256 calculado y registrado: {sha256}");
        return new(candidate, binary, sha256, VersionOf(binary), signer, [.. evidence]);
    }

    /// <summary>Motor de operaciones restaurables (respaldos de conjunto, diario, recuperación).</summary>
    public OperationEngine Operations { get; } = new(dataDirectory);

    // ---------- 3. Política (núcleo): propiedad + estado, revalidados justo antes de escribir ----------
    public static string? RefuseReason(string gameRoot, string relativePath, ComponentRecipe recipe, UpdateMode mode = UpdateMode.Standard,
        IReadOnlySet<string>? managed = null, string? optiBinary = null)
    {
        string target;
        try { target = PathGuard.Resolve(gameRoot, relativePath); }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException or ArgumentException) { return error.Message; }
        if (!File.Exists(target)) return "El archivo de destino no existe; Actualizar sólo sustituye componentes presentes.";
        if (!Path.GetFileName(target).Equals(recipe.FileName, StringComparison.OrdinalIgnoreCase)) return "El nombre no corresponde a la receta.";
        try { RequirePeX64(target); } catch (Exception error) when (error is IOException or BadImageFormatException or InvalidDataException) { return "El archivo actual no es PE x64: " + error.Message; }
        var info = ComponentClassifier.Classify(gameRoot, new DetectedLibrary(Path.GetFileName(target), "", relativePath, VersionOf(target), ""), managed, optiBinary);
        // Mods y archivos sin firma oficial: nunca con una DLL oficial genérica, en ningún modo. Solo carga manual.
        if (info.Owner is Ownership.Mod or Ownership.Unknown)
            return info.Owner == Ownership.Mod
                ? $"Pertenece a «{info.Package}»: una DLL oficial genérica puede romper el mod. Solo se admite la carga manual de una DLL elegida por ti."
                : "Sin firma oficial del fabricante (posible mod o archivo modificado): solo se admite la carga manual.";
        // OptiScaler: solo se admite la oficial cuando el archivo actual es una DLL oficial firmada por su fabricante
        if (info.Owner == Ownership.OptiScaler && (recipe.Signer is null || ComponentClassifier.SignerOf(target) is not { } s1 || !s1.Contains(recipe.Signer, StringComparison.OrdinalIgnoreCase)))
            return "Componente propio de OptiScaler o de un proveedor (no es la DLL oficial firmada): solo se admite la carga manual.";
        if (mode == UpdateMode.Standard && info.Owner != Ownership.Game)
            return info.Owner switch
            {
                Ownership.OptiScaler => "Componente de OptiScaler: solo se sustituye como operación avanzada (Swap › OptiScaler).",
                Ownership.Mod => $"Pertenece a «{info.Package}»: solo se sustituye como operación avanzada (Swap › Mods).",
                _ => "Procedencia desconocida (sin firma oficial del fabricante): solo como operación avanzada (Swap › Mods)."
            };
        return null;
    }

    // ---------- 4. Sustitución transaccional (uno o varios archivos) con respaldo de conjunto ----------
    public OperationManifest Update(string gameId, string gameRoot, string relativePath, VerifiedComponent component, string expectedSha,
        UpdateMode mode = UpdateMode.Standard, IReadOnlySet<string>? managed = null, string? optiBinary = null) =>
        UpdateMany(gameId, gameRoot, $"{component.Candidate.Recipe.Technology} {VersionUtil.Display(component.FileVersion)}",
            [(relativePath, component, expectedSha)], mode, managed, optiBinary);

    public OperationManifest UpdateMany(string gameId, string gameRoot, string label,
        IReadOnlyList<(string Relative, VerifiedComponent Component, string ExpectedSha)> items, UpdateMode mode,
        IReadOnlySet<string>? managed = null, string? optiBinary = null)
    {
        foreach (var (relative, component, _) in items)
        {
            if (RefuseReason(gameRoot, relative, component.Candidate.Recipe, mode, managed, optiBinary) is { } reason) throw new InvalidOperationException(reason);
            var target = PathGuard.Resolve(gameRoot, relative);
            if (Sha256(target) == component.Sha256) throw new InvalidOperationException($"{relative}: ya es idéntico a la versión publicada.");
            if (VersionUtil.Compare(component.FileVersion, VersionOf(target)) <= 0)
                throw new InvalidOperationException($"{relative}: la versión publicada ({component.FileVersion}) no es más reciente que la instalada ({VersionOf(target)}). No se sustituye.");
        }
        var writes = items.Select(i => new PlannedWrite(i.Relative, i.Component.LocalPath, i.ExpectedSha, i.Component.FileVersion)).ToList();
        var first = items[0].Component.Candidate;
        return Operations.Apply(gameId, gameRoot, mode == UpdateMode.Standard ? "Actualización" : "Sustitución avanzada", label, writes, first.Recipe.Repository, first.Tag);
    }

    public OperationManifest Restore(OperationManifest operation) => Operations.Restore(operation);

    // ---------- 5. Carga manual de una DLL elegida por el usuario ----------
    public sealed record ManualCheck(string? Block, string[] Warnings, string Version, string Identity, string Sha256);

    /// <summary>Comprueba una DLL local contra el destino. Block != null impide la operación; los avisos requieren confirmación.</summary>
    public static ManualCheck CheckManual(string gameRoot, string relativePath, string source)
    {
        string target;
        try { target = PathGuard.Resolve(gameRoot, relativePath); }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or ArgumentException) { return new(e.Message, [], "", "", ""); }
        if (!File.Exists(target)) return new("El archivo de destino no existe.", [], "", "", "");
        if (!File.Exists(source)) return new("El archivo elegido no existe.", [], "", "", "");
        if (Path.GetFullPath(source).Equals(target, StringComparison.OrdinalIgnoreCase)) return new("Has elegido el mismo archivo de destino.", [], "", "", "");
        var info = new FileInfo(source);
        if (info.Length < 1024 || info.Length > 1024L * 1024 * 1024) return new("Tamaño de archivo no válido para una DLL.", [], "", "", "");
        Machine srcMachine, dstMachine; bool srcDll;
        try
        {
            using (var s = File.OpenRead(source)) using (var pe = new PEReader(s)) { srcMachine = pe.PEHeaders.CoffHeader.Machine; srcDll = pe.PEHeaders.IsDll; }
            using (var t = File.OpenRead(target)) using (var pe = new PEReader(t)) dstMachine = pe.PEHeaders.CoffHeader.Machine;
        }
        catch (Exception e) when (e is BadImageFormatException or InvalidOperationException or IOException) { return new("El archivo elegido no es una DLL de Windows válida.", [], "", "", ""); }
        if (!srcDll) return new("El archivo elegido no es una DLL.", [], "", "", "");
        if (srcMachine != dstMachine) return new($"Arquitectura incompatible: la DLL elegida es {srcMachine} y el destino {dstMachine}.", [], "", "", "");
        var sha = Sha256(source);
        if (sha == Sha256(target)) return new("La DLL elegida es idéntica a la instalada.", [], "", "", "");
        var a = FileVersionInfo.GetVersionInfo(source); var b = FileVersionInfo.GetVersionInfo(target);
        var warnings = new List<string>();
        static string Id(FileVersionInfo v) => $"{v.ProductName} {v.FileDescription} {v.OriginalFilename}".Trim();
        if (!Path.GetFileName(source).Equals(Path.GetFileName(target), StringComparison.OrdinalIgnoreCase))
            warnings.Add($"El nombre es distinto ({Path.GetFileName(source)}); se instalará como {Path.GetFileName(target)}.");
        if (!string.IsNullOrWhiteSpace(b.OriginalFilename) && !string.IsNullOrWhiteSpace(a.OriginalFilename) && !a.OriginalFilename.Equals(b.OriginalFilename, StringComparison.OrdinalIgnoreCase))
            warnings.Add($"Identidad distinta: «{a.OriginalFilename}» en lugar de «{b.OriginalFilename}».");
        if (VersionUtil.Parse(a.FileVersion) is { } va && VersionUtil.Parse(b.FileVersion) is { } vb && va < vb)
            warnings.Add($"La versión elegida ({VersionUtil.Display(a.FileVersion)}) es anterior a la instalada ({VersionUtil.Display(b.FileVersion)}).");
        if (ComponentClassifier.SignerOf(target) is { } signer && ComponentClassifier.SignerOf(source) is null)
            warnings.Add($"La instalada está firmada por {signer}; la elegida no tiene firma válida.");
        warnings.Add("No se puede garantizar la compatibilidad funcional con el juego ni con el resto del paquete.");
        return new(null, [.. warnings], a.FileVersion ?? "", Id(a), sha);
    }

    public OperationManifest ManualReplace(string gameId, string gameRoot, string relativePath, string source, string expectedSha)
    {
        var check = CheckManual(gameRoot, relativePath, source);
        if (check.Block is not null) throw new InvalidOperationException(check.Block);
        var cache = Path.Combine(dataDirectory, "Manual", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cache);
        var copy = Path.Combine(cache, Path.GetFileName(relativePath));
        File.Copy(source, copy);
        if (Sha256(copy) != check.Sha256) throw new IOException("La DLL elegida cambió mientras se copiaba.");
        return Operations.Apply(gameId, gameRoot, "Sustitución manual", $"{Path.GetFileName(relativePath)} ← {Path.GetFileName(source)}",
            [new PlannedWrite(relativePath, copy, expectedSha, check.Version)], "Archivo local: " + source, VersionUtil.Display(check.Version));
    }
    public IReadOnlyList<OperationManifest> Backups(string gameId) => Operations.All(gameId);

    // ---------- utilidades ----------
    // Caché de 15 min por URL: GitHub sin token admite 60 consultas/hora.
    private readonly Dictionary<string, (DateTime When, string Body)> responses = new();
    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken token)
    {
        lock (responses)
            if (responses.TryGetValue(url, out var hit) && DateTime.UtcNow - hit.When < TimeSpan.FromMinutes(15)) return JsonDocument.Parse(hit.Body);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("ILION-Setup/" + Ilion.Setup.SetupEngine.SetupVersion);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await http.SendAsync(request, token);
        if ((int)response.StatusCode == 403) throw new HttpRequestException("GitHub limitó las consultas (60/h sin token). Reintenta más tarde.");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(token);
        lock (responses) responses[url] = (DateTime.UtcNow, body);
        return JsonDocument.Parse(body);
    }

    private static bool IntegrityMatches(string file, ComponentCandidate candidate)
    {
        if (new FileInfo(file).Length != candidate.Size) return false;
        if (candidate.IntegrityKind == "sha256") return Sha256(file) == candidate.IntegrityValue;
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1); // hash de objeto Git: "blob <n>\0" + contenido
        sha1.AppendData(Encoding.ASCII.GetBytes($"blob {candidate.Size}\0"));
        sha1.AppendData(File.ReadAllBytes(file));
        return Convert.ToHexStringLower(sha1.GetHashAndReset()) == candidate.IntegrityValue;
    }

    public static string Sha256(string file) { using var stream = File.OpenRead(file); return Convert.ToHexStringLower(SHA256.HashData(stream)); }
    public static string VersionOf(string file) { var info = FileVersionInfo.GetVersionInfo(file); return $"{info.FileMajorPart}.{info.FileMinorPart}.{info.FileBuildPart}.{info.FilePrivatePart}"; }
    private static string Safe(string value) => string.Concat(value.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_'));

    private static void RequirePeX64(string file)
    {
        using var stream = File.OpenRead(file);
        using var reader = new PEReader(stream);
        if (reader.PEHeaders.CoffHeader.Machine != Machine.Amd64) throw new InvalidDataException("No es x64.");
    }
}

public static class VersionUtil
{
    /// <summary>Convierte "310.9.16.0", "v310.9.16" o "3.1.4.0 (build)" en Version; null si no hay números.</summary>
    public static Version? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var match = Regex.Match(text.Replace(',', '.'), @"\d+(?:\.\d+){0,3}");
        if (!match.Success) return null;
        var parts = match.Value.Split('.').Select(int.Parse).Concat([0, 0, 0]).Take(4).ToArray();
        return new Version(parts[0], parts[1], parts[2], parts[3]);
    }
    public static int Compare(string? a, string? b) => (Parse(a) ?? new Version()).CompareTo(Parse(b) ?? new Version());
    public static string Display(string? text) => Parse(text) is { } v ? (v.Revision == 0 ? $"{v.Major}.{v.Minor}.{v.Build}" : v.ToString()) : "No declarada";
}

/// <summary>Verificación Authenticode mediante WinVerifyTrust (cadena + firma, sin interfaz).</summary>
public static class Authenticode
{
    public static bool IsTrusted(string file)
    {
        var path = Marshal.StringToCoTaskMemUni(file);
        var info = Marshal.AllocCoTaskMem(Marshal.SizeOf<FileInfo>());
        try
        {
            Marshal.StructureToPtr(new FileInfo { Size = (uint)Marshal.SizeOf<FileInfo>(), Path = path }, info, false);
            var data = new TrustData { Size = (uint)Marshal.SizeOf<TrustData>(), UiChoice = 2, UnionChoice = 1, File = info };
            var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
            return WinVerifyTrust(IntPtr.Zero, ref action, ref data) == 0;
        }
        finally { Marshal.FreeCoTaskMem(info); Marshal.FreeCoTaskMem(path); }
    }

    public static string? Subject(string file)
    {
        try
        {
#pragma warning disable SYSLIB0057
            return new X509Certificate2(X509Certificate.CreateFromSignedFile(file)).GetNameInfo(X509NameType.SimpleName, false);
#pragma warning restore SYSLIB0057
        }
        catch (System.Security.Cryptography.CryptographicException) { return null; }
    }

    [StructLayout(LayoutKind.Sequential)] private struct FileInfo { public uint Size; public IntPtr Path; public IntPtr Handle; public IntPtr Subject; }
    [StructLayout(LayoutKind.Sequential)]
    private struct TrustData
    {
        public uint Size; public IntPtr PolicyData; public IntPtr SipData; public uint UiChoice; public uint Revocation;
        public uint UnionChoice; public IntPtr File; public uint StateAction; public IntPtr State; public IntPtr Url;
        public uint ProvFlags; public uint UiContext; public IntPtr Settings;
    }
    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)] private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref TrustData data);
}
