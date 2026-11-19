using System.Buffers.Binary;
using System.Reflection.PortableExecutable;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Ilion.Client;

public enum EvidenceLevel { Unknown, Probable, Confirmed }
public sealed record ApiEvidence(string Name, EvidenceLevel Level, string Source);
public sealed record LibraryGame(string Id, string Name, string Platform, string Root, string? Executable);
public sealed record LibraryScan(LibraryGame[] Games, string[] Issues);
public interface IGameSource
{
    string Name { get; }
    LibraryScan Scan();
}

public static class LibraryDiscovery
{
    public static LibraryScan Scan(IEnumerable<IGameSource> sources)
    {
        var results = sources.Select(source =>
        {
            try { return source.Scan(); }
            catch (Exception error) when (IsReadError(error))
            { return new LibraryScan([], [$"{source.Name}: {error.Message}"]); }
        }).ToArray();
        return new LibraryScan(results.SelectMany(result => result.Games)
            .DistinctBy(game => (game.Platform, game.Id, game.Root.ToUpperInvariant()))
            .DistinctBy(game => game.Platform == "Manual" ? game.Id.ToUpperInvariant() : game.Root.ToUpperInvariant())
            .OrderBy(game => game.Name, StringComparer.CurrentCultureIgnoreCase).ToArray(),
            results.SelectMany(result => result.Issues).ToArray());
    }

    internal static bool IsReadError(Exception error) =>
        error is IOException or UnauthorizedAccessException or System.Security.SecurityException
            or JsonException or ArgumentException or InvalidDataException;

    internal static string? LocalPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) ||
            path.Length < 3 || path[1] != ':')
            return null;
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    internal static string? Executable(string root, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var full = LocalPath(Path.GetFullPath(path, Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar));
        if (full is null || Path.IsPathRooted(Path.GetRelativePath(root, full))) return null;
        var relative = Path.GetRelativePath(root, full);
        return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && string.Equals(Path.GetExtension(full), ".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(full) ? full : null;
    }

    internal static string ReadManifest(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > 4 * 1024 * 1024) throw new IOException("El manifiesto supera el límite de lectura.");
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var buffer = new char[4096];
        var text = new StringBuilder();
        int count;
        while ((count = reader.Read(buffer, 0, buffer.Length)) != 0)
        {
            if (text.Length + count > 4 * 1024 * 1024) throw new IOException("El manifiesto supera el límite de lectura.");
            text.Append(buffer, 0, count);
        }
        return text.ToString();
    }
}

internal sealed class ManifestCache<T>(Func<string, T> parse)
{
    private readonly Dictionary<string, (long Size, DateTime Stamp, T Value)> cache = new(StringComparer.OrdinalIgnoreCase);

    public T Read(string path)
    {
        var file = new FileInfo(path);
        var size = file.Length;
        var stamp = file.LastWriteTimeUtc;
        if (cache.TryGetValue(path, out var entry) && entry.Size == size && entry.Stamp == stamp) return entry.Value;
        var value = parse(LibraryDiscovery.ReadManifest(path));
        if (cache.Count >= 8192) cache.Clear();
        cache[path] = (size, stamp, value);
        return value;
    }
}

