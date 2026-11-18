using System.Diagnostics;
using System.Reflection.PortableExecutable;

namespace Ilion.Client;

public sealed record OptiScalerInstallation(string? Binary, string? Method, string? Version,
    string? IniPath, string? IniText, bool IlionConfiguration, bool Ambiguous)
{
    public string Status => Ambiguous ? "Varias instalaciones posibles"
        : Method is not null ? "OptiScaler instalado · carga en ejecución sin comprobar"
        : Binary is not null ? "Binario identificado · método de carga sin confirmar"
        : IniPath is not null ? "Configuración encontrada · instalación sin confirmar" : "Instalación no detectada";
    public string Edition => IlionConfiguration ? "Configuración ILION · edición del binario sin confirmar"
        : "Edición del binario sin confirmar";
}

public static class OptiScalerDetection
{
    private static readonly string[] Names = ["dxgi.dll", "winmm.dll", "d3d12.dll", "dbghelp.dll",
        "version.dll", "wininet.dll", "winhttp.dll", "nvngx.dll", "OptiScaler.asi", "OptiScaler.dll"];

    public static OptiScalerInstallation Inspect(string root, string? executable)
    {
        var binaries = new List<(string Path, string? Method, string? Version)>();
        var configs = new List<(string Path, string Text, bool Ilion)>();
        var executableDirectory = executable is null ? null : Path.GetDirectoryName(executable);
        foreach (var directory in new[] { executableDirectory, root }.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!IsOrdinaryPath(root, directory, directory: true)) continue;
            foreach (var name in Names)
            {
                var path = Path.Combine(directory, name);
                if (!IsOrdinaryPath(root, path, directory: false)) continue;
                try
                {
                    using var stream = File.OpenRead(path);
                    using var pe = new PEReader(stream);
                    if (pe.PEHeaders.PEHeader is null || !pe.PEHeaders.IsDll) continue;
                    var version = FileVersionInfo.GetVersionInfo(path);
                    if (version.OriginalFilename?.Equals("OptiScaler.dll", StringComparison.OrdinalIgnoreCase) != true &&
                        version.ProductName?.Equals("OptiScaler", StringComparison.OrdinalIgnoreCase) != true) continue;
                    var method = name == "OptiScaler.dll" || !directory.Equals(executableDirectory, StringComparison.OrdinalIgnoreCase)
                        ? null : name == "OptiScaler.asi" ? "Plugin ASI · requiere cargador" : $"Proxy {name}";
                    binaries.Add((path, method, version.ProductVersion ?? version.FileVersion));
                }
                catch (Exception error) when (LibraryDiscovery.IsReadError(error) ||
                    error is BadImageFormatException or System.ComponentModel.Win32Exception) { }
            }

            foreach (var path in new[] { Path.Combine(directory, "OptiScaler.ini"),
                Path.Combine(directory, "OptiScaler", "OptiScaler.ini"), Path.Combine(directory, "ILION", "OptiScaler.ini") })
            {
                if (!IsOrdinaryPath(root, path, directory: false)) continue;
                try
                {
                    using var stream = File.OpenRead(path);
                    if (stream.Length > 2 * 1024 * 1024) continue;
                    using var reader = new StreamReader(stream);
                    var text = reader.ReadToEnd();
                    var fields = IniDocument.Parse(text).Settings;
                    if (!fields.Any(field => field.Section.Equals("Upscalers", StringComparison.OrdinalIgnoreCase) &&
                        field.Key.Equals("Dx12Upscaler", StringComparison.OrdinalIgnoreCase))) continue;
                    var markers = fields.Select(field => field.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    configs.Add((path, text, markers.Contains("IlionVulkanBackend") && markers.Contains("IlionPalette")));
                }
                catch (Exception error) when (LibraryDiscovery.IsReadError(error)) { }
            }
        }
        var loaders = binaries.Where(binary => binary.Method is not null).ToArray();
        var binaryMatch = loaders.Length == 1 ? loaders[0] : binaries.Count == 1 ? binaries[0] : default;
        var paired = binaryMatch.Path is null ? [] : configs.Where(ini =>
            Path.GetDirectoryName(ini.Path)?.Equals(Path.GetDirectoryName(binaryMatch.Path), StringComparison.OrdinalIgnoreCase) == true).ToArray();
        var configuration = paired.Length == 1 ? paired[0] : configs.Count == 1 ? configs[0] : default;
        return new(binaryMatch.Path, binaryMatch.Method, binaryMatch.Version, configuration.Path, configuration.Text,
            configuration.Ilion, loaders.Length > 1 || (loaders.Length == 0 && binaries.Count > 1) ||
            (paired.Length != 1 && configs.Count > 1));
    }

    private static bool IsOrdinaryPath(string root, string path, bool directory)
    {
        var relative = Path.GetRelativePath(root, path);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar)) return false;
        try
        {
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) return false;
            var current = root;
            foreach (var part in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            {
                current = Path.Combine(current, part);
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
            }
            return (File.GetAttributes(path) & FileAttributes.Directory) != 0 == directory;
        }
        catch (Exception error) when (LibraryDiscovery.IsReadError(error)) { return false; }
    }
}
