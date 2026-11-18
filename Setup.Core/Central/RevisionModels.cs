using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;

namespace Ilion.Client;

public sealed record PageSlice<T>(T[] Items, int Index, int Count)
{
    public static PageSlice<T> Select(IReadOnlyList<T> items, int index, int pageSize, int selected = -1)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);
        var count = items.Count == 0 ? 0 : (items.Count - 1) / pageSize + 1;
        var page = count == 0 ? 0 : Math.Clamp(selected >= 0 ? selected / pageSize : index, 0, count - 1);
        return new PageSlice<T>(items.Skip(page * pageSize).Take(pageSize).ToArray(), page, count);
    }
}

public sealed record ExecutableEvidence(string? Path, string Architecture, string Source, ApiEvidence[] Apis);

public static class ExecutableDiscovery
{
    public static ExecutableEvidence Inspect(LibraryGame game)
    {
        var path = game.Executable;
        var source = path is null ? "Sin identificar" : game.Platform == "Manual" ? "Selección manual" : "Metadatos de la plataforma";
        var knownCyberpunk = game.Platform == "Steam" && game.Id == "1091500";
        if (path is null && knownCyberpunk)
        {
            path = LibraryDiscovery.Executable(game.Root, Path.Combine("bin", "x64", "Cyberpunk2077.exe"));
            if (path is not null) source = "Ruta documentada por CD PROJEKT RED";
        }
        // Ejecutable real: la plataforma no lo indica (Steam) o indica un lanzador; se busca como OptiScaler Client
        if (game.Platform != "Manual" && !knownCyberpunk && ExecutableResolver.Find(game.Root, game.Name) is { } found
            && (path is null || !File.Exists(path) || ExecutableResolver.IsBinariesWin64(found.Path) && !ExecutableResolver.IsBinariesWin64(path) || found.Score >= 40 && new FileInfo(path).Length < 5 * 1024 * 1024))
        {
            path = found.Path;
            source = "Detectado: " + found.Reason;
        }

        var machine = Machine.Unknown;
        if (path is not null)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var pe = new PEReader(stream);
                if (pe.PEHeaders.PEHeader is not null) machine = pe.PEHeaders.CoffHeader.Machine;
            }
            catch (Exception error) when (LibraryDiscovery.IsReadError(error) || error is BadImageFormatException) { }
        }
        if (machine == Machine.Unknown && game.Executable is null)
            return new ExecutableEvidence(null, "Desconocida", "Sin ejecutable verificable", []);

        var apis = ApiDetection.Inspect(path);
        if (knownCyberpunk && game.Executable is null && machine == Machine.Amd64 && !apis.Any(api => api.Name == "DirectX 12"))
            apis = [.. apis, new ApiEvidence("DirectX 12", EvidenceLevel.Probable, "Requisito oficial de CD PROJEKT RED; API activa no observada")];
        return new ExecutableEvidence(path, machine switch
        {
            Machine.Amd64 => "x64", Machine.I386 => "x86", Machine.Arm64 => "ARM64", _ => "Desconocida"
        }, source, apis);
    }
}

public sealed record GameProfile(string Name, string Text);