public sealed class SteamSource(IEnumerable<string> roots) : IGameSource
{
    private readonly ManifestCache<Vdf> manifests = new(Vdf.Parse);
    public string Name => "Steam";
    public LibraryScan Scan()
    {
        var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var games = new List<LibraryGame>();
        var issues = new List<string>();
        foreach (var root in roots.Select(LibraryDiscovery.LocalPath).OfType<string>())
        {
            libraries.Add(root);
            var modern = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            var legacy = Path.Combine(root, "config", "libraryfolders.vdf");
            var file = File.Exists(modern) ? modern : legacy;
            if (!File.Exists(file)) continue;
            try
            {
                var folders = manifests.Read(file).Child("libraryfolders");
                if (folders is null) continue;
                foreach (var entry in folders.Children.Where(entry => uint.TryParse(entry.Key, out _)))
                    if (LibraryDiscovery.LocalPath(entry.Value ?? entry.Child("path")?.Value) is { } path)
                        libraries.Add(path);
            }
            catch (Exception error) when (LibraryDiscovery.IsReadError(error))
            { issues.Add($"Steam · libraryfolders.vdf: {error.Message}"); }
        }

        foreach (var root in libraries)
        {
            var apps = Path.Combine(root, "steamapps");
            if (!Directory.Exists(apps)) continue;
            try
            {
                foreach (var manifest in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
                {
                    try
                    {
                        var app = manifests.Read(manifest).Child("AppState");
                        var id = app?.Child("appid")?.Value;
                        var name = app?.Child("name")?.Value;
                        var directory = app?.Child("installdir")?.Value;
                        var flags = app?.Child("StateFlags")?.Value;
                        if (id is null || !uint.TryParse(id, out _) || string.IsNullOrWhiteSpace(name) ||
                            string.IsNullOrWhiteSpace(directory) || directory is "." or ".." ||
                            directory.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                            continue;
                        // StateFlags: 4 = instalado del todo. Un juego con una actualización pendiente o en curso (2, 1024, 1026…) pierde ese
                        // bit pero sigue en su carpeta: antes desaparecía de la biblioteca. Se acepta si la carpeta ya tiene un ejecutable;
                        // una primera descarga a medias (sin ejecutable) sigue fuera
                        var game = Path.Combine(apps, "common", directory);
                        var installed = flags is null || (uint.TryParse(flags, out var state) && (state & 4) != 0);
                        if (Directory.Exists(game) && (installed || HasExecutable(game))) games.Add(new LibraryGame(id, name, Name, game, null));
                    }
                    catch (Exception error) when (LibraryDiscovery.IsReadError(error))
                    { issues.Add($"Steam · {Path.GetFileName(manifest)}: {error.Message}"); }
                }
            }
            catch (Exception error) when (LibraryDiscovery.IsReadError(error))
            { issues.Add($"Steam · biblioteca: {error.Message}"); }
        }
        return new LibraryScan([.. games], [.. issues]);
    }

    private static bool HasExecutable(string folder)
    {
        try { return Directory.EnumerateFiles(folder, "*.exe", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 4, IgnoreInaccessible = true }).Any(); }
        catch (Exception error) when (LibraryDiscovery.IsReadError(error)) { return false; }
    }

    private sealed record Vdf(string Key, string? Value, Vdf[] Children)
    {
        private static readonly Regex Tokens = new(
            """//[^\r\n]*|"((?:\\.|[^"\\])*)"|([{}])|(\S+)""",
            RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromSeconds(2));

        public Vdf? Child(string key) => Children.FirstOrDefault(node => node.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

        public static Vdf Parse(string text)
        {
            try
            {
                var tokens = Tokens.Matches(text).Select(match => match.Groups[1].Success
                    ? (Value: match.Groups[1].Value.Replace("\\\\", "\\").Replace("\\\"", "\""), Brace: false)
                    : (Value: match.Value, Brace: match.Groups[2].Success))
                    .Where(token => !token.Value.StartsWith("//", StringComparison.Ordinal) || token.Brace).ToArray();
                var index = 0;
                Vdf[] ReadGroup(int depth)
                {
                    if (depth > 32) throw new IOException("El manifiesto VDF tiene demasiados niveles.");
                    var nodes = new List<Vdf>();
                    while (index < tokens.Length)
                    {
                        var key = tokens[index++];
                        if (key is ("}", true) && depth > 0) return [.. nodes];
                        if (key.Brace || index >= tokens.Length) throw new IOException("Manifiesto VDF incompleto.");
                        var value = tokens[index++];
                        if (value is ("{", true)) nodes.Add(new Vdf(key.Value, null, ReadGroup(depth + 1)));
                        else if (!value.Brace) nodes.Add(new Vdf(key.Value, value.Value, []));
                        else throw new IOException("Valor VDF no válido.");
                    }
                    if (depth != 0) throw new IOException("Manifiesto VDF sin cierre.");
                    return [.. nodes];
                }
                return new Vdf("", null, ReadGroup(0));
            }
            catch (RegexMatchTimeoutException error) { throw new IOException("Tiempo de lectura VDF agotado.", error); }
        }
    }
}

public sealed class EpicSource(string manifestDirectory, string? launcherInstalled = null) : IGameSource
{
    private readonly ManifestCache<JsonElement> manifests = new(text =>
    {
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    });
    public string Name => "Epic";
    public LibraryScan Scan()
    {
        var games = new List<LibraryGame>();
        var issues = new List<string>();
        ReadLauncherInstalled(games, issues);
        if (!Directory.Exists(manifestDirectory)) return new LibraryScan([.. games], [.. issues]);
        foreach (var file in Directory.EnumerateFiles(manifestDirectory, "*.item"))
        {
            try
            {
                var item = manifests.Read(file);
                if (item.ValueKind != JsonValueKind.Object) throw new IOException("Se esperaba un objeto JSON.");
                string? Read(string key) => item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString() : null;
                if ((item.TryGetProperty("bIsIncompleteInstall", out var incomplete) && incomplete.ValueKind == JsonValueKind.True) ||
                    (item.TryGetProperty("bIsDLC", out var dlc) && dlc.ValueKind == JsonValueKind.True)) continue;
                var id = Read("AppName");
                var name = Read("DisplayName");
                var root = LibraryDiscovery.LocalPath(Read("InstallLocation"));
                var main = Read("MainGameAppName");
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name) || root is null || !Directory.Exists(root) ||
                    (!string.IsNullOrWhiteSpace(main) && !string.Equals(id, main, StringComparison.OrdinalIgnoreCase)))
                    continue;
                games.RemoveAll(g => g.Id.Equals(id, StringComparison.OrdinalIgnoreCase)); // el manifiesto manda sobre LauncherInstalled.dat
                games.Add(new LibraryGame(id, name, Name, root, LibraryDiscovery.Executable(root, Read("LaunchExecutable"))));
            }
            catch (Exception error) when (LibraryDiscovery.IsReadError(error))
            { issues.Add($"Epic · {Path.GetFileName(file)}: {error.Message}"); }
        }
        return new LibraryScan([.. games], [.. issues]);
    }

