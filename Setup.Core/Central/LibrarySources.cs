// ILION CENTRAL-01 — Más plataformas para encontrar la carpeta de cada juego (rutas de jugadores a los que no se les encontraba).
// Reimplementado con código propio a partir de cómo lo resuelven DLSS Swapper (beeradmoore/dlss-swapper, src/Data/*Library.cs) y
// OptiScaler Client (Optiscaler-Client/Optiscaler-Client, Services/*Scanner.cs):
//   · Xbox / Game Pass: archivo .GamingRoot de cada disco (cabecera RGBX + carpeta en UTF-16) o «XboxGames» por defecto;
//     cada juego trae Content\MicrosoftGame.config con su nombre y su ejecutable.
//   · EA App: claves de desinstalación cuyo UninstallString es «EAInstaller …Cleanup.exe» y la rama «Electronic Arts\EA Games».
//   · Ubisoft Connect: SOFTWARE\Ubisoft\Launcher\Installs\<id>\InstallDir (vista de 32 bits) y el nombre en «Uplay Install <id>».
//   · Battle.net: claves de desinstalación con editor «Blizzard Entertainment».
//   · Rockstar Games Launcher: SOFTWARE\Rockstar Games\<juego>\InstallFolder.
// Todas: solo lectura, rutas locales existentes, y un error en una fuente nunca impide leer las demás.
using System.Runtime.Versioning;
using System.Text;
using System.Xml.Linq;
using Microsoft.Win32;

namespace Ilion.Client;

[SupportedOSPlatform("windows")]
internal static class RegistryWalk
{
    public static readonly (RegistryHive Hive, RegistryView View)[] All =
        [(RegistryHive.LocalMachine, RegistryView.Registry64), (RegistryHive.LocalMachine, RegistryView.Registry32),
         (RegistryHive.CurrentUser, RegistryView.Registry64), (RegistryHive.CurrentUser, RegistryView.Registry32)];

    /// <summary>Cada clave de desinstalación de Windows (todas las vistas): (id, clave abierta).</summary>
    public static IEnumerable<(string Id, RegistryKey Key)> Uninstall(List<string> issues, string source)
    {
        foreach (var (hive, view) in All)
        {
            RegistryKey? root = null, list = null;
            try
            {
                root = RegistryKey.OpenBaseKey(hive, view);
                list = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", writable: false);
            }
            catch (Exception error) when (LibraryDiscovery.IsReadError(error)) { issues.Add($"{source} · {hive}/{view}: {error.Message}"); }
            if (list is null) { root?.Dispose(); continue; }
            foreach (var id in list.GetSubKeyNames())
            {
                RegistryKey? key = null;
                try { key = list.OpenSubKey(id, writable: false); }
                catch (Exception error) when (LibraryDiscovery.IsReadError(error)) { }
                if (key is null) continue;
                using (key) yield return (id, key);
            }
            list.Dispose(); root?.Dispose();
        }
    }

    public static string? Text(RegistryKey key, string name) => (key.GetValue(name) as string)?.Trim().Trim('"');
}

