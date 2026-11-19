// ILION CENTRAL-01 — Regla de las recetas: cada mod conoce TODOS los archivos que necesita en la carpeta del juego,
// los suyos (no oficiales) y los oficiales que lo complementan (NVIDIA, Intel, AMD). Al instalar, CENTRAL coloca los que
// faltan desde su origen oficial (misma cadena verificada que «Componentes»: SHA-256 de GitHub + Authenticode del fabricante)
// o desde una copia firmada que ya esté en el PC; si no puede, lo dice con claridad y con qué hacer. Nunca bloquea en silencio.
using System.Diagnostics;

namespace Ilion.Client;

/// <summary>Archivo oficial que un mod necesita y que su publicación no trae.</summary>
/// <param name="Relative">Destino, relativo a la carpeta del ejecutable.</param>
/// <param name="What">Nombre legible («XeFG de Intel para XeMFG XMX»).</param>
/// <param name="Vendor">Fabricante («Intel», «NVIDIA»).</param>
/// <param name="Why">Para qué la necesita el mod.</param>
public sealed record ModDependency(string Relative, string What, string Vendor, string Why)
{
    /// <summary>Versión mínima del archivo (FileVersion). Null = cualquiera.</summary>
    public string? MinVersion { get; init; }
    /// <summary>Firmante Authenticode exigido («Intel Corporation»). Null = sin comprobación de firma.</summary>
    public string? Signer { get; init; }
    /// <summary>Receta oficial con la que CENTRAL la descarga (GitHub del fabricante). Null = el fabricante no la publica suelta.</summary>
    public ComponentRecipe? Official { get; init; }
    /// <summary>Etiqueta fija de la publicación oficial (null = la que diga la receta).</summary>
    public string? OfficialTag { get; init; }
    /// <summary>Buscar una copia firmada en los demás juegos del PC (archivos que solo se distribuyen con juegos).</summary>
    public bool SearchGames { get; init; }
    /// <summary>Página donde el usuario puede conseguirla a mano.</summary>
    public string? Link { get; init; }
    /// <summary>Solo aviso: el mod se instala igual y la falta no lo marca como incompleto.</summary>
    public bool WarnOnly { get; init; }
    /// <summary>Otras rutas (relativas al ejecutable) donde una copia válida también sirve al mod.</summary>
    public string[] AlsoAccept { get; init; } = [];
    /// <summary>Versión a partir de la cual ya no vale (excluida). Null = sin techo.</summary>
    public string? BelowVersion { get; init; }
    /// <summary>Buscar primero en los juegos del PC (una copia mejor que la oficial) y solo después el origen oficial.</summary>
    public bool PreferGames { get; init; }
    /// <summary>Rutas del propio juego (relativas al ejecutable) de donde copiarla si valen: sin descarga.</summary>
    public string[] CopyFrom { get; init; } = [];
}

/// <summary>Estado de un archivo necesario en la carpeta del juego.</summary>
public sealed record RequirementState(string Relative, string What, bool Ok, bool Official, string Message, ModDependency? Dependency = null);

public static class ModRequirements
{
    /// <summary>Comprueba un archivo oficial en la carpeta del juego: presencia, versión y firma.</summary>
    public static RequirementState Evaluate(ModDependency dep, string exeDir)
    {
        foreach (var alternative in dep.AlsoAccept)
        {
            var other = Path.Combine(exeDir, alternative);
            if (File.Exists(other) && Problem(dep, other) is null)
                return new(alternative, dep.What, true, true, $"{Path.GetFileName(alternative)} {VersionUtil.Display(ComponentUpdateService.VersionOf(other))}", dep);
        }
        var path = Path.Combine(exeDir, dep.Relative);
        if (!File.Exists(path))
            return new(dep.Relative, dep.What, false, true, $"Falta {Path.GetFileName(dep.Relative)} ({dep.What}).", dep);
        var problem = Problem(dep, path);
        return problem is null
            ? new(dep.Relative, dep.What, true, true, $"{Path.GetFileName(dep.Relative)} {VersionUtil.Display(ComponentUpdateService.VersionOf(path))}", dep)
            : new(dep.Relative, dep.What, false, true, problem, dep);
    }