    /// <summary>ProgramData\Epic\UnrealEngineLauncher\LauncherInstalled.dat: lista mínima (carpeta y AppName) que el lanzador mantiene
    /// aunque falte el manifiesto .item. El nombre visible sale de la carpeta.</summary>
    private void ReadLauncherInstalled(List<LibraryGame> games, List<string> issues)
    {
        if (launcherInstalled is null || !File.Exists(launcherInstalled)) return;
        try
        {
            using var doc = JsonDocument.Parse(LibraryDiscovery.ReadManifest(launcherInstalled));
            if (!doc.RootElement.TryGetProperty("InstallationList", out var list) || list.ValueKind != JsonValueKind.Array) return;
            foreach (var item in list.EnumerateArray())
            {
                string? Read(string key) => item.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                var id = Read("AppName"); var root = LibraryDiscovery.LocalPath(Read("InstallLocation"));
                if (string.IsNullOrWhiteSpace(id) || root is null || !Directory.Exists(root) || id.StartsWith("UE_", StringComparison.OrdinalIgnoreCase)) continue;
                games.Add(new LibraryGame(id, Path.GetFileName(root), Name, root, null));
            }
        }
        catch (Exception error) when (LibraryDiscovery.IsReadError(error)) { issues.Add($"Epic · LauncherInstalled.dat: {error.Message}"); }
    }
}

[SupportedOSPlatform("windows")]
public sealed class GogSource : IGameSource
{
    public string Name => "GOG";
    public LibraryScan Scan()
    {
        var games = new List<LibraryGame>();
        var issues = new List<string>();
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            try
            {
                using var registry = RegistryKey.OpenBaseKey(hive, view);
                using var library = registry.OpenSubKey(@"SOFTWARE\GOG.com\Games", writable: false);
                if (library is null) continue;
                foreach (var id in library.GetSubKeyNames())
                {
                    using var game = library.OpenSubKey(id, writable: false);
                    var root = LibraryDiscovery.LocalPath(game?.GetValue("path") as string);
                    var name = game?.GetValue("gameName") as string;
                    if (root is null || !Directory.Exists(root) || string.IsNullOrWhiteSpace(name)) continue;
                    games.Add(new LibraryGame(id, name, Name, root,
                        LibraryDiscovery.Executable(root, game?.GetValue("exe") as string)));
                }
            }
            catch (Exception error) when (LibraryDiscovery.IsReadError(error))
            { issues.Add($"GOG · {hive}/{view}: {error.Message}"); }
        }
        return new LibraryScan([.. games], [.. issues]);
    }
}