/// <summary>Xbox / Game Pass (juegos de la Microsoft Store instalados con la app Xbox).</summary>
[SupportedOSPlatform("windows")]
public sealed class XboxSource(IEnumerable<string>? libraries = null) : IGameSource
{
    public string Name => "Xbox";
    public LibraryScan Scan()
    {
        var games = new List<LibraryGame>(); var issues = new List<string>();
        foreach (var folder in libraries ?? Libraries(issues))
        {
            string[] entries;
            try { entries = Directory.GetDirectories(folder); }
            catch (Exception error) when (LibraryDiscovery.IsReadError(error)) { issues.Add($"Xbox · {folder}: {error.Message}"); continue; }
            foreach (var game in entries)
            {
                try
                {
                    var content = Path.Combine(game, "Content");
                    var root = Directory.Exists(content) ? content : game;
                    var config = Path.Combine(root, "MicrosoftGame.config");
                    string name = Path.GetFileName(game); string? id = null; string? exe = null;
                    if (File.Exists(config))
                    {
                        var xml = XDocument.Parse(LibraryDiscovery.ReadManifest(config));
                        var top = xml.Root;
                        id = top?.Element("Identity")?.Attribute("Name")?.Value;
                        var display = top?.Element("ShellVisuals")?.Attribute("DefaultDisplayName")?.Value;
                        if (!string.IsNullOrWhiteSpace(display) && !display.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase)) name = display;
                        exe = top?.Element("ExecutableList")?.Elements("Executable").Select(e => e.Attribute("Name")?.Value)
                            .FirstOrDefault(n => n is not null && !n.Contains("gamelaunchhelper", StringComparison.OrdinalIgnoreCase));
                    }
                    else if (!Directory.EnumerateFiles(root, "*.exe", SearchOption.AllDirectories).Any()) continue; // carpeta vacía o a medio instalar
                    var local = LibraryDiscovery.LocalPath(root);
                    if (local is null) continue;
                    games.Add(new LibraryGame(id ?? Path.GetFileName(game), name, Name, local, LibraryDiscovery.Executable(local, exe)));
                }
                catch (Exception error) when (LibraryDiscovery.IsReadError(error) || error is System.Xml.XmlException)
                { issues.Add($"Xbox · {Path.GetFileName(game)}: {error.Message}"); }
            }
        }
        return new LibraryScan([.. games], [.. issues]);
    }

    /// <summary>Carpetas que indica un archivo .GamingRoot: «RGBX» + número de carpetas (uint32) + rutas UTF-16 terminadas en \0,
    /// relativas a la raíz del disco.</summary>
    public static string[] GamingRoot(byte[] bytes, string driveRoot)
    {
        if (bytes.Length <= 8 || bytes[0] != 'R' || bytes[1] != 'G' || bytes[2] != 'B' || bytes[3] != 'X') return [];
        return [.. Encoding.Unicode.GetString(bytes, 8, (bytes.Length - 8) & ~1).Split('\0', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(relative => Path.Combine(driveRoot, relative.TrimStart('\\')))];
    }

    /// <summary>Carpetas de juegos de la app Xbox: la que indica .GamingRoot en cada disco y, si no hay, X:\XboxGames.</summary>
    private static IEnumerable<string> Libraries(List<string> issues)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable) || !drive.IsReady) continue;
                var marker = Path.Combine(drive.RootDirectory.FullName, ".GamingRoot");
                if (File.Exists(marker))
                    foreach (var folder in GamingRoot(File.ReadAllBytes(marker), drive.RootDirectory.FullName))
                        if (Directory.Exists(folder)) found.Add(folder);
                var standard = Path.Combine(drive.RootDirectory.FullName, "XboxGames");
                if (Directory.Exists(standard)) found.Add(standard);
            }
            catch (Exception error) when (LibraryDiscovery.IsReadError(error)) { issues.Add($"Xbox · {drive.Name}: {error.Message}"); }
        }
        return found;
    }
}

/// <summary>EA App (y juegos antiguos de Origin).</summary>
[SupportedOSPlatform("windows")]
public sealed class EaSource : IGameSource
{
    public string Name => "EA";
    public LibraryScan Scan()
    {
        var games = new List<LibraryGame>(); var issues = new List<string>();
        foreach (var (id, key) in RegistryWalk.Uninstall(issues, Name))
        {
            var uninstall = RegistryWalk.Text(key, "UninstallString") ?? "";
            if (!uninstall.Contains("EAInstaller", StringComparison.OrdinalIgnoreCase) || !uninstall.Contains("Cleanup.exe", StringComparison.OrdinalIgnoreCase)) continue;
            Add(games, id, RegistryWalk.Text(key, "DisplayName"), RegistryWalk.Text(key, "InstallLocation"));
        }
        foreach (var (hive, view) in RegistryWalk.All)
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var list = root.OpenSubKey(@"SOFTWARE\Electronic Arts\EA Games", writable: false);
                if (list is null) continue;
                foreach (var sub in list.GetSubKeyNames())
                {
                    using var key = list.OpenSubKey(sub, writable: false);
                    if (key is null) continue;
                    Add(games, sub, RegistryWalk.Text(key, "DisplayName") ?? sub, RegistryWalk.Text(key, "Install Dir"));
                }
            }
            catch (Exception error) when (LibraryDiscovery.IsReadError(error)) { issues.Add($"EA · {hive}/{view}: {error.Message}"); }
        }
        return new LibraryScan([.. games], [.. issues]);
    }

    private void Add(List<LibraryGame> games, string id, string? name, string? folder)
    {
        var root = LibraryDiscovery.LocalPath(folder);
        if (root is null || string.IsNullOrWhiteSpace(name) || !Directory.Exists(root)) return;
        if (games.Any(g => g.Root.Equals(root, StringComparison.OrdinalIgnoreCase))) return;
        games.Add(new LibraryGame(id, name, Name, root, null));
    }
}