    /// <summary>Por qué un archivo concreto no vale para esta dependencia (null = vale).</summary>
    public static string? Problem(ModDependency dep, string path)
    {
        try
        {
            if (dep.MinVersion is { } min)
            {
                var version = ComponentUpdateService.VersionOf(path);
                if (VersionUtil.Compare(version, min) < 0)
                    return $"{Path.GetFileName(path)} es la {VersionUtil.Display(version)}; hace falta la {min} o superior ({dep.What}).";
            }
            if (dep.BelowVersion is { } below)
            {
                var version = ComponentUpdateService.VersionOf(path);
                if (VersionUtil.Compare(version, below) >= 0)
                    return $"{Path.GetFileName(path)} es la {VersionUtil.Display(version)}; hace falta una anterior a la {below} ({dep.What}).";
            }
            if (dep.Signer is { } signer && (ComponentClassifier.SignerOf(path) is not { } s || !s.Contains(signer, StringComparison.OrdinalIgnoreCase)))
                return $"{Path.GetFileName(path)} no lleva la firma de {dep.Vendor}: no se da por buena.";
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        { return $"No se pudo leer {Path.GetFileName(path)}: {error.Message}"; }
    }

    /// <summary>Todo lo que el mod necesita en la carpeta del juego (sus archivos y los oficiales) y si está.
    /// Solo tiene sentido cuando el mod ya está instalado (Marker presente).</summary>
    public static RequirementState[] Check(InstallableMod mod, string exeDir)
    {
        var list = new List<RequirementState>();
        foreach (var required in mod.Required)
        {
            var found = Resolve(required, exeDir);
            var name = Path.GetFileName(required);
            if (found is null) { list.Add(new(required, name, false, false, $"Falta {name} del propio mod: reinstálalo.")); continue; }
            if (mod.BuiltMinSize > 0 && required.Equals(mod.Marker, StringComparison.OrdinalIgnoreCase) && SafeLength(found) < mod.BuiltMinSize)
                list.Add(new(required, name, false, false, $"{name} está sin preparar (le falta lo que construye el setup de su autor): reinstálalo desde ILION SETUP."));
            else list.Add(new(required, name, true, false, name));
        }
        foreach (var dep in mod.Dependencies) list.Add(Evaluate(dep, exeDir));
        return [.. list];
    }

    /// <summary>¿Falta algo que impide que el mod funcione? (los avisos no cuentan).</summary>
    public static RequirementState[] Missing(InstallableMod mod, string exeDir) =>
        [.. Check(mod, exeDir).Where(r => !r.Ok && r.Dependency is not { WarnOnly: true })];

    /// <summary>Busca una copia válida (versión y firma) del archivo en otras carpetas del PC: la de versión más alta.</summary>
    public static string? FindLocalCopy(ModDependency dep, IEnumerable<string> roots, string? exclude = null, CancellationToken token = default)
    {
        var name = Path.GetFileName(dep.Relative);
        string? best = null; string? bestVersion = null;
        var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 6, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Offline };
        foreach (var root in roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(root, name, options).ToArray(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { continue; }
            foreach (var file in files)
            {
                if (exclude is not null && Path.GetFullPath(file).Equals(Path.GetFullPath(exclude), StringComparison.OrdinalIgnoreCase)) continue;
                if (Problem(dep, file) is not null) continue;
                var version = ComponentUpdateService.VersionOf(file);
                if (best is null || VersionUtil.Compare(version, bestVersion) > 0) { best = file; bestVersion = version; }
            }
        }
        return best;
    }

    /// <summary>Ruta real de un archivo de la receta ({tag} y {any} = cualquier carpeta de versión).</summary>
    public static string? Resolve(string relative, string exeDir)
    {
        var pattern = relative.Replace("{tag}", "{any}");
        var parts = pattern.Split("{any}");
        if (parts.Length == 1) return File.Exists(Path.Combine(exeDir, pattern)) ? Path.Combine(exeDir, pattern) : null;
        var parent = Path.GetDirectoryName(Path.Combine(exeDir, parts[0] + "x"))!;
        var leaf = Path.GetFileName(parts[0] + "x")[..^1];
        if (!Directory.Exists(parent)) return null;
        return Directory.EnumerateDirectories(parent, leaf + "*").Select(d => d + parts[1]).FirstOrDefault(File.Exists);
    }

    private static long SafeLength(string path) { try { return new FileInfo(path).Length; } catch (IOException) { return 0; } }
}

/// <summary>Plan de los archivos oficiales de una instalación: qué se coloca, de dónde, y qué queda pendiente.</summary>
public sealed record DependencyPlan(ModPlanItem[] Items, string[] Sources, RequirementState[] Pending);

public static class DependencyResolver
{
    /// <summary>Para cada dependencia que falte (o no valga) en la carpeta del juego: origen oficial verificado, copia firmada
    /// en otro juego del PC o, si no hay ninguno, queda pendiente con el aviso. No escribe nada.</summary>
    public static async Task<DependencyPlan> PlanAsync(InstallableMod mod, string exeDir, ComponentUpdateService? updates,
        IEnumerable<string> gameRoots, CancellationToken token)
    {
        var items = new List<ModPlanItem>(); var sources = new List<string>(); var pending = new List<RequirementState>();
        foreach (var dep in mod.Dependencies)
        {
            var state = ModRequirements.Evaluate(dep, exeDir);
            if (state.Ok) continue;
            var target = Path.Combine(exeDir, dep.Relative);
            async Task<bool> FromGames()
            {
                var copy = await Task.Run(() => ModRequirements.FindLocalCopy(dep, gameRoots, target, token), token);
                if (copy is null) return false;
                items.Add(new(dep.Relative, copy));
                sources.Add($"{Path.GetFileName(dep.Relative)} {VersionUtil.Display(ComponentUpdateService.VersionOf(copy))} · copia firmada por {dep.Vendor} encontrada en {copy}");
                return true;
            }
            if (dep.SearchGames && dep.PreferGames && await FromGames()) continue;
            // Una copia válida en el propio juego (p. ej. la de OptiScaler): sin descarga
            var local = dep.CopyFrom.Select(r => Path.Combine(exeDir, r)).FirstOrDefault(p => File.Exists(p) && ModRequirements.Problem(dep, p) is null);
            if (local is not null)
            {
                items.Add(new(dep.Relative, local));
                sources.Add($"{Path.GetFileName(dep.Relative)} {VersionUtil.Display(ComponentUpdateService.VersionOf(local))} · copia firmada por {dep.Vendor} de {Path.GetRelativePath(exeDir, local)} del propio juego");
                continue;
            }
            if (dep.Official is { } recipe && updates is not null)
            {
                try
                {
                    var candidate = await updates.ResolveAsync(recipe, dep.OfficialTag, token);
                    var verified = await updates.FetchAsync(candidate, token);
                    if (ModRequirements.Problem(dep, verified.LocalPath) is { } why) throw new InvalidDataException(why);
                    items.Add(new(dep.Relative, verified.LocalPath));
                    sources.Add($"{Path.GetFileName(dep.Relative)} {VersionUtil.Display(verified.FileVersion)} · github.com/{recipe.Repository} {candidate.Tag} (firma {dep.Vendor} comprobada)");
                    continue;
                }
                catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or InvalidOperationException or TaskCanceledException)
                {
                    if (error is TaskCanceledException && token.IsCancellationRequested) throw;
                    state = state with { Message = state.Message + $" No se pudo descargar de {recipe.Repository}: {error.Message}" };
                }
            }
            if (dep.SearchGames && !dep.PreferGames && await FromGames()) continue;
            pending.Add(state);
        }
        return new([.. items], [.. sources], [.. pending]);
    }