[SupportedOSPlatform("windows")]
public static class WindowsLibraryLocations
{
    public static IGameSource[] DefaultSources()
    {
        var roots = new[] { ReadSteamRoot(RegistryHive.CurrentUser, RegistryView.Default, "SteamPath"),
            ReadSteamRoot(RegistryHive.LocalMachine, RegistryView.Registry32, "InstallPath"),
            ReadSteamRoot(RegistryHive.LocalMachine, RegistryView.Registry64, "InstallPath"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam") }.OfType<string>().Concat(SteamLibrariesOnDrives());
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var epicData = ReadValue(RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\Epic Games\EpicGamesLauncher", "AppDataPath")
            ?? Path.Combine(programData, "Epic", "EpicGamesLauncher", "Data");
        return [new SteamSource(roots),
            new EpicSource(Path.Combine(epicData, "Manifests"), Path.Combine(programData, "Epic", "UnrealEngineLauncher", "LauncherInstalled.dat")),
            new GogSource(), new XboxSource(), new EaSource(), new UbisoftSource(), new BattleNetSource(), new RockstarSource()];
    }

    /// <summary>Bibliotecas de Steam en carpetas habituales de cada disco: por si libraryfolders.vdf no se puede leer o Steam está
    /// en una ruta que el registro no refleja (reinstalaciones, discos movidos). Solo se usan si tienen «steamapps».</summary>
    private static IEnumerable<string> SteamLibrariesOnDrives()
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            string root;
            try { if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue; root = drive.RootDirectory.FullName; }
            catch (Exception error) when (LibraryDiscovery.IsReadError(error)) { continue; }
            foreach (var candidate in new[] { "SteamLibrary", "Steam", @"Program Files (x86)\Steam", @"Program Files\Steam", @"Games\Steam", @"Games\SteamLibrary", @"Juegos\Steam", @"Juegos\SteamLibrary" })
            {
                var path = Path.Combine(root, candidate);
                if (Directory.Exists(Path.Combine(path, "steamapps"))) yield return path;
            }
        }
    }

    private static string? ReadValue(RegistryHive hive, RegistryView view, string key, string value)
    {
        try
        {
            using var registry = RegistryKey.OpenBaseKey(hive, view);
            using var sub = registry.OpenSubKey(key, writable: false);
            return sub?.GetValue(value) as string is { Length: > 0 } text ? text : null;
        }
        catch (Exception error) when (LibraryDiscovery.IsReadError(error)) { return null; }
    }

    private static string? ReadSteamRoot(RegistryHive hive, RegistryView view, string value)
    {
        try
        {
            using var registry = RegistryKey.OpenBaseKey(hive, view);
            using var steam = registry.OpenSubKey(@"Software\Valve\Steam", writable: false);
            return steam?.GetValue(value) as string;
        }
        catch (Exception error) when (LibraryDiscovery.IsReadError(error)) { return null; }
    }
}

public sealed class ManualSource(string settingsFile) : IGameSource
{
    public string Name => "Manual";

    public LibraryScan Scan() => new(ReadPaths().Select(ToGame).OfType<LibraryGame>().ToArray(), []);