/// <summary>Ubisoft Connect.</summary>
[SupportedOSPlatform("windows")]
public sealed class UbisoftSource : IGameSource
{
    public string Name => "Ubisoft";
    public LibraryScan Scan()
    {
        var games = new List<LibraryGame>(); var issues = new List<string>();
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, key) in RegistryWalk.Uninstall(issues, Name))
            if (id.StartsWith("Uplay Install ", StringComparison.OrdinalIgnoreCase) && RegistryWalk.Text(key, "DisplayName") is { Length: > 0 } display)
                names[id["Uplay Install ".Length..].Trim()] = display;
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var installs = root.OpenSubKey(@"SOFTWARE\Ubisoft\Launcher\Installs", writable: false);
                if (installs is null) continue;
                foreach (var id in installs.GetSubKeyNames().Where(i => uint.TryParse(i, out _)))
                {
                    using var key = installs.OpenSubKey(id, writable: false);
                    var folder = LibraryDiscovery.LocalPath(key is null ? null : RegistryWalk.Text(key, "InstallDir")?.Replace('/', '\\'));
                    if (folder is null || !Directory.Exists(folder) || games.Any(g => g.Id == id)) continue;
                    games.Add(new LibraryGame(id, names.TryGetValue(id, out var n) ? n : Path.GetFileName(folder), Name, folder, null));
                }
            }
            catch (Exception error) when (LibraryDiscovery.IsReadError(error)) { issues.Add($"Ubisoft · {view}: {error.Message}"); }
        }
        return new LibraryScan([.. games], [.. issues]);
    }
}

/// <summary>Battle.net (Blizzard y Activision: Call of Duty, Diablo…).</summary>
[SupportedOSPlatform("windows")]
public sealed class BattleNetSource : IGameSource
{
    public string Name => "Battle.net";
    public LibraryScan Scan()
    {
        var games = new List<LibraryGame>(); var issues = new List<string>();
        foreach (var (id, key) in RegistryWalk.Uninstall(issues, Name))
        {
            var publisher = RegistryWalk.Text(key, "Publisher") ?? "";
            var name = RegistryWalk.Text(key, "DisplayName");
            if (!publisher.Contains("Blizzard Entertainment", StringComparison.OrdinalIgnoreCase) || name is null || name.Contains("Battle.net", StringComparison.OrdinalIgnoreCase)) continue;
            var root = LibraryDiscovery.LocalPath(RegistryWalk.Text(key, "InstallLocation"));
            if (root is null || !Directory.Exists(root) || games.Any(g => g.Root.Equals(root, StringComparison.OrdinalIgnoreCase))) continue;
            games.Add(new LibraryGame(id, name.Replace(" (PTR)", ""), Name, root, null));
        }
        return new LibraryScan([.. games], [.. issues]);
    }
}

/// <summary>Rockstar Games Launcher.</summary>
[SupportedOSPlatform("windows")]
public sealed class RockstarSource : IGameSource
{
    public string Name => "Rockstar";
    public LibraryScan Scan()
    {
        var games = new List<LibraryGame>(); var issues = new List<string>();
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var list = root.OpenSubKey(@"SOFTWARE\Rockstar Games", writable: false);
                if (list is null) continue;
                foreach (var sub in list.GetSubKeyNames())
                {
                    if (sub.Contains("Launcher", StringComparison.OrdinalIgnoreCase) || sub.Contains("Social Club", StringComparison.OrdinalIgnoreCase)) continue;
                    using var key = list.OpenSubKey(sub, writable: false);
                    var folder = LibraryDiscovery.LocalPath(key is null ? null : RegistryWalk.Text(key, "InstallFolder"));
                    if (folder is null || !Directory.Exists(folder) || games.Any(g => g.Root.Equals(folder, StringComparison.OrdinalIgnoreCase))) continue;
                    games.Add(new LibraryGame(sub, sub, Name, folder, null));
                }
            }
            catch (Exception error) when (LibraryDiscovery.IsReadError(error)) { issues.Add($"Rockstar · {view}: {error.Message}"); }
        }
        return new LibraryScan([.. games], [.. issues]);
    }
}