    /// <summary>Texto para el usuario de lo que queda pendiente: qué falta y qué hacer.</summary>
    public static string Advice(RequirementState state) => state.Dependency is not { } dep ? state.Message
        : $"{state.Message} {dep.Why}" + (dep.Link is null ? "" : $" Dónde: {dep.Link}");
}

/// <summary>Mods cuyo autor exige construir parte del archivo en el PC del usuario (HelixSR: la red de NVIDIA y su Frame Generation).
/// CENTRAL ejecuta el propio setup del autor SOLO en su paso de compilación (sin tocar juegos), con las mismas herramientas fijadas por
/// el autor (Python embebido de python.org, numpy de PyPI y DXC de Microsoft, comprobadas por SHA-256) y deja el resultado en la caché.</summary>
public sealed record AuthorBuild(string OutFolder, bool NoFg, bool NeedsFg);

public static class AuthorBuilder
{
    private static readonly System.Text.RegularExpressions.Regex Pin = new(
        @"(?<name>python|numpy|dxc)\s*=\s*@\{\s*url\s*=\s*'(?<url>https://[^']+)'\s*sha\s*=\s*'(?<sha>[0-9a-fA-F]{64})'", System.Text.RegularExpressions.RegexOptions.Singleline);
    private static readonly string[] AllowedHosts = ["www.python.org", "files.pythonhosted.org", "github.com"];

