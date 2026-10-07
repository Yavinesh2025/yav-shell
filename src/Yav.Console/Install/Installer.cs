using System.Security;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Yav.Platform.Install;

namespace Yav.Console.Install;

/// <summary>A file that is installed next to yav.exe, named relative to the installation directory with forward slashes.</summary>
public sealed record PackageFile(string Path, Func<Stream> Open);

/// <summary>A file an installation put there: removing YAV Shell removes these and nothing else.</summary>
public sealed record InstalledFile(string Path, long Bytes, string Sha256);

/// <summary>Written into the installation directory, so that a later installation or a removal knows what is its own.</summary>
public sealed record InstallManifest(string Product, string Version, DateTimeOffset InstalledAt, IReadOnlyList<InstalledFile> Files)
{
    /// <summary>
    /// Set by a removal that left the program that runs behind: the deletion after the end of the process deletes
    /// the program and this manifest only while the manifest still holds this text. An installation into the
    /// directory in the meantime writes a manifest without it, so that its files stay.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Removal { get; init; }
}

public sealed record InstallOptions
{
    /// <summary>Where to install. Null: where YAV Shell is installed already, otherwise <see cref="IInstallSystem.DefaultDirectory"/>.</summary>
    public string? Directory { get; init; }

    /// <summary>Adds the directory to the PATH of the user account, so that 'yav' is found in every console opened from now on.</summary>
    public bool AddToPath { get; init; } = true;

    /// <summary>Adds YAV Shell to "Installed apps", where it can be removed.</summary>
    public bool Register { get; init; } = true;
}

public enum PathChange
{
    Added,
    WasThere,
    NotAsked,
    Removed,
    WasNotThere,
}

public sealed record InstallOutcome(
    string Directory,
    string Executable,
    string? EarlierVersion,
    bool ProgramCopied,
    PathChange Path,
    bool Registered);

/// <param name="DirectoryRemoved">False when the directory still holds something: the program that runs, files the installation did not put there, or nothing but in use by another program.</param>
/// <param name="ProgramLeftAt">
/// The program that runs; it stays until the process has ended and is then deleted by the caller, together with the
/// manifest beside it, which names only the program by then. The directory is kept while it holds them; the caller
/// removes it afterwards when nothing else is left in it.
/// </param>
public sealed record UninstallOutcome(
    string Directory,
    bool DirectoryRemoved,
    PathChange Path,
    bool RegistrationRemoved,
    string? ProgramLeftAt)
{
    /// <summary>True when the directory holds files the installation did not put there, which stay. The program that runs and its manifest do not count.</summary>
    public bool OtherFilesKept { get; init; }

    /// <summary>The text the manifest beside <see cref="ProgramLeftAt"/> holds (<see cref="InstallManifest.Removal"/>). Null when no program was left.</summary>
    public string? Removal { get; init; }
}

/// <summary>What an installed yav.exe answered when it was started with --version.</summary>
/// <param name="Output">What it wrote. Null when it wrote nothing that counts.</param>
/// <param name="Failure">Why it gave no answer: "could not be started: ...", "gave no answer within 60 seconds", "ended with exit code 3: ...". Null when it answered.</param>
public sealed record VersionAnswer(string? Output, string? Failure = null);

/// <summary>An installation or removal that was refused or failed. Its message says why and what was left as it was.</summary>
public sealed class InstallException : Exception
{
    public InstallException(string message)
        : base(message)
    {
    }

    public InstallException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Installs YAV Shell for the current user from the program that runs, and removes it again. No administrator
/// rights are needed. The data directory of the user (settings, history, workspaces) is never touched here.
/// </summary>
public sealed class Installer
{
    public const string ExecutableName = "yav.exe";
    public const string ManifestName = "yav-install.json";
    public const string Product = "YAV Shell";

    /// <summary>What YAV Shell 0.1.1, installed by its PowerShell script, wrote in place of <see cref="ManifestName"/>.</summary>
    internal const string EarlierManifestName = "package-manifest.json";