public sealed class GameProfileStore(string root)
{
    private static string GameId(string gameKey) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(gameKey)));
    private string GameFolder(string key) => Path.Combine(root, "sets", GameId(key));
    private string Legacy(string key) => Path.Combine(root, GameId(key) + ".ini");
    private string ActiveFile(string key) => Path.Combine(GameFolder(key), "active.txt");

    public string[] List(string key)
    {
        var folder = GameFolder(key);
        var names = Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "*.ini").Select(Path.GetFileNameWithoutExtension).OfType<string>() : [];
        return (File.Exists(Legacy(key)) ? names.Prepend("Perfil anterior") : names)
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public string? Active(string key)
    {
        if (!File.Exists(ActiveFile(key))) return null;
        var name = File.ReadAllText(ActiveFile(key));
        return List(key).FirstOrDefault(item => item.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    public GameProfile Read(string key, string name)
    {
        var path = ProfilePath(key, name);
        if (name.Equals("Perfil anterior", StringComparison.OrdinalIgnoreCase) && !File.Exists(path)) path = Legacy(key);
        if (new FileInfo(path).Length > 2 * 1024 * 1024) throw new IOException("El perfil supera 2 MiB.");
        return new GameProfile(name, File.ReadAllText(path));
    }

    public void Save(string key, string name, string text, bool overwrite)
    {
        if (Encoding.UTF8.GetByteCount(text) > 2 * 1024 * 1024) throw new IOException("El perfil supera 2 MiB.");
        var path = ProfilePath(key, name);
        if (!overwrite && List(key).Any(item => item.Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("Ya hay un perfil con ese nombre.");
        if (overwrite && File.Exists(path))
        {
            var previous = Read(key, name).Text;
            if (previous == text) return;
            Write(BackupPath(key, name), previous, overwrite: true);
        }
        Write(path, text, overwrite);
    }

    public bool CanRestore(string key, string name) => File.Exists(ProfilePath(key, name)) && File.Exists(BackupPath(key, name));

    public GameProfile Restore(string key, string name)
    {
        if (!CanRestore(key, name)) throw new IOException("No hay una copia anterior de este perfil.");
        var backup = BackupPath(key, name);
        if (new FileInfo(backup).Length > 2 * 1024 * 1024) throw new IOException("La copia del perfil supera 2 MiB.");
        var original = File.ReadAllText(backup);
        Save(key, name, original, overwrite: true);
        return new GameProfile(name, original);
    }

    private string BackupPath(string key, string name) => Path.Combine(GameFolder(key), "Backups", Path.GetFileName(ProfilePath(key, name)));

    public void Activate(string key, string name)
    {
        _ = Read(key, name);
        Write(ActiveFile(key), name, overwrite: true);
    }

    public void Duplicate(string key, string name, string newName) => Save(key, newName, Read(key, name).Text, overwrite: false);

    public void Rename(string key, string name, string newName)
    {
        if (name.Equals(newName, StringComparison.OrdinalIgnoreCase)) throw new IOException("Escribe un nombre diferente.");
        var source = ProfilePath(key, name);
        if (!File.Exists(source)) throw new IOException("Duplica primero el perfil heredado para conservar su original.");
        if (List(key).Any(item => item.Equals(newName, StringComparison.OrdinalIgnoreCase))) throw new IOException("Ya hay un perfil con ese nombre.");
        var active = Active(key) == name;
        File.Move(source, ProfilePath(key, newName));
        if (File.Exists(BackupPath(key, name))) File.Move(BackupPath(key, name), BackupPath(key, newName), overwrite: true);
        if (active) Activate(key, newName);
    }

    public void Delete(string key, string name)
    {
        var path = ProfilePath(key, name);
        if (!File.Exists(path)) throw new IOException("La copia heredada se conserva. Puedes duplicarla para editarla.");
        var active = Active(key) == name;
        File.Delete(path);
        if (File.Exists(BackupPath(key, name))) File.Delete(BackupPath(key, name));
        if (active) File.Delete(ActiveFile(key));
    }

    private string ProfilePath(string key, string name)
    {
        if (name.Length is < 1 or > 55 || name != name.Trim() ||
            !name.All(character => char.IsLetterOrDigit(character) || character is ' ' or '_' or '-') ||
            System.Text.RegularExpressions.Regex.IsMatch(name, @"^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            throw new ArgumentException("Usa de 1 a 55 letras, números, espacios o guiones para nombrar el perfil.");
        return Path.Combine(GameFolder(key), name + ".ini");
    }

    private static void Write(string path, string text, bool overwrite)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, text);
            File.Move(temporary, path, overwrite);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