    /// <summary>Herramientas fijadas por el autor en su helixsr-setup.ps1 (url + SHA-256).</summary>
    public static Dictionary<string, (Uri Url, string Sha)> Pins(string setupScript)
    {
        var text = File.ReadAllText(setupScript);
        var pins = Pin.Matches(text).ToDictionary(m => m.Groups["name"].Value, m => (Url: new Uri(m.Groups["url"].Value), Sha: m.Groups["sha"].Value.ToLowerInvariant()));
        foreach (var name in new[] { "python", "numpy", "dxc" })
            if (!pins.TryGetValue(name, out var pin)) throw new InvalidDataException($"El setup del autor no fija {name}: no se compila.");
            else if (!AllowedHosts.Contains(pin.Url.Host, StringComparer.OrdinalIgnoreCase) || pin.Url.Scheme != "https") throw new InvalidDataException($"{name}: origen no permitido ({pin.Url.Host}).");
        return pins;
    }

    /// <summary>Compila en «folder» (copia del paquete en la caché). Devuelve el registro de la compilación.</summary>
    public static async Task<string> BuildAsync(HttpClient http, string folder, AuthorBuild build, IProgress<string>? progress, CancellationToken token)
    {
        var script = Path.Combine(folder, "helixsr-setup.ps1");
        var setup = Path.Combine(folder, "setup", "helixsr_setup.py");
        if (!File.Exists(script) || !File.Exists(setup)) throw new InvalidDataException("El paquete no trae el setup del autor.");
        var pins = Pins(script);
        var tools = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HelixSR"); // la misma carpeta que usa el setup del autor
        Directory.CreateDirectory(tools);
        var python = Path.Combine(tools, "python", "python.exe");
        if (!Directory.Exists(Path.Combine(tools, "python", "numpy")) || !File.Exists(python))
        {
            progress?.Report("Descargando Python embebido (python.org) y numpy (PyPI)…");
            await FetchAndExpand(http, pins["python"], Path.Combine(tools, "python"), token);
            await FetchAndExpand(http, pins["numpy"], Path.Combine(tools, "python"), token);
        }
        var dxc = Path.Combine(tools, "dxc", "dxc.exe");
        if (!File.Exists(dxc))
        {
            progress?.Report("Descargando el compilador de shaders de Microsoft (DXC)…");
            var unpack = Path.Combine(tools, "dxc-unpack");
            await FetchAndExpand(http, pins["dxc"], unpack, token);
            Directory.CreateDirectory(Path.Combine(tools, "dxc"));
            foreach (var f in Directory.EnumerateFiles(Path.Combine(unpack, "bin", "x64"))) File.Copy(f, Path.Combine(tools, "dxc", Path.GetFileName(f)), true);
            Directory.Delete(unpack, true);
        }
        var output = Path.Combine(folder, build.OutFolder);
        var log = new System.Text.StringBuilder();
        if (await Status(python, setup, output, log) is var (net, fg) && net && (fg || !build.NeedsFg)) return log.ToString();
        progress?.Report("Construyendo HelixSR en tu PC con la DLL oficial de NVIDIA (varios minutos)…");
        var args = new List<string> { setup, output, "--dxc", dxc, "--yes" };
        if (build.NoFg) args.Add("--no-fg");
        var code = await Run(python, args, log, progress, token);
        if (code is not (0 or 3)) throw new InvalidOperationException($"El setup de HelixSR terminó con error ({code}). Últimas líneas:\n" + Tail(log));
        var after = await Status(python, setup, output, log);
        if (!after.Network) throw new InvalidOperationException("El setup de HelixSR no construyó la red.\n" + Tail(log));
        if (build.NeedsFg && !after.Fg) throw new InvalidOperationException("El setup de HelixSR no construyó el Frame Generation.\n" + Tail(log));
        return log.ToString();
    }