    /// <summary>Windows starts no program whose path has this many characters or more, whether long paths are turned on or not.</summary>
    internal const int LongestProgramPath = 259;

    private readonly IInstallSystem _system;
    private readonly string _version;
    private readonly string _dataDirectory;
    private readonly Func<string, CancellationToken, Task<VersionAnswer>> _reportedVersion;

    /// <param name="reportedVersion">Starts an installed yav.exe with --version and returns what it answered, or why it did not.</param>
    public Installer(IInstallSystem system, string version, string dataDirectory, Func<string, CancellationToken, Task<VersionAnswer>> reportedVersion)
    {
        _system = system;
        _version = version;
        _dataDirectory = LongPath.Of(dataDirectory);
        _reportedVersion = reportedVersion;
    }

    public IInstallSystem System => _system;

    /// <summary>
    /// The directory an installation uses: the one named, otherwise the one "Installed apps" names, otherwise the
    /// default directory of the system. The start-up offer shows it and installs exactly there.
    /// </summary>
    public string DirectoryFor(string? named) =>
        LongPath.Of(named ?? _system.ReadRegistration()?.InstallLocation ?? _system.DefaultDirectory);

    /// <summary>The installation "Installed apps" knows of, when its program is still there. Null when YAV Shell is not installed.</summary>
    public InstallRegistration? Current()
    {
        var registration = _system.ReadRegistration();
        return registration is not null && File.Exists(Path.Combine(registration.InstallLocation, ExecutableName)) ? registration : null;
    }

    /// <summary>True when <paramref name="program"/> is the yav.exe of the installation "Installed apps" knows of.</summary>
    public bool IsInstalledProgram(string program) =>
        Current() is { } current && LongPath.Same(program, Path.Combine(current.InstallLocation, ExecutableName));

    /// <summary>
    /// True when the folder of <paramref name="program"/> holds a readable yav-install.json of YAV Shell: the program is
    /// an installed copy, whether "Installed apps" lists it or not (an installation made with --no-register).
    /// </summary>
    public static bool RunsFromInstallation(string program) =>
        Path.GetDirectoryName(Path.GetFullPath(program)) is { } folder && ReadManifest(Path.Combine(folder, ManifestName)).Manifest is not null;

    /// <summary>
    /// The directory of the installation a removal takes, found as <see cref="Uninstall"/> finds it, and nothing is
    /// changed: the one named, otherwise the one "Installed apps" names, otherwise the folder of <paramref name="program"/>
    /// when it holds yav-install.json. Null when none is named or known: YAV Shell is not installed for this user account.
    /// </summary>
    /// <exception cref="InstallException">That directory holds no installation of YAV Shell that can be read.</exception>
    public string? InstallationToRemove(string? directory, string program) =>
        Installation(directory, _system.ReadRegistration(), program)?.Root;