    public void Add(string executable)
    {
        var game = ToGame(executable) ?? throw new IOException("Selecciona un ejecutable local existente.");
        var paths = ReadPaths().Append(game.Executable!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var directory = Path.GetDirectoryName(settingsFile)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, "manual-" + Guid.NewGuid().ToString("N") + ".tmp");
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, paths);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, settingsFile, overwrite: true);
    }

    private string[] ReadPaths() => File.Exists(settingsFile)
        ? JsonSerializer.Deserialize<string[]>(LibraryDiscovery.ReadManifest(settingsFile)) ?? [] : [];

    private static LibraryGame? ToGame(string path)
    {
        var full = LibraryDiscovery.LocalPath(path);
        if (full is null || !File.Exists(full) || !Path.GetExtension(full).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            return null;
        return new LibraryGame(full, Path.GetFileNameWithoutExtension(full), "Manual", Path.GetDirectoryName(full)!, full);
    }
}

/// <summary>API gráfica de un juego. Las importaciones normales del ejecutable no bastan: muchos juegos DirectX 12 (Unreal Engine y otros)
/// cargan d3d12.dll de forma diferida o en tiempo de ejecución y solo importan d3d11.dll de forma estática, así que salían como DirectX 11.
/// Inspect (barato, para elegir el ejecutable) mira las importaciones estáticas y las diferidas; InspectGame añade lo que hay junto al
/// ejecutable (Agility SDK de DirectX 12, FidelityFX para DX12 o Vulkan) y, si aún no hay DX12, busca «d3d12.dll» dentro del ejecutable.</summary>
public static class ApiDetection
{
    private static readonly Dictionary<string, string> Imports = new(StringComparer.OrdinalIgnoreCase)
    {
        ["d3d11.dll"] = "DirectX 11", ["d3d12.dll"] = "DirectX 12", ["vulkan-1.dll"] = "Vulkan"
    };

