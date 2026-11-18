// ILION CENTRAL-01 0.9 — Localiza el ejecutable REAL del juego (y con él la carpeta donde debe ir OptiScaler).
// Criterios equivalentes a los de OptiScaler Client (reimplementados, no copiados):
//   1) Unreal Engine: si hay ejecutables en <Proyecto>\Binaries\Win64, solo se elige entre ellos (convención del motor).
//   2) Se descartan lanzadores, crash reporters, instaladores, redistribuibles y auxiliares.
//   3) Puntuación: nombre parecido al del juego, tamaño, DLL de escalado al lado, API gráfica importada y x64.
using System.Reflection.PortableExecutable;

namespace Ilion.Client;

public sealed record ResolvedExecutable(string Path, int Score, string Reason);

public static class ExecutableResolver
{
    private static readonly string[] Excluded = ["crash", "redist", "setup", "launcher", "unrealcefsubprocess", "prerequisite", "unins", "vc_redist", "vcredist",
        "dxsetup", "dxwebsetup", "easyanticheat", "beservice", "battleye", "unitycrashhandler", "qtwebengine", "updater", "helper", "reporter", "install",
        "cefprocess", "steamerrorreporter", "touchup", "cleanup", "dotnet", "oalinst", "physx", "uplay", "eadesktop", "epicwebhelper", "renderdoc"];
    private static readonly string[] SkippedFolders = ["_commonredist", "redist", "redistributables", "directx", "vcredist", "__installer", "installer", "support",
        "easyanticheat", "battleye", "_redist", "prereqs", "commonredist", "engine\\extras", "thirdparty"];
    private static readonly string[] UpscalerHints = ["nvngx", "dlss", "sl.interposer", "amd_fidelityfx", "ffx_", "fsr", "libxess", "xess"];

    public static ResolvedExecutable? Find(string root, string gameName)
    {
        if (!Directory.Exists(root)) return null;
        string[] all;
        try
        {
            all = Directory.EnumerateFiles(root, "*.exe", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 8, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
                .Where(p => !InSkippedFolder(System.IO.Path.GetRelativePath(root, p).ToLowerInvariant()))
                .Where(p => !Excluded.Any(x => System.IO.Path.GetFileNameWithoutExtension(p).Contains(x, StringComparison.OrdinalIgnoreCase)))
                .ToArray();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
        if (all.Length == 0) return null;

        // Regla del motor: Binaries\Win64 manda (el .exe de la raíz de un juego Unreal suele ser un lanzador pequeño)
        var unreal = all.Where(IsBinariesWin64).ToArray();
        var pool = unreal.Length > 0 ? unreal : all;
        var gameLetters = Letters(gameName);
        ResolvedExecutable? best = null;
        foreach (var exe in pool)
        {
            var score = 0; var why = new List<string>();
            var name = System.IO.Path.GetFileNameWithoutExtension(exe);
            var letters = Letters(name.Replace("-Win64-Shipping", "", StringComparison.OrdinalIgnoreCase).Replace("-WinGDK-Shipping", "", StringComparison.OrdinalIgnoreCase));
            if (letters.Length > 2 && gameLetters.Length > 2 && (letters.Contains(gameLetters, StringComparison.OrdinalIgnoreCase) || gameLetters.Contains(letters, StringComparison.OrdinalIgnoreCase))) { score += 15; why.Add("nombre del juego"); }
            if (IsBinariesWin64(exe)) { score += 5; why.Add("Binaries\\Win64 (Unreal)"); }
            if (name.EndsWith("-Shipping", StringComparison.OrdinalIgnoreCase)) { score += 5; why.Add("compilación Shipping"); }
            long size = 0; try { size = new FileInfo(exe).Length; } catch (IOException) { }
            if (size > 5 * 1024 * 1024) { score += 10; why.Add("tamaño de ejecutable principal"); }
            try
            {
                if (Directory.EnumerateFiles(System.IO.Path.GetDirectoryName(exe)!, "*.dll").Any(d => UpscalerHints.Any(h => System.IO.Path.GetFileName(d).Contains(h, StringComparison.OrdinalIgnoreCase))))
                { score += 25; why.Add("DLL de escalado junto al ejecutable"); }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            var machine = MachineOf(exe);
            if (machine == Machine.Amd64) score += 5; else if (machine == Machine.I386) score -= 10;
            if (ApiDetection.Inspect(exe).Length > 0) { score += 10; why.Add("importa DirectX o Vulkan"); }
            // Desempate estable: mayor tamaño
            if (best is null || score > best.Score || (score == best.Score && size > SafeSize(best.Path)))
                best = new(exe, score, why.Count == 0 ? "único candidato" : string.Join(", ", why));
        }
        return best;
    }

    public static bool IsBinariesWin64(string exe)
    {
        var dir = System.IO.Path.GetDirectoryName(exe);
        return dir is not null && System.IO.Path.GetFileName(dir).Equals("Win64", StringComparison.OrdinalIgnoreCase)
            && System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(dir) ?? "").Equals("Binaries", StringComparison.OrdinalIgnoreCase)
            && !dir.Contains(@"\Engine\Binaries\", StringComparison.OrdinalIgnoreCase); // herramientas del motor, no el juego
    }

    private static bool InSkippedFolder(string relative)
    {
        var folders = relative.Split('\\')[..^1];
        return folders.Any(f => SkippedFolders.Contains(f)) || relative.Contains(@"engine\extras") || relative.Contains(@"\thirdparty\");
    }

    private static string Letters(string text) => new(text.Where(char.IsLetterOrDigit).ToArray());
    private static long SafeSize(string path) { try { return new FileInfo(path).Length; } catch (IOException) { return 0; } }
    private static Machine MachineOf(string exe)
    {
        try { using var s = File.OpenRead(exe); using var pe = new PEReader(s); return pe.PEHeaders.CoffHeader.Machine; }
        catch (Exception error) when (error is IOException or BadImageFormatException or UnauthorizedAccessException) { return Machine.Unknown; }
    }
}