    public async Task<InstallOutcome> InstallAsync(string program, IReadOnlyList<PackageFile> files, InstallOptions options, CancellationToken cancellationToken)
    {
        // Everything is checked before the first change: a refusal leaves the machine as it was.
        var directory = DirectoryFor(options.Directory);
        if (IsVolumeRoot(directory))
        {
            throw new InstallException($"{directory} is the root of a drive. Nothing was changed; YAV Shell is installed into a folder of its own: name one with --dir.");
        }

        if (LongPath.IsSameOrInside(directory, _dataDirectory) || LongPath.IsSameOrInside(_dataDirectory, directory))
        {
            throw new InstallException($"{directory} is where YAV keeps your data, or holds it. Nothing was changed; the program is installed somewhere else.");
        }

        var target = Path.Combine(directory, ExecutableName);
        if (target.Length > LongestProgramPath)
        {
            throw new InstallException(
                $"{target} would be {target.Length} characters long, and Windows starts no program from a path of 260 characters or more. "
                + "Nothing was changed; name a shorter directory with --dir.");
        }

        if (options.AddToPath && directory.Contains(';', StringComparison.Ordinal))
        {
            // In the PATH, a ';' separates two folders: the directory would become two entries, neither of them it.
            throw new InstallException($"{directory} holds a ';', which separates the folders of the PATH. Nothing was changed; name another directory with --dir, or add --no-path.");
        }

        if (options.Register && Current() is { } other && !LongPath.Same(other.InstallLocation, directory))
        {
            // A second installation would hide behind the first one in the PATH, and "Installed apps" would lose the first.
            throw new InstallException(
                $"YAV Shell {other.DisplayVersion} is installed in {other.InstallLocation}. Nothing was changed: install it there "
                + "('yav install' without --dir), or remove that installation first with 'yav uninstall'.");
        }

        var manifestPath = Path.Combine(directory, ManifestName);
        var (manifestExists, earlier) = ReadInstallation(directory);
        if (manifestExists && earlier is null)
        {
            throw new InstallException(
                $"{manifestPath} cannot be read, so it is not known which files in {directory} belong to YAV Shell. "
                + "Nothing was changed; remove the directory, or name another one with --dir.");
        }

        if (earlier is null && Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any(entry => !IsLeftover(entry)))
        {
            throw new InstallException($"{directory} exists and was not created by the installation of YAV Shell. Nothing was changed; name another directory with --dir.");
        }

        RefuseWhileRunning(target, "installed");
        if (options.AddToPath)
        {
            // A PATH that is not text is refused now, before anything is written; it is read again before it is written.
            _ = ReadPath("Nothing was changed.");
        }

        // Started from the installed program itself, the installation is repaired: its other files, the PATH and
        // the entry under "Installed apps" are put right. A program cannot be copied over itself.
        var copyProgram = !LongPath.Same(program, target);
        var previous = earlier?.Files ?? [];
        var installed = new List<InstalledFile>();
        var again = $"'yav install --dir \"{directory}\"' again completes the installation, 'yav uninstall --dir \"{directory}\"' removes it.";
        var begun = false;
        try
        {
            Directory.CreateDirectory(directory);

            // Before the first file is written, the manifest names every file that is about to be there, so that an
            // installation that is interrupted can still be completed or removed: without it, the directory would be
            // one that holds files of nobody's, which no installation may touch.
            var names = new[] { ExecutableName }.Concat(files.Select(f => f.Path));
            var planned = previous.Concat(names
                .Where(name => !previous.Any(e => SameName(e.Path, name)))
                .Select(name => new InstalledFile(name, 0, string.Empty))).ToList();
            WriteManifest(manifestPath, new InstallManifest(Product, _version, DateTimeOffset.UtcNow, planned));
            begun = true;
            RemoveLeftovers(directory, planned.Select(f => f.Path));

            installed.Add(copyProgram
                ? Write(directory, ExecutableName, () => File.OpenRead(program))
                : Describe(directory, ExecutableName));
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                installed.Add(Write(directory, file.Path, file.Open));
            }

            // What an earlier version installed and this one does not have goes; anything else in the directory stays.
            foreach (var old in previous)
            {
                if (!installed.Any(f => SameName(f.Path, old.Path)) && Inside(directory, old.Path) is { } stale && File.Exists(stale))
                {
                    Delete(stale);
                    RemoveEmptyParents(directory, stale);
                }
            }

            WriteManifest(manifestPath, new InstallManifest(Product, _version, DateTimeOffset.UtcNow, installed));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InstallException(
                $"The installation in {directory} could not be completed: {ex.Message} "
                + (begun ? $"{manifestPath} names the files it may have written: {again} " : string.Empty)
                + "The PATH and \"Installed apps\" were not changed.",
                ex);
        }
        catch (OperationCanceledException ex) when (begun)
        {
            throw new InstallException(
                $"The installation in {directory} was stopped before it was complete. {manifestPath} names the files it may have written: {again} "
                + "The PATH and \"Installed apps\" were not changed.",
                ex);
        }