    private static async Task<(bool Network, bool Fg)> Status(string python, string setup, string output, System.Text.StringBuilder log)
    {
        var text = new System.Text.StringBuilder();
        await Run(python, [setup, output, "--status"], text, null, CancellationToken.None);
        log.Append(text);
        var s = text.ToString();
        return (s.Contains("network=1"), s.Contains("fg=1"));
    }

    private static async Task<int> Run(string exe, IEnumerable<string> args, System.Text.StringBuilder log, IProgress<string>? progress, CancellationToken token)
    {
        var info = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        foreach (var a in args) info.ArgumentList.Add(a);
        info.Environment["PYTHONIOENCODING"] = "utf-8";
        using var process = Process.Start(info) ?? throw new InvalidOperationException("No se pudo iniciar Python.");
        process.StandardInput.Close(); // nunca se queda esperando una respuesta
        void Line(string? l) { if (l is null) return; lock (log) log.AppendLine(l); if (l.Trim().Length > 0) progress?.Report("HelixSR: " + l.Trim()); }
        process.OutputDataReceived += (_, e) => Line(e.Data);
        process.ErrorDataReceived += (_, e) => Line(e.Data);
        process.BeginOutputReadLine(); process.BeginErrorReadLine();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(40));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { try { process.Kill(true); } catch (InvalidOperationException) { } throw; }
        return process.ExitCode;
    }

    private static async Task FetchAndExpand(HttpClient http, (Uri Url, string Sha) pin, string destination, CancellationToken token)
    {
        var file = Path.Combine(Path.GetTempPath(), "ilion-central-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            using (var net = await http.GetStreamAsync(pin.Url, token))
            using (var output = File.Create(file)) await net.CopyToAsync(output, token);
            if (!ComponentUpdateService.Sha256(file).Equals(pin.Sha, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"{Path.GetFileName(pin.Url.AbsolutePath)}: la huella SHA-256 no coincide con la que fija el autor.");
            Directory.CreateDirectory(destination);
            System.IO.Compression.ZipFile.ExtractToDirectory(file, destination, overwriteFiles: true);
        }
        finally { try { File.Delete(file); } catch (IOException) { } }
    }

    private static string Tail(System.Text.StringBuilder log)
    {
        var lines = log.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return string.Join("\n", lines.TakeLast(8));
    }
}