    public static ApiEvidence[] Inspect(string? executable)
    {
        if (executable is null) return [];
        try
        {
            using var stream = new FileStream(executable, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var pe = new PEReader(stream);
            var evidence = new Dictionary<string, string>(StringComparer.Ordinal);
            void Add(string dll, string source) { if (Imports.TryGetValue(dll, out var api)) evidence.TryAdd(api, source); }
            string? Name(int rva)
            {
                if (rva <= 0) return null;
                var names = pe.GetSectionData(rva);
                if (names.Length == 0) return null;
                var raw = names.GetContent(0, Math.Min(names.Length, 256)).AsSpan();
                var end = raw.IndexOf((byte)0);
                return end > 0 ? Encoding.ASCII.GetString(raw[..end]) : null;
            }
            // Importaciones estáticas (descriptores de 20 bytes; el nombre en el desplazamiento 12)
            var imports = pe.PEHeaders.PEHeader?.ImportTableDirectory ?? default;
            if (imports.RelativeVirtualAddress != 0 && imports.Size >= 20)
            {
                var table = pe.GetSectionData(imports.RelativeVirtualAddress);
                var count = Math.Min(Math.Min(imports.Size, table.Length) / 20, 4096);
                for (var index = 0; index < count; index++)
                {
                    var descriptor = table.GetContent(index * 20, 20).AsSpan();
                    var nameRva = BinaryPrimitives.ReadInt32LittleEndian(descriptor[12..]);
                    if (nameRva == 0) break;
                    if (Name(nameRva) is { } dll) Add(dll, "Importación estática PE");
                }
            }
            // Importaciones diferidas (descriptores de 32 bytes; el nombre en el desplazamiento 4; bit 0 de los atributos = RVA)
            var delayed = pe.PEHeaders.PEHeader?.DelayImportTableDirectory ?? default;
            if (delayed.RelativeVirtualAddress != 0 && delayed.Size >= 32)
            {
                var table = pe.GetSectionData(delayed.RelativeVirtualAddress);
                var count = Math.Min(Math.Min(delayed.Size, table.Length) / 32, 4096);
                var imageBase = (long)(pe.PEHeaders.PEHeader?.ImageBase ?? 0);
                for (var index = 0; index < count; index++)
                {
                    var descriptor = table.GetContent(index * 32, 32).AsSpan();
                    var attributes = BinaryPrimitives.ReadInt32LittleEndian(descriptor);
                    var name = BinaryPrimitives.ReadInt32LittleEndian(descriptor[4..]);
                    if (name == 0) break;
                    var rva = (attributes & 1) != 0 ? name : (int)(name - imageBase);
                    if (Name(rva) is { } dll) Add(dll, "Importación diferida PE");
                }
            }
            return [.. evidence.OrderBy(e => e.Key).Select(e => new ApiEvidence(e.Key, EvidenceLevel.Probable, e.Value))];
        }
        catch (Exception error) when (LibraryDiscovery.IsReadError(error) || error is BadImageFormatException || error is ArgumentOutOfRangeException)
        { return []; }
    }

    /// <summary>API del juego con todas las pistas (una vez por juego). Si hay pistas de DirectX 12 y de DirectX 11, aparecen las dos:
    /// quien la usa elige DirectX 12 (el juego puede arrancar en cualquiera de los dos modos).</summary>
    public static ApiEvidence[] InspectGame(string? executable)
    {
        if (executable is null) return [];
        var list = Inspect(executable).ToList();
        void Add(string api, string source) { if (!list.Any(e => e.Name == api)) list.Add(new(api, EvidenceLevel.Probable, source)); }
        try
        {
            var dir = Path.GetDirectoryName(executable)!;
            if (File.Exists(Path.Combine(dir, "D3D12", "D3D12Core.dll"))) Add("DirectX 12", "Agility SDK de DirectX 12 junto al juego");
            foreach (var file in Directory.EnumerateFiles(dir, "*.dll", new EnumerationOptions { IgnoreInaccessible = true }).Select(Path.GetFileName).OfType<string>())
            {
                var f = file.ToLowerInvariant();
                if (f.StartsWith("amd_fidelityfx") || f.StartsWith("ffx_"))
                {
                    if (f.Contains("_dx12")) Add("DirectX 12", $"{file} (FidelityFX para DirectX 12) junto al juego");
                    else if (f.Contains("_vk")) Add("Vulkan", $"{file} (FidelityFX para Vulkan) junto al juego");
                    else if (f.Contains("_dx11")) Add("DirectX 11", $"{file} (FidelityFX para DirectX 11) junto al juego");
                }
            }
            // Unity lleva todas las API en UnityPlayer.dll y elige al arrancar: su ejecutable no dice cuál usa el juego
            var unity = File.Exists(Path.Combine(dir, "UnityPlayer.dll")) || Directory.Exists(Path.Combine(dir, Path.GetFileNameWithoutExtension(executable) + "_Data"));
            if (!unity && !list.Any(e => e.Name == "DirectX 12") && Mentions(executable, "d3d12.dll")) Add("DirectX 12", "El ejecutable carga d3d12.dll en tiempo de ejecución");
            if (!unity && !list.Any(e => e.Name is "Vulkan" or "DirectX 12") && Mentions(executable, "vulkan-1.dll")) Add("Vulkan", "El ejecutable carga vulkan-1.dll en tiempo de ejecución");
        }
        catch (Exception error) when (LibraryDiscovery.IsReadError(error)) { }
        return [.. list.OrderBy(e => e.Name)];
    }

    /// <summary>¿El ejecutable contiene ese nombre (ASCII o UTF-16, sin distinguir mayúsculas)? Lectura por bloques, hasta 1 GB.</summary>
    private static bool Mentions(string executable, string text)
    {
        var ascii = Encoding.ASCII.GetBytes(text.ToLowerInvariant());
        var wide = Encoding.Unicode.GetBytes(text.ToLowerInvariant());
        using var stream = new FileStream(executable, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        if (stream.Length > 1L << 30) return false;
        var buffer = new byte[(1 << 22) + wide.Length];
        var keep = 0;
        int read;
        while ((read = stream.Read(buffer, keep, buffer.Length - keep)) > 0)
        {
            var span = buffer.AsSpan(0, keep + read);
            for (var i = 0; i < span.Length; i++) if (span[i] is >= (byte)'A' and <= (byte)'Z') span[i] = (byte)(span[i] + 32);
            if (span.IndexOf(ascii) >= 0 || span.IndexOf(wide) >= 0) return true;
            keep = Math.Min(wide.Length, span.Length);
            span[^keep..].CopyTo(buffer);
        }
        return false;
    }
}