        VersionAnswer answer;
        try
        {
            answer = await _reportedVersion(target, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            throw new InstallException(
                $"The installation was stopped after its files were written to {directory}, while the installed program was started once to check it. {again} "
                + "The PATH and \"Installed apps\" were not changed.",
                ex);
        }

        var reported = answer.Output?.Trim();
        if (!string.Equals(reported, "yav " + _version, StringComparison.Ordinal))
        {
            var said = answer.Failure is { } failure ? $"{failure}." : $"said '{reported}' instead of 'yav {_version}'.";
            throw new InstallException(
                $"The installed program did not start as expected: {target} --version {said} "
                + $"The files stay where they are; 'yav uninstall --dir \"{directory}\"' removes them. The PATH and \"Installed apps\" were not changed.");
        }

        var pathChange = PathChange.NotAsked;
        try
        {
            if (options.AddToPath)
            {
                // Read again: the copy and the check took a while, and another program may have changed the PATH since.
                var path = _system.ReadUserPath();
                var changed = PathList.WithEntry(path.Value, directory);
                pathChange = changed == path.Value ? PathChange.WasThere : PathChange.Added;
                if (pathChange == PathChange.Added)
                {
                    // Only the text changes: the kind of the value, and with it whether Windows expands it, stays.
                    _system.WriteUserPath(path with { Value = changed });
                    _system.AnnounceEnvironmentChange();
                }
            }

            if (options.Register)
            {
                _system.WriteRegistration(new InstallRegistration(directory, _version), target, installed.Sum(f => f.Bytes));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or InvalidDataException)
        {
            throw new InstallException($"The files are installed in {directory}, but the registry of your account could not be changed: {ex.Message}", ex);
        }

        return new InstallOutcome(directory, target, earlier?.Version, copyProgram, pathChange, options.Register);
    }

    /// <summary>
    /// Removes the installation in <paramref name="directory"/>, or the one "Installed apps" knows of. Only the
    /// files the installation put there are removed; a directory that holds anything else is kept.
    /// </summary>
    /// <param name="program">The program that runs. When it is the installed one it stays where it is, and the outcome names it, for the caller to delete once the process has ended.</param>
    public UninstallOutcome Uninstall(string? directory, string program)
    {
        var registration = _system.ReadRegistration();
        var (root, manifest) = Installation(directory, registration, program)
            ?? throw new InstallException("YAV Shell is not installed for this user account, so nothing was removed. Name an installation with --dir.");
        var manifestPath = Path.Combine(root, ManifestName);

        RefuseWhileRunning(Path.Combine(root, ExecutableName), "removed");

        string? left = null;
        string? removal = null;
        try
        {
            RemoveLeftovers(root, manifest.Files.Select(f => f.Path));
            foreach (var file in manifest.Files)
            {
                if (Inside(root, file.Path) is not { } path || !File.Exists(path))
                {
                    continue;
                }

                if (LongPath.Same(path, program))
                {
                    // A program that runs cannot be deleted, and it must not be moved either: a program that is one
                    // file goes on reading its parts from where it was started. It stays until the process has ended.
                    left = path;
                    continue;
                }

                Delete(path);
                RemoveEmptyParents(root, path);
            }

            if (left is null)
            {
                Delete(manifestPath);
            }
            else
            {
                // The program that runs stays until the process has ended. A manifest that names only it stays beside it,
                // so that the directory is still known as an installation of YAV Shell - to 'yav install' and to
                // 'yav uninstall --dir' - when the deletion after the end of the process does not happen. The text it
                // holds ties that deletion to this removal.
                removal = Guid.NewGuid().ToString("N");
                WriteManifest(manifestPath, manifest with
                {
                    Files = manifest.Files.Where(file => Inside(root, file.Path) is { } kept && LongPath.Same(kept, left)).ToList(),
                    Removal = removal,
                });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InstallException(
                $"A file in {root} could not be removed: {ex.Message} The files before it are gone; {ManifestName}, the PATH and "
                + "\"Installed apps\" are as they were, so 'yav uninstall' can be run again once the file is free.",
                ex);
        }

        // A directory that still holds the program that runs, or anything of the user, stays. So does an empty one that
        // another program uses, such as a console whose current directory it is: the rest of the removal goes on.
        var removed = !Directory.Exists(root);
        if (!removed && !IsVolumeRoot(root) && !Directory.EnumerateFileSystemEntries(root).Any())
        {
            removed = TryDeleteDirectory(root);
        }

        var others = !removed && Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any(entry =>
            !string.Equals(Path.GetFileName(entry), ManifestName, StringComparison.OrdinalIgnoreCase) && !(left is not null && LongPath.Same(entry, left)));

        var pathChange = PathChange.WasNotThere;
        var unregistered = false;
        try
        {
            if (TryReadPath() is { } path)
            {
                var changed = PathList.WithoutEntry(path.Value, root);
                if (changed != path.Value)
                {
                    _system.WriteUserPath(path with { Value = changed });
                    _system.AnnounceEnvironmentChange();
                    pathChange = PathChange.Removed;
                }
            }

            if (registration is not null && LongPath.Same(registration.InstallLocation, root))
            {
                _system.DeleteRegistration();
                unregistered = true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            throw new InstallException($"The files of YAV Shell were removed, but the registry of your account could not be changed: {ex.Message}", ex);
        }

        return new UninstallOutcome(root, removed, pathChange, unregistered, left) { OtherFilesKept = others, Removal = removal };
    }

    /// <summary>The installation a removal takes, and what its manifest says. Null when none is named or known.</summary>
    /// <exception cref="InstallException">The directory holds no installation of YAV Shell that can be read.</exception>
    private (string Root, InstallManifest Manifest)? Installation(string? directory, InstallRegistration? registration, string program)
    {
        var chosen = directory ?? registration?.InstallLocation;
        if (chosen is null && Path.GetDirectoryName(Path.GetFullPath(program)) is { } beside && File.Exists(Path.Combine(beside, ManifestName)))
        {
            // Installed without an entry under "Installed apps": the program knows where it is itself.
            chosen = beside;
        }

        if (chosen is null)
        {
            return null;
        }

        var root = LongPath.Of(chosen);
        if (ReadInstallation(root).Manifest is not { } manifest)
        {
            throw new InstallException($"{root} does not hold an installation of YAV Shell ({ManifestName} is missing or cannot be read). Nothing was removed.");
        }

        return (root, manifest);
    }

    private void RefuseWhileRunning(string executable, string what)
    {
        var running = _system.ProcessesRunning(executable).Where(id => id != Environment.ProcessId).ToList();
        if (running.Count > 0)
        {
            throw new InstallException(
                $"YAV Shell is running from {Path.GetDirectoryName(executable)} (process {string.Join(", ", running)}), so it cannot be {what} now. "
                + "Nothing was changed. Leave it with /exit and try again.");
        }
    }

    private UserPath ReadPath(string consequence)
    {
        try
        {
            return _system.ReadUserPath();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or SecurityException)
        {
            throw new InstallException($"The PATH of your account could not be read: {ex.Message} {consequence}", ex);
        }
    }

    /// <summary>The PATH, or null when it is not text: then YAV has not added to it, and does not take anything out of it either.</summary>
    private UserPath? TryReadPath()
    {
        try
        {
            return _system.ReadUserPath();
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>
    /// What the directory says about the installation in it: its manifest, or the one YAV Shell 0.1.1 wrote. Exists is
    /// true when a manifest is there; Manifest is null when it cannot be read or is not YAV Shell's.
    /// </summary>
    private static (bool Exists, InstallManifest? Manifest) ReadInstallation(string directory)
    {
        var found = ReadManifest(Path.Combine(directory, ManifestName));
        return found.Exists ? found : ReadEarlierManifest(Path.Combine(directory, EarlierManifestName));
    }

    /// <summary>Writes a file through a temporary one beside it, so that an interrupted installation never leaves half a file under the real name.</summary>
    private static InstalledFile Write(string root, string relative, Func<Stream> open)
    {
        var target = Inside(root, relative) ?? throw new InstallException($"'{relative}' is not a path inside the installation directory.");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temporary = target + ".partial-" + Guid.NewGuid().ToString("N")[..8];
        long bytes;
        byte[] hash;
        try
        {
            using (var source = open())
            using (var destination = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                // Copied as bytes, not as a file: what Windows recorded about where a download came from stays with the download.
                var buffer = new byte[1 << 16];
                int read;
                while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    destination.Write(buffer, 0, read);
                    hasher.AppendData(buffer, 0, read);
                }

                destination.Flush(flushToDisk: true);
                bytes = destination.Length;
                hash = hasher.GetHashAndReset();
            }

            // A file the user made read-only is still the installation's own: it is replaced like any other.
            ClearReadOnly(target);
            File.Move(temporary, target, overwrite: true);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }

        return new InstalledFile(relative, bytes, Convert.ToHexStringLower(hash));
    }

    private static InstalledFile Describe(string root, string relative)
    {
        var path = Inside(root, relative)!;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new InstalledFile(relative, stream.Length, Convert.ToHexStringLower(SHA256.HashData(stream)));
    }

    /// <summary>Whether a manifest is there, and what it says. A manifest that cannot be read, or is not YAV Shell's, says nothing.</summary>
    private static (bool Exists, InstallManifest? Manifest) ReadManifest(string path)
    {
        if (!File.Exists(path))
        {
            return (false, null);
        }

        try
        {
            var manifest = JsonSerializer.Deserialize(File.ReadAllText(path), InstallJson.Default.InstallManifest);
            if (manifest is not { Files: not null } || !string.Equals(manifest.Product, Product, StringComparison.Ordinal))
            {
                return (true, null);
            }

            // An entry without a name names nothing that could be removed.
            return (true, manifest with { Files = manifest.Files.Where(f => f is { Path.Length: > 0 }).ToList() });
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _ = ex;
            return (true, null);
        }
    }

    /// <summary>
    /// The manifest of YAV Shell 0.1.1, which its PowerShell script installed into the same directory and registered under
    /// the same entry of "Installed apps": {"product": "YAV Shell", "version": "0.1.1", "files": [{"path": "yav.exe"}, ...]}.
    /// It does not name itself; it is one of the files of that installation all the same.
    /// </summary>
    private static (bool Exists, InstallManifest? Manifest) ReadEarlierManifest(string path)
    {
        if (!File.Exists(path))
        {
            return (false, null);
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("product", out var product) || product.ValueKind != JsonValueKind.String || product.GetString() != Product
                || !root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("files", out var listed) || listed.ValueKind != JsonValueKind.Array)
            {
                return (true, null);
            }

            var files = listed.EnumerateArray()
                .Where(entry => entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("path", out var name) && name.ValueKind == JsonValueKind.String)
                .Select(entry => entry.GetProperty("path").GetString()!)
                .Where(name => name.Length > 0)
                .Append(EarlierManifestName)
                .Select(name => new InstalledFile(name, 0, string.Empty))
                .ToList();
            return (true, new InstallManifest(Product, version.GetString()!, default, files));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _ = ex;
            return (true, null);
        }
    }

    /// <summary>Writes the manifest through a temporary file, flushed to the disk before it takes the real name: it decides what a removal may remove.</summary>
    private static void WriteManifest(string path, InstallManifest manifest)
    {
        var temporary = path + ".partial";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, manifest, InstallJson.Default.InstallManifest);
            stream.Flush(flushToDisk: true);
        }

        ClearReadOnly(path);
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>
    /// Deletes what an interrupted write left beside the files of the installation: '&lt;file&gt;.partial-&lt;8 hex digits&gt;'
    /// and the manifest's '.partial'. They are the installation's own, and would otherwise keep a directory from being
    /// removed. Best effort: one that cannot be deleted stays.
    /// </summary>
    private static void RemoveLeftovers(string root, IEnumerable<string> names)
    {
        foreach (var name in names.Append(ManifestName))
        {
            if (Inside(root, name) is not { } file || Path.GetDirectoryName(file) is not { } folder || !Directory.Exists(folder))
            {
                continue;
            }

            foreach (var leftover in Directory.EnumerateFiles(folder, Path.GetFileName(file) + ".partial*"))
            {
                if (IsLeftoverOf(Path.GetFileName(leftover), Path.GetFileName(file)))
                {
                    try
                    {
                        Delete(leftover);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // It stays; it is named nowhere and changes nothing.
                        _ = ex;
                    }
                }
            }
        }
    }

    /// <summary>True for the manifest's temporary file, the one thing an installation interrupted before its first manifest leaves behind.</summary>
    private static bool IsLeftover(string entry) =>
        string.Equals(Path.GetFileName(entry), ManifestName + ".partial", StringComparison.OrdinalIgnoreCase) && File.Exists(entry);

    private static bool IsLeftoverOf(string name, string file)
    {
        if (string.Equals(file, ManifestName, StringComparison.OrdinalIgnoreCase) && string.Equals(name, file + ".partial", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var prefix = file + ".partial-";
        return name.Length == prefix.Length + 8
            && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && name[prefix.Length..].All(char.IsAsciiHexDigitLower);
    }

    /// <summary>
    /// Removes the directories a removed file was in, from the inside out, as long as they hold nothing. The root
    /// stays: it is the caller's to remove. An empty directory of the user elsewhere in the root is left alone, and so
    /// is one that cannot be removed now because another program uses it.
    /// </summary>
    private static void RemoveEmptyParents(string root, string file)
    {
        var top = WithSeparator(root);
        var directory = Path.GetDirectoryName(file);
        while (directory is not null
            && directory.StartsWith(top, StringComparison.OrdinalIgnoreCase)
            && Directory.Exists(directory)
            && !Directory.EnumerateFileSystemEntries(directory).Any()
            && TryDeleteDirectory(directory))
        {
            directory = Path.GetDirectoryName(directory);
        }
    }

    /// <summary>
    /// The full path of a file named relative to the root. Null when the name leads outside of it, is rooted, or names
    /// a stream of a file rather than a file: a manifest that was changed by hand cannot point anywhere else.
    /// </summary>
    private static string? Inside(string root, string relative)
    {
        if (relative.Length == 0 || relative.Contains(':', StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            return null;
        }

        var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        return full.StartsWith(WithSeparator(root), StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    /// <summary>The full path of a directory with one separator at its end: 'C:\YAV\' for 'C:\YAV', and 'E:\' for the root of a drive.</summary>
    private static string WithSeparator(string directory)
    {
        var full = Path.GetFullPath(directory);
        return Path.EndsInDirectorySeparator(full) ? full : full + Path.DirectorySeparatorChar;
    }

    private static bool SameName(string a, string b) =>
        string.Equals(a.Replace('\\', '/'), b.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);

    private static bool IsVolumeRoot(string path) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetPathRoot(path) ?? string.Empty), Path.TrimEndingDirectorySeparator(path), StringComparison.OrdinalIgnoreCase);

    private static void Delete(string path)
    {
        ClearReadOnly(path);
        File.Delete(path);
    }

    /// <summary>Removes an empty directory. False when it stays, because another program uses it or it may not be removed.</summary>
    private static bool TryDeleteDirectory(string directory)
    {
        try
        {
            Directory.Delete(directory);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _ = ex;
            return false;
        }
    }

    private static void ClearReadOnly(string path)
    {
        if (File.Exists(path) && File.GetAttributes(path) is var attributes && attributes.HasFlag(FileAttributes.ReadOnly))
        {
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // What could not be deleted stays; it is named nowhere and changes nothing.
            _ = ex;
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(InstallManifest))]
internal sealed partial class InstallJson : JsonSerializerContext;
