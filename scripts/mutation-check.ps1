<#
.SYNOPSIS
    Proves that the tests can fail: breaks one behavior at a time and expects the named tests to go red.
.DESCRIPTION
    Each mutation replaces a fragment of production code with a deliberately wrong one, runs the tests
    that are supposed to protect that behavior, and restores the file. A mutation that the tests do not
    notice is reported as SURVIVED, which means the behavior is not really protected.
    A mutation that makes a test hang is ended by a time limit (-HangSeconds) and reported as KILLED by it:
    the tests did not pass. The working tree is always restored, also when a run is interrupted.
#>
[CmdletBinding()]
param(
    # Only the mutations whose name contains this text.
    [string]$Only,
    # Leaves out that many mutations from the beginning of the list, to go on where a run ended.
    [int]$Skip = 0,
    # Makes no more than that many mutations. 0 makes all that are left.
    [int]$Take = 0,
    # When no test has begun or ended for that many seconds, the test platform ends the tests of the mutation.
    # 0 sets no limit.
    [ValidateRange(0, 86400)]
    [int]$HangSeconds = 600
)

. "$PSScriptRoot\env.ps1"

$mutations = @(
    @{
        Name    = 'child is not placed in the job object'
        File    = 'src\Yav.Platform\Processes\NativeProcess.cs'
        Find    = 'if (!NativeMethods.UpdateProcThreadAttribute(attributeList, 0, NativeMethods.PROC_THREAD_ATTRIBUTE_JOB_LIST, (nint)(&jobHandle), nint.Size, 0, 0))'
        Replace = 'if (jobHandle == 0 && nint.Size < 0)'
        Filter  = 'FullyQualifiedName~ProcessRunnerTests'
    },
    @{
        Name    = 'the program of an installation made with no-register offers to install itself'
        File    = 'src\Yav.Console\Install\InstallOffer.cs'
        Find    = '        if (situation.FromInstallation)'
        Replace = '        if (situation.FromInstallation && !situation.FromInstallation)'
        Filter  = 'FullyQualifiedName~InstallOfferTests'
    },
    @{
        Name    = 'a manifest beside the program that cannot be read makes it an installed copy'
        File    = 'src\Yav.Console\Install\Installer.cs'
        Find    = 'ReadManifest(Path.Combine(folder, ManifestName)).Manifest is not null;'
        Replace = 'ReadManifest(Path.Combine(folder, ManifestName)).Exists;'
        Filter  = 'FullyQualifiedName~InstallOfferTests'
    },
    @{
        Name    = 'removing the data asks before it looks for an installation'
        File    = 'src\Yav.Console\Install\InstallCommand.cs'
        Find    = 'if (world.Installer.InstallationToRemove(options.InstallDirectory ?? world.InstallDirectory, world.Program) is null)'
        Replace = 'if (world.Installer.InstallationToRemove(options.InstallDirectory ?? world.InstallDirectory, world.Program) is null && options.RemoveData && !options.RemoveData)'
        Filter  = 'FullyQualifiedName~InstallCommandTests'
    },
    @{
        Name    = 'an installation the offer started cannot be stopped'
        File    = 'src\Yav.Console\Install\InstallOffer.cs'
        Find    = '            world, new InstallOptions { Directory = directory ?? world.InstallDirectory }, cancellationToken).ConfigureAwait(false);'
        Replace = '            world, new InstallOptions { Directory = directory ?? world.InstallDirectory }, CancellationToken.None).ConfigureAwait(false);'
        Filter  = 'FullyQualifiedName~InstallOfferTests'
    },
    @{
        Name    = 'a control c while the offer asks is followed by an installation'
        File    = 'src\Yav.Console\Install\InstallOffer.cs'
        Find    = '        cancellationToken.ThrowIfCancellationRequested();'
        Replace = '        _ = cancellationToken.IsCancellationRequested;'
        Filter  = 'FullyQualifiedName~InstallOfferTests'
    },
    @{
        Name    = 'the program of an installation reads the registry to find out that it is installed'
        File    = 'src\Yav.Console\Install\InstallOffer.cs'
        Find    = '        var installed = asks && !fromInstallation ? world.Installer.Current() : null;'
        Replace = '        var installed = asks ? world.Installer.Current() : null;'
        Filter  = 'FullyQualifiedName~InstallOfferTests'
    },
    @{
        Name    = 'a registry that cannot be read before the data is asked about is reported as a defect'
        File    = 'src\Yav.Console\Install\InstallCommand.cs'
        Find    = '            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException)
            {
                await world.Error.WriteLineAsync($"yav: the removal could not be completed: {ex.Message}").ConfigureAwait(false);
                return ExitCodes.Failed;
            }

            var hasData = Directory.Exists(home);'
        Replace = '            var hasData = Directory.Exists(home);'
        Filter  = 'FullyQualifiedName~InstallCommandTests'
    },
    @{
        Name    = 'a build of the source tree removes the installation of the user'
        File    = 'src\Yav.Console\Install\InstallCommand.cs'
        Find    = '        if (!world.IsSingleFile && options.InstallDirectory is null)'
        Replace = '        if (!world.IsSingleFile && options.InstallDirectory is null && options.RemoveData && !options.RemoveData)'
        Filter  = 'FullyQualifiedName~InstallCommandTests.A_build_of_the_source_tree_removes_only'
    },
    @{
        Name    = 'dir takes the next option for its value'
        File    = 'src\Yav.Console\Cli\CommandLine.cs'
        Find    = '                    if (inline is null && index + 1 < arguments.Count && arguments[index + 1].StartsWith(''-''))'
        Replace = '                    if (inline is null && index + 1 < arguments.Count && arguments[index + 1].Length == 0)'
        Filter  = 'FullyQualifiedName~CommandLineTests'
    },
    @{
        Name    = 'a data directory that holds files of someone else is removed'
        File    = 'src\Yav.Console\Install\DataDirectory.cs'
        Find    = '            if (!own)'
        Replace = '            if (!own && own)'
        Filter  = 'FullyQualifiedName~InstallCommandTests'
    },
    @{
        Name    = 'a database another yav has open does not stop the question'
        File    = 'src\Yav.Console\Install\DataDirectory.cs'
        Find    = '        if (InUse(Path.Combine(full, "yav.db")) is { } use)'
        Replace = '        if (InUse(Path.Combine(full, "yav.db")) is { } use && use.Length < 0)'
        Filter  = 'FullyQualifiedName~InstallCommandTests.A_database_another_yav'
    },
    @{
        Name    = 'the data directory is removed without being looked at again'
        File    = 'src\Yav.Console\Install\DataDirectory.cs'
        Find    = '        if (Problem(home) is { } problem)
        {
            throw new InstallException(problem + " Nothing was removed.");
        }'
        Replace = '        if (home.Length < 0)
        {
            throw new InstallException(" Nothing was removed.");
        }'
        Filter  = 'FullyQualifiedName~InstallCommandTests'
    },
    @{
        Name    = 'the database and the settings are not removed last'
        File    = 'src\Yav.Console\Install\DataDirectory.cs'
        Find    = '                if (!string.Equals(entry.FullName, database, StringComparison.OrdinalIgnoreCase) && !string.Equals(entry.FullName, settings, StringComparison.OrdinalIgnoreCase))'
        Replace = '                if (!string.Equals(entry.FullName, database, StringComparison.OrdinalIgnoreCase))'
        Filter  = 'FullyQualifiedName~InstallCommandTests'
    },
    @{
        Name    = 'the deletion after the end deletes the manifest while the program is still there'
        File    = 'src\Yav.Console\Install\InstallSurroundings.cs'
        Find    = '            + $" & (if not exist \"{program}\" (del /f /q \"{manifest}\" 2>nul & rmdir \"{directory}\" 2>nul)))");'
        Replace = '            + $" & del /f /q \"{manifest}\" 2>nul & rmdir \"{directory}\" 2>nul)");'
        Filter  = 'FullyQualifiedName~InstallCommandTests.The_deletion_after_the_end'
    },
    @{
        Name    = 'the deletion after the end deletes a new installation in the same directory'
        File    = 'src\Yav.Console\Install\InstallSurroundings.cs'
        Find    = '            $"\"{ping}\" -n {waitSeconds + 1} 127.0.0.1 >nul & {unchanged} && ("'
        Replace = '            $"\"{ping}\" -n {waitSeconds + 1} 127.0.0.1 >nul & ("'
        Filter  = 'FullyQualifiedName~InstallCommandTests.The_deletion_after_the_end'
    },
    @{
        Name    = 'the deletion after the end gives findstr the path of the manifest'
        File    = 'src\Yav.Console\Install\InstallSurroundings.cs'
        Find    = '        var unchanged = $"\"{findstr}\" /l /c:{removal} <\"{manifest}\" >nul";'
        Replace = '        var unchanged = $"\"{findstr}\" /l /c:{removal} \"{manifest}\" >nul";'
        Filter  = 'FullyQualifiedName~InstallCommandTests.The_deletion_after_the_end'
    },
    @{
        Name    = 'a removal is not tied to the manifest it leaves'
        File    = 'src\Yav.Console\Install\Installer.cs'
        Find    = '                    Removal = removal,'
        Replace = '                    Removal = null,'
        Filter  = 'FullyQualifiedName~InstallerTests|FullyQualifiedName~InstallCommandTests'
    },
    @{
        Name    = 'the report of a self-removal says that other files are kept'
        File    = 'src\Yav.Console\Install\InstallReport.cs'
        Find    = '            : outcome.OtherFilesKept ? $"Removed the files of YAV Shell. {directory} holds other files and was kept."'
        Replace = '            : outcome.OtherFilesKept || left is not null ? $"Removed the files of YAV Shell. {directory} holds other files and was kept."'
        Filter  = 'FullyQualifiedName~InstallCommandTests'
    },
    @{
        Name    = 'the root of a drive is installed into'
        File    = 'src\Yav.Console\Install\Installer.cs'
        Find    = '        if (IsVolumeRoot(directory))'
        Replace = '        if (directory.Length == 0)'
        Filter  = 'FullyQualifiedName~InstallerTests.The_root_of_a_drive'
    },
    @{
        Name    = 'a program that windows cannot start is installed'
        File    = 'src\Yav.Console\Install\Installer.cs'
        Find    = '        if (target.Length > LongestProgramPath)'
        Replace = '        if (target.Length < 0)'
        Filter  = 'FullyQualifiedName~InstallerTests.A_directory_whose_program_could_not_be_started'
    },
    @{
        Name    = 'a directory with a semicolon is added to the path'
        File    = 'src\Yav.Console\Install\Installer.cs'
        Find    = '        if (options.AddToPath && directory.Contains('';'', StringComparison.Ordinal))'
        Replace = '        if (options.AddToPath && directory.Length == 0)'
        Filter  = 'FullyQualifiedName~InstallerTests.A_directory_with_a_semicolon'
    },
    @{
        Name    = 'an empty directory another program uses stops the removal'
        File    = 'src\Yav.Console\Install\Installer.cs'
        Find    = '            Directory.Delete(directory);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)'
        Replace = '            Directory.Delete(directory);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException)'
        Filter  = 'FullyQualifiedName~InstallerTests.An_empty_directory_that_another_program_uses'
    },
    @{
        Name    = 'what an interrupted write left keeps the directory from being removed'
        File    = 'src\Yav.Console\Install\Installer.cs'
        Find    = '            RemoveLeftovers(root, manifest.Files.Select(f => f.Path));'
        Replace = '            _ = manifest.Files.Count;'
        Filter  = 'FullyQualifiedName~InstallerTests.What_an_interrupted_write'
    },
    @{
        Name    = 'an installation of yav shell 0.1.1 is taken for a stranger'
        File    = 'src\Yav.Console\Install\Installer.cs'
        Find    = '        return found.Exists ? found : ReadEarlierManifest(Path.Combine(directory, EarlierManifestName));'
        Replace = '        return found.Exists || directory.Length > 0 ? found : ReadEarlierManifest(Path.Combine(directory, EarlierManifestName));'
        Filter  = 'FullyQualifiedName~InstallerTests.An_installation_of_yav_shell_0_1_1'
    },
    @{
        Name    = 'the program is not compared by where its path leads'
        File    = 'src\Yav.Platform\Install\InstallSystem.cs'
        Find    = '        var final = FinalPathOf(wanted) ?? wanted;'
        Replace = '        var final = wanted;'
        Filter  = 'FullyQualifiedName~WindowsInstallSystemTests'
    },
    @{
        Name    = 'a failure nobody foresaw in the offer keeps the shell from starting'
        File    = 'src\Yav.Console\Install\InstallOffer.cs'
        Find    = '        ex is not OutOfMemoryException && !(ex is OperationCanceledException && cancellationToken.IsCancellationRequested);'
        Replace = '        ex is not OutOfMemoryException && !(ex is OperationCanceledException && cancellationToken.IsCancellationRequested) && ex is not InvalidOperationException;'
        Filter  = 'FullyQualifiedName~InstallOfferTests'
    },
    @{
        Name    = 'a no that could not be saved is said to be remembered'
        File    = 'src\Yav.Console\Install\InstallOffer.cs'
        Find    = '                await world.Output.WriteLineAsync(rememberDecline()'
        Replace = '                await world.Output.WriteLineAsync(rememberDecline() || true'
        Filter  = 'FullyQualifiedName~InstallOfferTests'
    },
    @{
        Name    = 'a window that was asked to be hidden is taken for one somebody sees'
        File    = 'src\Yav.Console\Install\ConsoleWindow.cs'
        Find    = '    internal static bool IsHidden(uint flags, ushort showWindow) => (flags & StartfUseShowWindow) != 0 && showWindow == SwHide;'
        Replace = '    internal static bool IsHidden(uint flags, ushort showWindow) => flags == uint.MaxValue && showWindow == SwHide;'
        Filter  = 'FullyQualifiedName~InstallOfferTests'
    },
    @{
        Name    = 'an installation takes a directory that holds files of someone else'
        File    = 'src\Yav.Console\Install\Installer.cs'
        Find    = '        if (earlier is null && Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any(entry => !IsLeftover(entry)))'
        Replace = '        if (earlier is null && directory.Length == 0)'
        Filter  = 'FullyQualifiedName~Yav.Tests.Packaging.InstallerTests'
    },
    @{
        Name    = 'a manifest that cannot be read is taken for no installation at all'
        File    = 'src\Yav.Console\Install\Installer.cs'
        Find    = '        if (manifestExists && earlier is null)'
        Replace = '        if (manifestExists && earlier is null && directory.Length == 0)'
        Filter  = 'FullyQualifiedName~Yav.Tests.Packaging.InstallerTests'
    },
    @{
        Name    = 'the data directory is taken for an installation directory'
        File    = 'src\Yav.Console\Install\Installer.cs'
        Find    = '        if (LongPath.IsSameOrInside(directory, _dataDirectory) || LongPath.IsSameOrInside(_dataDirectory, directory))'
        Replace = '        if (LongPath.IsSameOrInside(directory, _dataDirectory) && directory.Length == 0)'
        Filter  = 'FullyQualifiedName~Yav.Tests.Packaging.InstallerTests'
    },
    @{
        Name    = 'a short name of a directory is taken for another directory'
        File    = 'src\Yav.Platform\Install\LongPath.cs'
        Find    = '    public static string Of(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));'
        Replace = '    public static string Of(string path) => Path.TrimEndingDirectorySeparator(path);'
        Filter  = 'FullyQualifiedName~Yav.Tests.Packaging.InstallerTests|FullyQualifiedName~WindowsInstallSystemTests'
    },
    @{
        Name    = 'a second installation takes over the entry of the first'
        File    = 'src\Yav.Console\Install\Installer.cs'
        Find    = '        if (options.Register && Current() is { } other && !LongPath.Same(other.InstallLocation, directory))'
        Replace = '        if (options.Register && Current() is { } other && other.InstallLocation.Length == 0)'
        Filter  = 'FullyQualifiedName~Yav.Tests.Packaging.InstallerTests'
    },
    @{
        Name    = 'files of an earlier version stay when the new one has none of them'
        File    = 'src\Yav.Console\Install\Installer.cs'
        Find    = '                if (!installed.Any(f => SameName(f.Path, old.Path)) && Inside(directory, old.Path) is { } stale && File.Exists(stale))'
        Replace = '                if (old.Path.Length == 0 && Inside(directory, old.Path) is { } stale && File.Exists(stale))'
        Filter  = 'FullyQualifiedName~Yav.Tests.Packaging.InstallerTests'
    },
    @{
        Name    = 'an interrupted installation leaves files that belong to nobody'
        File    = 'src\Yav.Console\Install\Installer.cs'
        Find    = '            WriteManifest(manifestPath, new InstallManifest(Product, _version, DateTimeOffset.UtcNow, planned));
            begun = true;'
        Replace = '            begun = planned.Count > 0;'
        Filter  = 'FullyQualifiedName~Yav.Tests.Packaging.InstallerTests'
    },
    @{
        Name    = 'a damaged file of the installation is left as it is'
        File    = 'src\Yav.Console\Install\Installer.cs'
        Find    = '                installed.Add(Write(directory, file.Path, file.Open));'
        Replace = '                installed.Add(File.Exists(Path.Combine(directory, file.Path)) ? Describe(directory, file.Path) : Write(directory, file.Path, file.Open));'
        Filter  = 'FullyQualifiedName~Yav.Tests.Packaging.InstallerTests'
    },
    @{
        Name    = 'a program that does not start is installed and put on the path'
        File    = 'src\Yav.Console\Install\Installer.cs'
        Find    = '        if (!string.Equals(reported, "yav " + _version, StringComparison.Ordinal))'
        Replace = '        if (reported is null)'
        Filter  = 'FullyQualifiedName~Yav.Tests.Packaging.InstallerTests'
    },
    @{
        Name    = 'the path loses its kind when the installation writes it'
        File    = 'src\Yav.Console\Install\Installer.cs'
        Find    = '// Only the text changes: the kind of the value, and with it whether Windows expands it, stays.
                    _system.WriteUserPath(path with { Value = changed });'
        Replace = '// Only the text changes: the kind of the value, and with it whether Windows expands it, stays.
                    _system.WriteUserPath(new UserPath(changed, Expandable: true));'
        Filter  = 'FullyQualifiedName~Yav.Tests.Packaging.InstallerTests'
    },
    @{
        Name    = 'a path entry that only begins like the directory is removed with it'
        File    = 'src\Yav.Console\Install\PathList.cs'
        Find    = '        return string.Join('';'', path.Split('';'').Where(part => Comparable(part) != wanted));'
        Replace = '        return string.Join('';'', path.Split('';'').Where(part => !Comparable(part).StartsWith(wanted, StringComparison.Ordinal)));'
        Filter  = 'FullyQualifiedName~PathListTests'
    },
    @{
        Name    = 'empty parts of the path are dropped when the installation is removed'
        File    = 'src\Yav.Console\Install\PathList.cs'
        Find    = '        return string.Join('';'', path.Split('';'').Where(part => Comparable(part) != wanted));'
        Replace = '        return string.Join('';'', path.Split('';'').Where(part => part.Length > 0 && Comparable(part) != wanted));'
        Filter  = 'FullyQualifiedName~PathListTests'
    },
    @{
        Name    = 'the directory is added to the path although it is there already'
        File    = 'src\Yav.Console\Install\PathList.cs'
        Find    = '        if (Contains(path, directory))'
        Replace = '        if (path.Length < 0)'
        Filter  = 'FullyQualifiedName~PathListTests'
    },
    @{
        Name    = 'removing an installation removes what the user put there'
        File    = 'src\Yav.Console\Install\Installer.cs'
        Find    = '        if (!removed && !IsVolumeRoot(root) && !Directory.EnumerateFileSystemEntries(root).Any())
        {
            removed = TryDeleteDirectory(root);'
        Replace = '        if (!removed && !IsVolumeRoot(root))
        {
            Directory.Delete(root, recursive: true);
            removed = true;'
        Filter  = 'FullyQualifiedName~Yav.Tests.Packaging.InstallerTests'
    },
    @{
        Name    = 'a directory without an installation is removed as if it were one'
        File    = 'src\Yav.Console\Install\Installer.cs'
        Find    = '        if (ReadInstallation(root).Manifest is not { } manifest)'
        Replace = '        var manifest = ReadInstallation(root).Manifest ?? new InstallManifest(Product, _version, DateTimeOffset.UtcNow, []);
        if (manifest is null)'
        Filter  = 'FullyQualifiedName~Yav.Tests.Packaging.InstallerTests'
    },
    @{
        Name    = 'the program that runs is deleted with the other files'
        File    = 'src\Yav.Console\Install\Installer.cs'
        Find    = '                    left = path;
                    continue;'
        Replace = '                    left = path;'
        Filter  = 'FullyQualifiedName~Yav.Tests.Packaging.InstallerTests'
    },
    @{
        Name    = 'the program that runs is not named for the caller to delete'
        File    = 'src\Yav.Console\Install\Installer.cs'
        Find    = '                    left = path;
                    continue;'
        Replace = '                    continue;'
        Filter  = 'FullyQualifiedName~Yav.Tests.Packaging.InstallerTests'
    },
    @{
        Name    = 'an entry under installed apps of another directory is removed'
        File    = 'src\Yav.Console\Install\Installer.cs'
        Find    = '            if (registration is not null && LongPath.Same(registration.InstallLocation, root))'
        Replace = '            if (registration is not null)'
        Filter  = 'FullyQualifiedName~Yav.Tests.Packaging.InstallerTests'
    },
    @{
        Name    = 'an installation goes on while yav runs from its directory'
        File    = 'src\Yav.Console\Install\Installer.cs'
        Find    = '        var running = _system.ProcessesRunning(executable).Where(id => id != Environment.ProcessId).ToList();'
        Replace = '        var running = _system.ProcessesRunning(executable).Where(id => id == -1).ToList();'
        Filter  = 'FullyQualifiedName~Yav.Tests.Packaging.InstallerTests'
    },
    @{
        Name    = 'the process that installs counts as one that runs the program'
        File    = 'src\Yav.Console\Install\Installer.cs'
        Find    = '        var running = _system.ProcessesRunning(executable).Where(id => id != Environment.ProcessId).ToList();'
        Replace = '        var running = _system.ProcessesRunning(executable).ToList();'
        Filter  = 'FullyQualifiedName~Yav.Tests.Packaging.InstallerTests'
    },
    @{
        Name    = 'processes of a copy elsewhere count as running the program'
        File    = 'src\Yav.Platform\Install\InstallSystem.cs'
        Find    = '                    && (LongPath.Same(image, wanted) || LongPath.Same(image, final) || (FinalPathOf(image) is { } leadsTo && LongPath.Same(leadsTo, final))))'
        Replace = '                    && image.Length > 0)'
        Filter  = 'FullyQualifiedName~WindowsInstallSystemTests'
    },
    @{
        Name    = 'a manifest removes a file outside the installation directory'
        File    = 'src\Yav.Console\Install\Installer.cs'
        Find    = '        return full.StartsWith(WithSeparator(root), StringComparison.OrdinalIgnoreCase) ? full : null;'
        Replace = '        return full;'
        Filter  = 'FullyQualifiedName~Yav.Tests.Packaging.InstallerTests'
    },
    @{
        Name    = 'a read-only file of the installation stops a new installation'
        File    = 'src\Yav.Console\Install\Installer.cs'
        Find    = '            ClearReadOnly(target);
            File.Move(temporary, target, overwrite: true);'
        Replace = '            File.Move(temporary, target, overwrite: true);'
        Filter  = 'FullyQualifiedName~Yav.Tests.Packaging.InstallerTests'
    },
    @{
        Name    = 'a path that cannot be read as text is replaced by the directory alone'
        File    = 'src\Yav.Console\Install\Installer.cs'
        Find    = '            throw new InstallException($"The PATH of your account could not be read: {ex.Message} {consequence}", ex);'
        Replace = '            _ = (ex, consequence);
            return new UserPath(string.Empty, Expandable: true);'
        Filter  = 'FullyQualifiedName~Yav.Tests.Packaging.InstallerTests'
    },
    @{
        Name    = 'relative PATH entries are searched'
        File    = 'src\Yav.Platform\Processes\ExecutableResolver.cs'
        Find    = 'if (directory.Length == 0 || !Path.IsPathFullyQualified(directory))'
        Replace = 'if (directory.Length == 0)'
        Filter  = 'FullyQualifiedName~ExecutableResolverTests'
        Extra   = @(
            @{ Find = 'candidate = Path.Combine(directory, command);'; Replace = 'candidate = Path.Combine(Path.GetFullPath(directory, workingDirectory ?? Environment.CurrentDirectory), command);' }
        )
    },
    @{
        Name    = 'trailing backslashes are not doubled for batch launchers'
        File    = 'src\Yav.Platform\Processes\CommandLine.cs'
        Find    = "inner.Append('\\', trailing);"
        Replace = '_ = trailing;'
        Filter  = 'FullyQualifiedName~CommandLineTests|FullyQualifiedName~ProcessRunnerTests'
    },
    @{
        Name    = 'a build of the source tree installs itself'
        File    = 'src\Yav.Console\Install\InstallCommand.cs'
        Find    = 'if (!world.IsSingleFile)'
        Replace = 'if (world.IsSingleFile && !world.IsSingleFile)'
        Filter  = 'FullyQualifiedName~InstallCommandTests'
    },
    @{
        Name    = 'a console without a window is taken for one that closes with yav'
        File    = 'src\Yav.Console\Install\ConsoleWindow.cs'
        Find    = 'public static bool IsOwn(int processesSharingConsole, bool hasWindow) => processesSharingConsole == 1 && hasWindow;'
        Replace = 'public static bool IsOwn(int processesSharingConsole, bool hasWindow) => processesSharingConsole == 1;'
        Filter  = 'FullyQualifiedName~InstallOfferTests'
    },
    @{
        Name    = 'a no to the offer in an open console is asked again'
        File    = 'src\Yav.Console\Install\InstallOffer.cs'
        Find    = 'return situation.OwnWindow || !situation.DeclinedBefore ? OfferKind.Install : OfferKind.None;'
        Replace = 'return OfferKind.Install;'
        Filter  = 'FullyQualifiedName~InstallOfferTests'
    },
    @{
        Name    = 'an answer other than yes removes the data of the user'
        File    = 'src\Yav.Console\Install\InstallCommand.cs'
        Find    = 'removeData = string.Equals(answer?.Trim(), "yes", StringComparison.OrdinalIgnoreCase);'
        Replace = 'removeData = answer is not null;'
        Filter  = 'FullyQualifiedName~InstallCommandTests'
    },
    @{
        Name    = 'a directory that holds no data of yav is removed as its data'
        File    = 'src\Yav.Console\Install\DataDirectory.cs'
        Find    = '        if (!File.Exists(Path.Combine(full, "yav.db")))'
        Replace = '        if (full.Length == 0)'
        Filter  = 'FullyQualifiedName~InstallCommandTests'
    },
    @{
        Name    = 'removing the data follows a link out of the data directory'
        File    = 'src\Yav.Console\Install\DataDirectory.cs'
        Find    = 'if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))'
        Replace = 'if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint) && entry is not DirectoryInfo)'
        Filter  = 'FullyQualifiedName~InstallCommandTests.Removing_the_data_does_not_enter'
    },
    @{
        Name    = 'a console that finds yav is told to change its path'
        File    = 'src\Yav.Console\Install\InstallReport.cs'
        Find    = 'if (findsIt && !ownWindow && !PathList.Contains(inheritedPath, directory))'
        Replace = 'if (findsIt && !ownWindow)'
        Filter  = 'FullyQualifiedName~InstallCommandTests'
    },
    @{
        Name    = 'an installed version that is only different is offered to be replaced'
        File    = 'src\Yav.Console\Install\InstallOffer.cs'
        Find    = 'Parse(installed) is { } old && Parse(version) is { } current && old < current;'
        Replace = 'Parse(installed) is { } old && Parse(version) is { } current && old != current;'
        Filter  = 'FullyQualifiedName~InstallOfferTests'
    },
    @{
        Name    = 'the installed program that removed itself is left behind'
        File    = 'src\Yav.Console\Install\InstallCommand.cs'
        Find    = '                world.DeleteAfterExit(left, removal);'
        Replace = '                _ = (left, removal);'
        Filter  = 'FullyQualifiedName~InstallCommandTests'
    },
    @{
        Name    = 'the running program is handed to the deletion before its window was closed'
        File    = 'src\Yav.Console\Install\InstallCommand.cs'
        Find    = '        using (outcome?.ProgramLeftAt is not null && world.OwnWindow ? PosixSignalRegistration.Create(PosixSignal.SIGHUP, _ => HandOver()) : null)'
        Replace = '        HandOver();
        using (outcome?.ProgramLeftAt is not null && world.OwnWindow ? PosixSignalRegistration.Create(PosixSignal.SIGHUP, _ => HandOver()) : null)'
        Filter  = 'FullyQualifiedName~InstallCommandTests'
    },
    @{
        Name    = 'a path that cmd would expand is handed to cmd'
        File    = 'src\Yav.Console\Install\InstallSurroundings.cs'
        Find    = "Path.GetDirectoryName(program) is not null && program.IndexOfAny(['%', '!', '`"']) < 0;"
        Replace = 'Path.GetDirectoryName(program) is not null;'
        Filter  = 'FullyQualifiedName~InstallCommandTests'
    },
    @{
        Name    = 'the manifest of a program that was left behind is deleted'
        File    = 'src\Yav.Console\Install\Installer.cs'
        Find    = "            if (left is null)`n            {`n                Delete(manifestPath);`n            }"
        Replace = "            if (left is null || left.Length > 0)`n            {`n                Delete(manifestPath);`n            }"
        Filter  = 'FullyQualifiedName~InstallerTests'
    },
    @{
        Name    = 'a window opened from explorer closes before its result is read'
        File    = 'src\Yav.Console\Install\InstallCommand.cs'
        Find    = 'if (!world.OwnWindow || !world.CanAsk)'
        Replace = 'if (world.OwnWindow || !world.OwnWindow)'
        Filter  = 'FullyQualifiedName~InstallCommandTests|FullyQualifiedName~InstallOfferTests'
    },
    @{
        Name    = 'an option of uninstall is taken by install'
        File    = 'src\Yav.Console\Cli\CommandLine.cs'
        Find    = 'case "--remove-data" when mode == CliMode.Uninstall && inline is null:'
        Replace = 'case "--remove-data" when inline is null:'
        Filter  = 'FullyQualifiedName~CommandLineTests'
    },
    @{
        Name    = 'a bare carriage return is passed through'
        File    = 'src\Yav.Core\Text\TerminalSanitizer.cs'
        Find    = "                _pendingCarriageReturn = true;`n                return;"
        Replace = "                output.Append(c);`n                return;"
        Filter  = 'FullyQualifiedName~TerminalSanitizerTests'
    },
    @{
        Name    = 'the start of a tool counts from when its report is taken up'
        File    = 'src\Yav.Coordinator\TurnDriver.cs'
        Find    = 'startedAt: reportedAt >= state.Began ? reportedAt : null);'
        Replace = 'startedAt: null);'
        Filter  = 'FullyQualifiedName~Yav.Tests.Coordinator.ToolTimeTests'
    },
    @{
        Name    = 'the end of a tool counts from when its report is taken up'
        File    = 'src\Yav.Coordinator\TurnDriver.cs'
        Find    = '            tool.End(reportedAt);'
        Replace = '            tool.Dispose();'
        Filter  = 'FullyQualifiedName~Yav.Tests.Coordinator.ToolTimeTests'
    },
    @{
        Name    = 'a report from before the turn is taken for the start of a tool'
        File    = 'src\Yav.Coordinator\TurnDriver.cs'
        Find    = 'reportedAt >= state.Began ? reportedAt : null'
        Replace = 'reportedAt'
        Filter  = 'FullyQualifiedName~Yav.Tests.Coordinator.ToolTimeTests'
    },
    @{
        Name    = 'a tool that is not a command counts from when its reports are taken up'
        File    = 'src\Yav.Coordinator\TurnDriver.cs'
        Find    = 'BeginTool(context, role, tool.ItemId, "tool", tool.At, state);'
        Replace = 'BeginTool(context, role, tool.ItemId, "tool", default, state);'
        Filter  = 'FullyQualifiedName~Yav.Tests.Coordinator.ToolTimeTests'
    },
    @{
        Name    = 'a span given when it began begins when it is started'
        File    = 'src\Yav.Core\Timing\Timing.cs'
        Find    = '        var began = startedAt is { } given && given < now ? given : now;'
        Replace = '        var began = now;'
        Filter  = 'FullyQualifiedName~Yav.Tests.Core.TimingTests'
    },
    @{
        Name    = 'a span begins at a time still to come'
        File    = 'src\Yav.Core\Timing\Timing.cs'
        Find    = '        var began = startedAt is { } given && given < now ? given : now;'
        Replace = '        var began = startedAt ?? now;'
        Filter  = 'FullyQualifiedName~Yav.Tests.Core.TimingTests'
    },
    @{
        Name    = 'a span given when it began loses the time before it was started'
        File    = 'src\Yav.Core\Timing\Timing.cs'
        Find    = '            var measured = entry.Before + clock.GetElapsedTime(entry.StartTimestamp);'
        Replace = '            var measured = clock.GetElapsedTime(entry.StartTimestamp);'
        Filter  = 'FullyQualifiedName~Yav.Tests.Core.TimingTests'
    },
    @{
        Name    = 'a span ends at a time it cannot have ended at'
        File    = 'src\Yav.Core\Timing\Timing.cs'
        Find    = 'Duration = given >= TimeSpan.Zero && given <= measured ? given : measured'
        Replace = 'Duration = given ?? measured'
        Filter  = 'FullyQualifiedName~Yav.Tests.Core.TimingTests'
    },
    @{
        Name    = 'a span given when it ended ends when it is ended'
        File    = 'src\Yav.Core\Timing\Timing.cs'
        Find    = 'Duration = given >= TimeSpan.Zero && given <= measured ? given : measured'
        Replace = 'Duration = given > TimeSpan.MaxValue ? given : measured'
        Filter  = 'FullyQualifiedName~Yav.Tests.Core.TimingTests'
    },
    @{
        Name    = 'an answer that changes nothing is left in checking'
        File    = 'src\Yav.Core\Runs\RunState.cs'
        Find    = 'RunState.ReadyToApply, RunState.Completed, RunState.Repairing,'
        Replace = 'RunState.ReadyToApply, RunState.Repairing,'
        Filter  = 'FullyQualifiedName~RunPipelineTests.An_answer_that_changes_nothing_completes_without_a_review'
    },
    @{
        Name    = 'an answer that changes nothing is completed before what it left was frozen'
        File    = 'src\Yav.Core\Runs\RunState.cs'
        Find    = "        [RunState.Implementing] =`n        [`n            RunState.AwaitingApproval, RunState.Checking, RunState.Blocked,"
        Replace = "        [RunState.Implementing] =`n        [`n            RunState.AwaitingApproval, RunState.Checking, RunState.Completed, RunState.Blocked,"
        Filter  = 'FullyQualifiedName~RunStateMachineTests'
    },
    @{
        Name    = 'claude as model a is offered a question to the user that nobody can see'
        File    = 'src\Yav.Adapters\Claude\ClaudeCliSession.cs'
        Find    = '            arguments.AddRange(["--disallowedTools", "AskUserQuestion"]);'
        Replace = '            _ = arguments;'
        Filter  = 'FullyQualifiedName~The_implementer_edits_freely_and_asks_the_user_for_everything_else'
    },
    @{
        Name    = 'a route that is not a subscription is used without asking'
        File    = 'src\Yav.Core\Agents\AgentContracts.cs'
        Find    = 'Authenticated && Route == AccountRouteKind.Subscription && Billing == BillingKind.IncludedInSubscription'
        Replace = 'Authenticated && Route != AccountRouteKind.ApiKey'
        Filter  = 'FullyQualifiedName~Yav.Tests.Core.AccountRouteTests'
    },
    @{
        Name    = 'a subscription blocks a run until it was acknowledged'
        File    = 'src\Yav.Coordinator\AdapterCatalog.cs'
        Find    = '(reading.Auth!.UsedWithoutAsking || _trust.IsRouteAcknowledged(routeKey))'
        Replace = '(_trust.IsRouteAcknowledged(routeKey))'
        Filter  = 'FullyQualifiedName~ShellModelCommandTests.A_subscription_the_agent_is_signed_in_to_is_used_without_asking_and_without_a_record'
    },
    @{
        Name    = 'first run: a subscription is asked about'
        File    = 'src\Yav.Console\Shell\Commands.Setup.cs'
        Find    = "                || auth.UsedWithoutAsking`r`n"
        Replace = "`r`n"
        Filter  = 'FullyQualifiedName~ShellSetupTests.A_first_request_asks_for_both_models_only_and_is_then_sent_through_the_subscriptions_without_asking'
    },
    @{
        Name    = 'login asks about a subscription'
        File    = 'src\Yav.Console\Shell\Commands.Models.cs'
        Find    = 'var acknowledged = auth.UsedWithoutAsking || _services.Database'
        Replace = 'var acknowledged = _services.Database'
        Filter  = 'FullyQualifiedName~ShellModelCommandTests.A_subscription_the_agent_is_signed_in_to_is_used_without_asking_and_without_a_record'
    },
    @{
        Name    = 'doctor does not say a subscription is used without asking'
        File    = 'src\Yav.Console\Doctor\Doctor.cs'
        Find    = '            case RoutePolicy.RequiresAcknowledgement when auth.UsedWithoutAsking:'
        Replace = '            case RoutePolicy.RequiresAcknowledgement when auth.UsedWithoutAsking && !auth.UsedWithoutAsking:'
        Filter  = 'FullyQualifiedName~CliCommandTests.Doctor_reports_a_subscription_the_agent_is_signed_in_to_as_used_without_asking'
    },
    @{
        Name    = 'a stale review is accepted'
        File    = 'src\Yav.Core\Runs\AcceptanceGate.cs'
        Find    = 'if (!review.Binding.Matches(expected, out var difference))'
        Replace = 'if (!review.Binding.Matches(review.Binding, out var difference))'
        Filter  = 'FullyQualifiedName~AcceptanceGateTests'
    },
    @{
        Name    = 'checks: required again by default'
        File    = 'src\Yav.Core\Settings\AppSettings.cs'
        Find    = '    public bool RequireGates { get; init; }'
        Replace = '    public bool RequireGates { get; init; } = true;'
        Filter  = 'FullyQualifiedName~SettingsStoreTests|FullyQualifiedName~ReviewOnlyTests.By_default'
    },
    @{
        Name    = 'checks: the 0.2.0 requireGates key is read again'
        File    = 'src\Yav.Core\Settings\AppSettings.cs'
        Find    = '    [JsonPropertyName("requireChecks")]'
        Replace = '    [JsonPropertyName("requireGates")]'
        Filter  = 'FullyQualifiedName~SettingsStoreTests.The_requireGates'
    },
    @{
        Name    = 'checks: optional runs on the review alone without saying so'
        File    = 'src\Yav.Coordinator\RunCoordinator.Prepare.cs'
        Find    = '                    null, ProblemSeverity.Warning, "checks-optional",'
        Replace = '                    null, ProblemSeverity.Info, "checks-optional-unsaid",'
        Filter  = 'FullyQualifiedName~ReviewOnlyTests.With_checks_optional'
    },
    @{
        Name    = 'checks: detected checks are not suggested'
        File    = 'src\Yav.Coordinator\RunCoordinator.Prepare.cs'
        Find    = '                if (validation.Detect(project).Count > 0)'
        Replace = '                if (validation.Detect(project).Count > 99)'
        Filter  = 'FullyQualifiedName~ReviewOnlyTests.With_checks_optional_checks_the_project_suggests'
    },
    @{
        Name    = 'checks: optional accepts on the review alone although the checks cannot be read'
        File    = 'src\Yav.Coordinator\RunCoordinator.Prepare.cs'
        Find    = '        if (!policy.RequireGates && !state.Effective.RequiredGates.Any())
        {
            if (state.Errors.Count > 0 || state.Trust == ConfigurationTrust.Invalid)'
        Replace = '        if (!policy.RequireGates && !state.Effective.RequiredGates.Any())
        {
            if (state.Errors.Count < 0 && state.Trust == ConfigurationTrust.Invalid)'
        Filter  = 'FullyQualifiedName~ReviewOnlyTests.With_checks_optional_a_configuration_file'
    },
    @{
        Name    = 'first run: a request is not sent again once what blocked it was settled'
        File    = 'src\Yav.Console\Shell\Commands.Setup.cs'
        Find    = '        await StartRunAsync(sent with { Round = sent.Round + 1, Settled = all }, cancellationToken).ConfigureAwait(false);'
        Replace = '        _ = all;'
        Filter  = 'FullyQualifiedName~ShellSetupTests.A_first_request'
    },
    @{
        Name    = 'first run: the request is sent again without what was attached'
        File    = 'src\Yav.Console\Shell\Commands.Setup.cs'
        Find    = '        await StartRunAsync(sent with { Round = sent.Round + 1, Settled = all }, cancellationToken).ConfigureAwait(false);'
        Replace = '        await StartRunAsync(sent with { Attachments = [], Round = sent.Round + 1, Settled = all }, cancellationToken).ConfigureAwait(false);'
        Filter  = 'FullyQualifiedName~ShellSetupTests.What_was_attached'
    },
    @{
        Name    = 'first run: questions are asked when nobody can answer them'
        File    = 'src\Yav.Console\Shell\Commands.Setup.cs'
        Find    = '        if (!_input.CanAsk || _session.ProjectPath is not { } project || !RefusedBeforeItBegan(outcome))'
        Replace = '        if (_session.ProjectPath is not { } project || !RefusedBeforeItBegan(outcome))'
        Filter  = 'FullyQualifiedName~ShellSetupTests.Nothing_is_asked_when_nobody_can_answer'
    },
    @{
        Name    = 'first run: Model B may be the model of Model A under Quality Lock'
        File    = 'src\Yav.Console\Shell\Commands.Setup.cs'
        Find    = '            var notThis = current.QualityLock && current.Strict ? current.ModelA : null;'
        Replace = '            var notThis = current.QualityLock && current.Strict ? null : current.ModelA;'
        Filter  = 'FullyQualifiedName~ShellSetupTests.Model_B_has_to_be_another_model'
    },
    @{
        Name    = 'first run: an account route is acknowledged without a typed yes'
        File    = 'src\Yav.Console\Shell\Commands.Models.cs'
        Find    = '        if (!await ConfirmAsync($"Use ''{auth.RouteLabel}'' for runs of YAV, billed as stated above?", cancellationToken).ConfigureAwait(false))
        {
            return false;
        }'
        Replace = '        _ = await ConfirmAsync($"Use ''{auth.RouteLabel}'' for runs of YAV, billed as stated above?", cancellationToken).ConfigureAwait(false);'
        Filter  = 'FullyQualifiedName~ShellSetupTests.An_account_route_is_acknowledged_only_by_a_typed_yes'
    },
    @{
        Name    = 'first run: acceptance on the review alone is recorded without a typed yes'
        File    = 'src\Yav.Console\Shell\Commands.Setup.cs'
        Find    = '        if (!await ConfirmAsync("Accept candidates of this project on the review alone?", cancellationToken).ConfigureAwait(false))
        {
            return Settling.Declined;
        }'
        Replace = '        _ = await ConfirmAsync("Accept candidates of this project on the review alone?", cancellationToken).ConfigureAwait(false);'
        Filter  = 'FullyQualifiedName~ShellSetupTests.Checks_that_were_declined'
    },
    @{
        Name    = 'first run: missing ignored files are accepted without a typed yes'
        File    = 'src\Yav.Console\Shell\Commands.Setup.cs'
        Find    = '        if (!await ConfirmAsync("Accept this difference for the project?", cancellationToken).ConfigureAwait(false))
        {
            return Settling.Declined;
        }'
        Replace = '        _ = await ConfirmAsync("Accept this difference for the project?", cancellationToken).ConfigureAwait(false);'
        Filter  = 'FullyQualifiedName~ShellSetupTests.Files_the_isolated_workspace_would_not_have_are_not_accepted_by_a_no'
    },
    @{
        Name    = 'first run: an effort the model does not list is taken'
        File    = 'src\Yav.Console\Shell\Commands.Setup.cs'
        Find    = '            if (model.SupportedEfforts.FirstOrDefault(e => e.Equals(answer, StringComparison.OrdinalIgnoreCase)) is { } listed)'
        Replace = '            if ((model.SupportedEfforts.FirstOrDefault(e => e.Equals(answer, StringComparison.OrdinalIgnoreCase)) ?? answer) is { } listed)'
        Filter  = 'FullyQualifiedName~ShellSetupTests.An_effort_the_model_does_not_rank'
    },
    @{
        Name    = 'first run: a problem settled for a request is asked about again'
        File    = 'src\Yav.Console\Shell\Commands.Setup.cs'
        Find    = '        problems.Where(p => p.Severity == ProblemSeverity.Blocking && CanSettle(p.Code) && !settled.Contains(Key(p))).ToList();'
        Replace = '        problems.Where(p => p.Severity == ProblemSeverity.Blocking && CanSettle(p.Code)).ToList();'
        Filter  = 'FullyQualifiedName~ShellSetupTests.Only_blocking_problems'
    },
    @{
        Name    = 'first run: a request without a project is not asked where it goes'
        File    = 'src\Yav.Console\Shell\InteractiveShell.cs'
        Find    = '            if (!await AskForProjectAsync(cancellationToken).ConfigureAwait(false))'
        Replace = '            if (!await Task.FromResult(false).ConfigureAwait(false))'
        Filter  = 'FullyQualifiedName~ShellSetupTests.A_request_typed_without_a_project'
    },
    @{
        Name    = 'first run: the sign-in of Claude Code is offered by YAV'
        File    = 'src\Yav.Console\Shell\Commands.Setup.cs'
        Find    = '    private static bool MayStartSignIn(IAgentAdapter adapter) => adapter.Provider != "anthropic";'
        Replace = '    private static bool MayStartSignIn(IAgentAdapter adapter) => adapter.Provider.Length > 0;'
        Filter  = 'FullyQualifiedName~ShellSetupTests.A_role_whose_Claude_Code_is_not_signed_in'
    },
    @{
        Name    = 'first run: the experimental interface is not marked'
        File    = 'src\Yav.Console\Shell\Commands.Setup.cs'
        Find    = '(agent.First().Experimental ? " - experimental interface" : string.Empty)'
        Replace = '(!agent.First().Experimental ? " - experimental interface" : string.Empty)'
        Filter  = 'FullyQualifiedName~ShellSetupTests.The_first_run_marks_the_experimental_interface'
    },
    @{
        Name    = 'first run: the folder of downloads is taken for a project'
        File    = 'src\Yav.Console\Shell\InteractiveShell.cs'
        Find    = '        if (string.Equals(Path.TrimEndingDirectorySeparator(KnownFolders.Downloads), full, StringComparison.OrdinalIgnoreCase))'
        Replace = '        if (string.Equals(Path.TrimEndingDirectorySeparator(KnownFolders.Downloads) + "?", full, StringComparison.OrdinalIgnoreCase))'
        Filter  = 'FullyQualifiedName~ShellSetupTests.The_folder_of_downloads'
    },
    @{
        Name    = 'first run: started outside a shell, the directory it starts in is taken for the project'
        File    = 'src\Yav.Console\Shell\InteractiveShell.cs'
        Find    = '        if (projectPath is null && startedOutsideAShell)'
        Replace = '        if (projectPath is null && startedOutsideAShell && projectPath is not null)'
        Filter  = 'FullyQualifiedName~ShellSetupTests.Started_outside_a_shell'
    },
    @{
        Name    = 'first run: quality gates required leaves the project accepted on the review alone'
        File    = 'src\Yav.Console\Shell\Commands.Models.cs'
        Find    = '                if (value == "required" && _session.ProjectPath is { } open && _services.Database.WithdrawReviewOnly(open))'
        Replace = '                if (value == "required" && _session.ProjectPath is { } open && !_services.Database.IsReviewOnlyAccepted(open))'
        Filter  = 'FullyQualifiedName~ShellSetupTests.Quality_shows'
    },
    @{
        Name    = 'first run: a project whose approved checks are optional is said to have none'
        File    = 'src\Yav.Console\Shell\Commands.Models.cs'
        Find    = '        return (state.Effective.Gates.Count == 0'
        Replace = '        return (state.Effective.Gates.Count >= 0'
        Filter  = 'FullyQualifiedName~ShellSetupTests.A_project_whose_approved_checks_are_all_optional'
    },
    @{
        Name    = 'first run: a failure while an answer is recorded ends the shell'
        File    = 'src\Yav.Console\Shell\Commands.Setup.cs'
        Find    = '            catch (Exception ex) when (IsCommandFailure(ex))'
        Replace = '            catch (Exception ex) when (IsCommandFailure(ex) && _session.ExitRequested)'
        Filter  = 'FullyQualifiedName~ShellSetupTests.A_failure_while_an_answer_is_recorded'
    },
    @{
        Name    = 'first run: what was attached before a project was selected is dropped'
        File    = 'src\Yav.Console\Shell\Commands.Setup.cs'
        Find    = '        _session.Attachments.AddRange(attached.Where(a => !_session.Attachments.Contains(a, StringComparer.OrdinalIgnoreCase)));'
        Replace = '        _ = attached;'
        Filter  = 'FullyQualifiedName~ShellSetupTests.What_was_attached_before_a_project'
    },
    @{
        Name    = 'first run: a drive is taken for the project of a request'
        File    = 'src\Yav.Console\Shell\Commands.Setup.cs'
        Find    = '            if (Directory.Exists(full) && !IsSensibleProject(full))'
        Replace = '            if (Directory.Exists(full) && !IsSensibleProject(full) && _session.ExitRequested)'
        Filter  = 'FullyQualifiedName~ShellSetupTests.A_drive_is_not_taken'
    },
    @{
        Name    = 'first run: without a project, the folder is asked for when nobody can answer'
        File    = 'src\Yav.Console\Shell\InteractiveShell.cs'
        Find    = '                _ui.Warn("No project is selected, so the request was not sent. Select one with /open <path>.");
                return;'
        Replace = '                _ui.Warn("No project is selected, so the request was not sent. Select one with /open <path>.");'
        Filter  = 'FullyQualifiedName~ShellSetupTests.Without_a_project_nothing_is_asked'
    },
    @{
        Name    = 'first run: an unusable answer is asked for without end'
        File    = 'src\Yav.Console\Shell\Commands.Setup.cs'
        Find    = '    private const int Attempts = 3;'
        Replace = '    private const int Attempts = 1000;'
        Filter  = 'FullyQualifiedName~ShellSetupTests.Three_answers_that_are_not_on_the_list'
    },
    @{
        Name    = 'first run: no answer to the approval of the checks leads to the review alone'
        File    = 'src\Yav.Console\Shell\Commands.Setup.cs'
        Find    = '                if (_confirmUnanswered)'
        Replace = '                if (_confirmUnanswered && _session.ExitRequested)'
        Filter  = 'FullyQualifiedName~ShellSetupTests.Control_C_at_the_approval_of_the_checks'
    },
    @{
        Name    = 'first run: Control+C as a signal at a question is left to whoever asked'
        File    = 'src\Yav.Console\Shell\InteractiveShell.cs'
        Find    = '            if (Volatile.Read(ref _guideQuestion) is not { } question)'
        Replace = '            if (Volatile.Read(ref _guideQuestion) is not { } question || question.Token.CanBeCanceled)'
        Filter  = 'FullyQualifiedName~ShellSetupTests.Control_C_as_a_signal'
    },
    @{
        Name    = 'first run: Model A may be the model of a Model B that stays, under Quality Lock'
        File    = 'src\Yav.Console\Shell\Commands.Setup.cs'
        Find    = '            var kept = settings.QualityLock && settings.Strict && !needB ? settings.ModelB : null;'
        Replace = '            var kept = settings.QualityLock && settings.Strict && needB ? settings.ModelB : null;'
        Filter  = 'FullyQualifiedName~ShellSetupTests.Model_A_has_to_be_another_model'
    },
    @{
        Name    = 'first run: a refusal no answer settled is sent again'
        File    = 'src\Yav.Console\Shell\Commands.Setup.cs'
        Find    = '        if (settled.Count == 0)'
        Replace = '        if (settled.Count == 0 && _session.ExitRequested)'
        Filter  = 'FullyQualifiedName~ShellSetupTests.A_refusal_that_no_question_settled'
    },
    @{
        Name    = 'first run: a request is sent again once more than the rounds allow'
        File    = 'src\Yav.Console\Shell\Commands.Setup.cs'
        Find    = '        return round >= MaxRounds ? (SetupStep.TooManyRounds, open) : (SetupStep.Ask, open);'
        Replace = '        return round > MaxRounds ? (SetupStep.TooManyRounds, open) : (SetupStep.Ask, open);'
        Filter  = 'FullyQualifiedName~ShellSetupTests.A_request_is_asked_for_until_the_rounds'
    },
    @{
        Name    = 'first run: an agent that is not found is asked to be chosen again'
        File    = 'src\Yav.Console\Shell\Commands.Setup.cs'
        Find    = '"model-unavailable", "adapter-unknown"];'
        Replace = '"model-unavailable", "adapter-unknown", "adapter-not-found"];'
        Filter  = 'FullyQualifiedName~ShellSetupTests.Only_blocking_problems'
    },
    @{
        Name    = 'first run: what was settled before a decline is advised again'
        File    = 'src\Yav.Console\Shell\InteractiveShell.cs'
        Find    = 'p.Remedy is not null && !settled.Contains(SetupProblems.Key(p))'
        Replace = 'p.Remedy is not null'
        Filter  = 'FullyQualifiedName~ShellSetupTests.A_decline_keeps_what_was_attached'
    },
    @{
        Name    = 'first run: what was attached is dropped when the request is not sent'
        File    = 'src\Yav.Console\Shell\Commands.Setup.cs'
        Find    = '        _session.Attachments.AddRange(back);'
        Replace = '        _ = back;'
        Filter  = 'FullyQualifiedName~ShellSetupTests.A_decline_keeps_what_was_attached'
    },
    @{
        Name    = 'a pass with blocking findings is accepted by the parser'
        File    = 'src\Yav.Core\Runs\ReviewOutputParser.cs'
        Find    = 'case ReviewVerdict.Pass when blocking > 0:'
        Replace = 'case ReviewVerdict.Pass when blocking > 1000:'
        Filter  = 'FullyQualifiedName~ReviewOutputParserTests'
    },
    @{
        Name    = 'an unsupported effort is silently lowered'
        File    = 'src\Yav.Core\Profiles\ProfileResolver.cs'
        Find    = 'return (selection.EffortPreference, VerificationStatus.Unsupported);
        }

        var maximum'
        Replace = 'return (supported[^1], VerificationStatus.Verified);
        }

        var maximum'
        Filter  = 'FullyQualifiedName~ProfileResolverTests'
    },
    @{
        Name    = 'a project whose review alone was accepted stays blocked'
        File    = 'src\Yav.Coordinator\RunCoordinator.Prepare.cs'
        Find    = 'if (policy.RequireGates && !state.Effective.RequiredGates.Any() && _services.Trust.IsReviewOnlyAccepted(project))'
        Replace = 'if (policy.RequireGates && !state.Effective.RequiredGates.Any() && _services.Trust.IsReviewOnlyAccepted(project) && project.Length < 0)'
        Filter  = 'FullyQualifiedName~ReviewOnlyTests'
    },
    @{
        Name    = 'every project is taken as accepted on the review alone'
        File    = 'src\Yav.Coordinator\RunCoordinator.Prepare.cs'
        Find    = 'if (policy.RequireGates && !state.Effective.RequiredGates.Any() && _services.Trust.IsReviewOnlyAccepted(project))'
        Replace = 'if (policy.RequireGates && !state.Effective.RequiredGates.Any())'
        Filter  = 'FullyQualifiedName~ReviewOnlyTests'
    },
    @{
        Name    = 'the review alone accepts a candidate although a check is approved'
        File    = 'src\Yav.Coordinator\RunCoordinator.Prepare.cs'
        Find    = 'if (policy.RequireGates && !state.Effective.RequiredGates.Any() && _services.Trust.IsReviewOnlyAccepted(project))'
        Replace = 'if (policy.RequireGates && _services.Trust.IsReviewOnlyAccepted(project))'
        Filter  = 'FullyQualifiedName~ReviewOnlyTests'
    },
    @{
        Name    = 'the acceptance of the review alone is announced but not honored'
        File    = 'src\Yav.Coordinator\RunCoordinator.Prepare.cs'
        Find    = 'policy = policy with { RequireGates = false };'
        Replace = '_ = policy;'
        Filter  = 'FullyQualifiedName~ReviewOnlyTests'
    },
    @{
        Name    = 'that the review alone accepts a candidate is not shown as a warning'
        File    = 'src\Yav.Coordinator\RunCoordinator.Prepare.cs'
        Find    = 'null, ProblemSeverity.Warning, "review-only",'
        Replace = 'null, ProblemSeverity.Info, "review-only",'
        Filter  = 'FullyQualifiedName~ReviewOnlyTests'
    },
    @{
        Name    = 'the acceptance of the review alone is read from another acknowledgement'
        File    = 'src\Yav.Storage\YavDatabase.Trust.cs'
        Find    = 'public bool IsReviewOnlyAccepted(string projectPath) => IsAcknowledged(ReviewOnlyKind, ProjectKey(projectPath));'
        Replace = 'public bool IsReviewOnlyAccepted(string projectPath) => IsAcknowledged(InPlaceKind, ProjectKey(projectPath));'
        Filter  = 'FullyQualifiedName~TrustStoreTests'
    },
    @{
        Name    = 'withdrawing the acceptance of the review alone withdraws another acknowledgement'
        File    = 'src\Yav.Storage\YavDatabase.Trust.cs'
        Find    = 'transaction, ("$kind", ReviewOnlyKind), ("$subject", subject)) == 0)'
        Replace = 'transaction, ("$kind", InPlaceKind), ("$subject", subject)) == 0)'
        Filter  = 'FullyQualifiedName~TrustStoreTests'
    },
    @{
        Name    = 'the review alone accepts a candidate although the approved checks cannot be read'
        File    = 'src\Yav.Coordinator\RunCoordinator.Prepare.cs'
        Find    = 'if (state.Trust == ConfigurationTrust.Invalid || state.Errors.Count > 0)'
        Replace = 'if (state.Trust == ConfigurationTrust.Invalid && state.Errors.Count < 0)'
        Filter  = 'FullyQualifiedName~ReviewOnlyTests'
    },
    @{
        Name    = 'why the approval cannot be read is not said beside a file that was not approved (review alone)'
        File    = 'src\Yav.Coordinator\RunCoordinator.Prepare.cs'
        Find    = '                AddErrors(problems, state.Errors);
                break;

            case ConfigurationTrust.Changed:'
        Replace = '                break;

            case ConfigurationTrust.Changed:'
        Filter  = 'FullyQualifiedName~ReviewOnlyTests'
    },
    @{
        Name    = 'a candidate accepted on the review alone is applied after the acceptance was withdrawn'
        File    = 'src\Yav.Coordinator\RunCoordinator.Deliver.cs'
        Find    = 'if (WithdrawnAfterTheRun(run, profile))'
        Replace = 'if (WithdrawnAfterTheRun(run, profile) && runId.Length < 0)'
        Filter  = 'FullyQualifiedName~ReviewOnlyTests'
    },
    @{
        Name    = 'a withdrawal of the review alone before the run keeps its candidate from being applied'
        File    = 'src\Yav.Coordinator\RunCoordinator.Deliver.cs'
        Find    = '&& withdrawn >= run.CreatedAt;'
        Replace = '&& withdrawn >= DateTimeOffset.MinValue;'
        Filter  = 'FullyQualifiedName~ReviewOnlyTests'
    },
    @{
        Name    = 'accepting the review alone once more does not make its candidate applicable again'
        File    = 'src\Yav.Coordinator\RunCoordinator.Deliver.cs'
        Find    = '&& !_services.Trust.IsReviewOnlyAccepted(run.ProjectPath)'
        Replace = '&& run.ProjectPath.Length >= 0'
        Filter  = 'FullyQualifiedName~ReviewOnlyTests'
    },
    @{
        Name    = 'when the review alone was withdrawn is not kept'
        File    = 'src\Yav.Storage\YavDatabase.Trust.cs'
        Find    = '"SELECT acknowledged_at FROM acknowledgements WHERE kind = $kind AND subject = $subject;"'
        Replace = '"SELECT acknowledged_at FROM acknowledgements WHERE kind = $kind AND subject = $subject AND 1 = 0;"'
        Filter  = 'FullyQualifiedName~TrustStoreTests|FullyQualifiedName~ReviewOnlyTests'
    },
    @{
        Name    = 'a withdrawal of the review alone is listed as an acknowledgement'
        File    = 'src\Yav.Storage\YavDatabase.Trust.cs'
        Find    = 'WHERE kind <> $withdrawn ORDER BY acknowledged_at;'
        Replace = 'WHERE kind <> $withdrawn OR 1 = 1 ORDER BY acknowledged_at;'
        Filter  = 'FullyQualifiedName~TrustStoreTests'
    },
    @{
        Name    = 'cached tokens are counted twice'
        File    = 'src\Yav.Core\Agents\Usage.cs'
        Find    = 'uncached = Math.Max(0, input.Value - cachedInput.Value);'
        Replace = 'uncached = input.Value;'
        Filter  = 'FullyQualifiedName~TokenCountsTests'
    },
    @{
        Name    = 'apply overwrites a file the user edited meanwhile'
        File    = 'src\Yav.Workspace\WorkspaceService.Apply.cs'
        Find    = 'return new ApplyConflict(
                    file.Path, ApplyConflictKind.ConcurrentEdit,
                    "The file was edited in the project after the task started.", AutoMergePossible: !file.IsBinary);'
        Replace = 'return null;'
        Filter  = 'FullyQualifiedName~ApplyTests'
    },
    @{
        Name    = 'apply writes the current workspace file instead of the frozen candidate'
        File    = 'src\Yav.Workspace\WorkspaceService.Apply.cs'
        Find    = '            var target = ResolveInside(workspace.OriginalRoot, entry.Path)!;
            try
            {
                Perform(entry, target);'
        Replace = '            var target = ResolveInside(workspace.OriginalRoot, entry.Path)!;
            try
            {
                var live = ResolveInside(workspace.RootPath, entry.Path)!;
                if (entry.Kind != ChangeKind.Deleted && File.Exists(live)) { File.Copy(live, target, true); } else { Perform(entry, target); }'
        Filter  = 'FullyQualifiedName~ApplyTests'
    },
    @{
        Name    = 'a likely secret is copied without permission'
        File    = 'src\Yav.Workspace\WorkspaceService.Prepare.cs'
        Find    = 'if (SecretPaths.LooksLikeSecret(relative) && !Glob.MatchesAny(configuration.AllowSecrets, relative))'
        Replace = 'if (relative.Length == 0)'
        Filter  = 'FullyQualifiedName~PrepareTests'
    },
    @{
        Name    = 'uncommitted work is not carried into the workspace'
        File    = 'src\Yav.Workspace\WorkspaceService.Prepare.cs'
        Find    = '                        Overlay(originalRoot, root, inspection.DirtyEntries, notes);'
        Replace = '                        notes.Add("skipped");'
        Filter  = 'FullyQualifiedName~PrepareTests|FullyQualifiedName~FreezeTests'
    },
    @{
        Name    = 'discard deletes a directory outside YAV storage'
        File    = 'src\Yav.Workspace\WorkspaceService.Prepare.cs'
        Find    = 'if (!Path.GetFullPath(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))'
        Replace = 'if (path.Length == 0 && prefix.Length == 0)'
        Filter  = 'FullyQualifiedName~DiscardAndGuardTests'
        Extra   = @(
            @{ Find = '        DeleteDirectory(directory);

        if (mode == WorkspaceMode.GitWorktree && _git.IsAvailable && Directory.Exists(originalRoot))'; Replace = '        DeleteDirectory(root);
        DeleteDirectory(directory);

        if (mode == WorkspaceMode.GitWorktree && _git.IsAvailable && Directory.Exists(originalRoot))' }
        )
    },
    @{
        Name    = 'undo follows a journal path out of the project'
        File    = 'src\Yav.Workspace\WorkspaceService.cs'
        Find    = '            if (segment is ".." or ".")
            {
                return null;
            }'
        Replace = '            if (segment is ".")
            {
                return null;
            }'
        Filter  = 'FullyQualifiedName~UndoTests|FullyQualifiedName~MechanicalEditTests'
        Extra   = @(
            @{ Find = 'return full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? full : null;'; Replace = 'return full;' }
        )
    },
    @{
        Name    = 'undo overwrites a file the user edited after the apply'
        File    = 'src\Yav.Workspace\WorkspaceService.Apply.cs'
        Find    = '            if (!string.Equals(current, entry.PostImageHash, StringComparison.Ordinal))
            {
                var detail = entry.Kind == ChangeKind.Deleted'
        Replace = '            if (current == "never")
            {
                var detail = entry.Kind == ChangeKind.Deleted'
        Filter  = 'FullyQualifiedName~UndoTests'
    },
    @{
        Name    = 'reading a repository rewrites its index'
        File    = 'src\Yav.Workspace\GitClient.cs'
        Find    = '["GIT_OPTIONAL_LOCKS"] = "0",'
        Replace = '["GIT_OPTIONAL_LOCKS"] = "1",'
        Filter  = 'FullyQualifiedName~InspectionTests|FullyQualifiedName~PrepareTests'
    },
    @{
        Name    = 'a changed file with unchanged size and time is missed'
        File    = 'src\Yav.Workspace\WorkspaceService.cs'
        Find    = '                var hash = BlobStore.HashFile(fullPath);
                var store ='
        Replace = '                var hash = BlobStore.HashBytes(System.Text.Encoding.UTF8.GetBytes(relative + info.Length + info.LastWriteTimeUtc.Ticks));
                var store ='
        Filter  = 'FullyQualifiedName~FreezeTests'
    },
    @{
        Name    = 'a rollback after a crash overwrites a later edit by the user'
        File    = 'src\Yav.Workspace\WorkspaceService.Apply.cs'
        Find    = '                if (!string.Equals(current, entry.PostImageHash, StringComparison.Ordinal))
                {
                    // Someone edited the file since. It is left alone.
                    complete = false;
                    continue;
                }'
        Replace = '                if (current == "never")
                {
                    complete = false;
                    continue;
                }'
        Filter  = 'FullyQualifiedName~ApplyTests'
    },
    @{
        Name    = 'a merge with conflicting edits is reported as merged'
        File    = 'src\Yav.Workspace\WorkspaceService.Merge.cs'
        Find    = '                if (conflicts != 0)
                {
                    unresolved.Add(path);
                    continue;
                }'
        Replace = '                if (conflicts < 0)
                {
                    unresolved.Add(path);
                    continue;
                }'
        Filter  = 'FullyQualifiedName~MergeTests'
    },
    @{
        Name    = 'a gate with an unmet requirement is run anyway'
        File    = 'src\Yav.Validation\ValidationService.cs'
        Find    = '        if (unmet.Count > 0)
        {'
        Replace = '        if (unmet.Count > 1000)
        {'
        Filter  = 'FullyQualifiedName~GateRunTests'
    },
    @{
        Name    = 'a changed gate configuration takes effect without approval'
        File    = 'src\Yav.Validation\ValidationService.cs'
        Find    = '        return new ProjectConfigurationState(
            trustedHash is null ? ConfigurationTrust.Untrusted : ConfigurationTrust.Changed,
            effective, pending, trustedHash, fileHash, file, errors);'
        Replace = '        return new ProjectConfigurationState(
            trustedHash is null ? ConfigurationTrust.Untrusted : ConfigurationTrust.Changed,
            pending, pending, trustedHash, fileHash, file, errors);'
        Filter  = 'FullyQualifiedName~ConfigurationTrustTests'
    },
    @{
        Name    = 'a gate that timed out is reported as failed by the candidate'
        File    = 'src\Yav.Validation\ValidationService.cs'
        Find    = '        if (result.TimedOut)
        {'
        Replace = '        if (result.TimedOut && result.ExitCode == 12345)
        {'
        Filter  = 'FullyQualifiedName~GateRunTests'
    },
    @{
        Name    = 'the claude reviewer keeps tools that can write and run code'
        File    = 'src\Yav.Adapters\Claude\ClaudeCliSession.cs'
        Find    = '            arguments.Add("--restricted");
            arguments.AddRange(["--tools", "Read,Glob,Grep"]);'
        Replace = '            arguments.AddRange(["--tools", "default"]);'
        Filter  = 'FullyQualifiedName~ClaudeSessionTests|FullyQualifiedName~ClaudeApprovalTests'
    },
    @{
        Name    = 'one harmless tool is enough to confirm the boundary of the claude reviewer'
        File    = 'src\Yav.Adapters\Claude\ClaudeCliSession.cs'
        Find    = 'var readOnly = tools.Count > 0 && tools.All(ReadOnlyTools.Contains) && problem is null;'
        Replace = 'var readOnly = tools.Count > 0 && tools.Any(ReadOnlyTools.Contains) && problem is null;'
        Filter  = 'FullyQualifiedName~ClaudeSessionTests'
    },
    @{
        Name    = 'the tool a structured result is handed back with is taken for one that can change something'
        File    = 'src\Yav.Adapters\Claude\ClaudeCliSession.cs'
        Find    = '"EndConversation", ResultTool,'
        Replace = '"EndConversation",'
        Filter  = 'FullyQualifiedName~ClaudeSessionTests|FullyQualifiedName~TwoProviderTests'
    },
    @{
        Name    = 'the effort claude was asked for is reported as the effort in effect'
        File    = 'src\Yav.Adapters\Claude\ClaudeCliSession.cs'
        Find    = '                    Effort: applied?.Effort,'
        Replace = '                    Effort: _request.Effort,'
        Filter  = 'FullyQualifiedName~ClaudeSessionTests'
    },
    @{
        Name    = 'a model that is sent no effort is reported as if nothing were known'
        File    = 'src\Yav.Adapters\Claude\ClaudeCliSession.cs'
        Find    = '            JsonValueKind.Null => NoEffort,'
        Replace = '            JsonValueKind.Null => null,'
        Filter  = 'FullyQualifiedName~ClaudeSessionTests'
    },
    @{
        Name    = 'an answer that names no level is taken for no effort'
        File    = 'src\Yav.Adapters\Claude\ClaudeCliSession.cs'
        Find    = '        !applied.TryGetProperty("effort", out var effort) ? null'
        Replace = '        !applied.TryGetProperty("effort", out var effort) ? NoEffort'
        Filter  = 'FullyQualifiedName~ClaudeSessionTests'
    },
    @{
        Name    = 'the settings of claude are asked for once and believed for every later turn'
        File    = 'src\Yav.Adapters\Claude\ClaudeCliSession.cs'
        Find    = '        if (_settingsNotAnswered)'
        Replace = '        if (_settingsNotAnswered || _controlCounter > 0)'
        Filter  = 'FullyQualifiedName~ClaudeSessionTests'
    },
    @{
        Name    = 'an agent that did not say is asked again before every turn'
        File    = 'src\Yav.Adapters\Claude\ClaudeCliSession.cs'
        Find    = '        _settingsNotAnswered = true;'
        Replace = '        _settingsNotAnswered = false;'
        Filter  = 'FullyQualifiedName~ClaudeSessionTests'
    },
    @{
        Name    = 'an agent that does not answer holds the turn up for longer than it may take to start'
        File    = 'src\Yav.Adapters\Claude\ClaudeCliSession.cs'
        Find    = '        var patience = _adapter.Options.EffectiveStartupTimeout;'
        Replace = '        var patience = _adapter.Options.EffectiveStartupTimeout * 10;'
        Filter  = 'FullyQualifiedName~ClaudeSessionTests'
    },
    @{
        Name    = 'an answer to what somebody else asked is taken for the settings'
        File    = 'src\Yav.Adapters\Claude\ClaudeCliSession.cs'
        Find    = '                        && _answers.TryGetValue(answered, out var waiting))'
        Replace = '                        && (_answers.TryGetValue(answered, out var waiting) || (waiting = _answers.Values.FirstOrDefault()) is not null))'
        Filter  = 'FullyQualifiedName~ClaudeSessionTests'
    },
    @{
        Name    = 'why claude did not say what is in effect is kept from the user'
        File    = 'src\Yav.Adapters\Claude\ClaudeCliSession.cs'
        Find    = '        await NotAnsweredAsync(given.Error ?? "its answer does not say what is applied").ConfigureAwait(false);'
        Replace = '        _settingsNotAnswered = true;'
        Filter  = 'FullyQualifiedName~ClaudeSessionTests'
    },
    @{
        Name    = 'a tool that has ended is reported without what it was given'
        File    = 'src\Yav.Adapters\Claude\ClaudeCliSession.cs'
        Find    = 'new ToolActivity(Now, id, tool.Name, tool.Summary, failed ? "failed" : "completed", tool.Where)'
        Replace = 'new ToolActivity(Now, id, tool.Name, string.Empty, failed ? "failed" : "completed")'
        Filter  = 'FullyQualifiedName~ClaudeTurnTests'
    },
    @{
        Name    = 'the reviewer is given the settings of the workspace'
        File    = 'src\Yav.Adapters\Claude\ClaudeCliSession.cs'
        Find    = '"--setting-sources", _request.ProjectTrusted && !reviewer ? "user,project,local" : "user",'
        Replace = '"--setting-sources", _request.ProjectTrusted ? "user,project,local" : "user",'
        Filter  = 'FullyQualifiedName~ClaudeSessionTests'
    },
    @{
        Name    = 'what is reviewed can name files that claude then attaches'
        File    = 'src\Yav.Adapters\Claude\ClaudeCliSession.cs'
        Find    = '                    if (_request.Role == AgentRole.Reviewer || request.QuotesOutput)'
        Replace = '                    if (request.QuotesOutput)'
        Filter  = 'FullyQualifiedName~ClaudeSessionTests'
    },
    @{
        Name    = 'a turn that could not be started leaves the conversation believing that one is running'
        File    = 'src\Yav.Adapters\Claude\ClaudeCliSession.cs'
        Find    = '        catch
        {
            // Nothing was started, so nobody is there who would report the end of this turn.'
        Replace = '        catch (InvalidOperationException)
        {
            // Nothing was started, so nobody is there who would report the end of this turn.'
        Filter  = 'FullyQualifiedName~ClaudeSessionTests'
    },
    @{
        Name    = 'an agent that does not read its answer leaves the turn without an end'
        File    = 'src\Yav.Adapters\Claude\ClaudeCliSession.cs'
        Find    = '        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Whatever it was: what follows has to happen'
        Replace = '        catch (OutOfMemoryException ex)
        {
            // Whatever it was: what follows has to happen'
        Filter  = 'FullyQualifiedName~ClaudeSessionTests'
    },
    @{
        Name    = 'a window of claude that is used more than fully is shown as hardly used'
        File    = 'src\Yav.Adapters\Claude\ClaudeCliSession.cs'
        Find    = '            ? (int?)Math.Round(fraction * 100)'
        Replace = '            ? (int?)Math.Round(fraction <= 1 ? fraction * 100 : fraction)'
        Filter  = 'FullyQualifiedName~ClaudeTurnTests'
    },
    @{
        Name    = 'a limit of the account as claude words it is taken for a failure'
        File    = 'src\Yav.Adapters\Claude\ClaudeCliSession.cs'
        Find    = '            || (message.Contains("hit your", StringComparison.OrdinalIgnoreCase) && message.Contains("limit", StringComparison.OrdinalIgnoreCase)))'
        Replace = '            )'
        Filter  = 'FullyQualifiedName~ClaudeTurnTests'
    },
    @{
        Name    = 'what a tool was pointed at is shown with its whole path'
        File    = 'src\Yav.Coordinator\WorkspacePaths.cs'
        Find    = '            return tool with { Path = Pointed(where, workspaceRoot) };'
        Replace = '            return tool;'
        Filter  = 'FullyQualifiedName~WorkspacePathsTests'
    },
    @{
        Name    = 'an effort that could not be asked for is called verified'
        File    = 'src\Yav.Coordinator\SettingsVerifier.cs'
        Find    = '        if (role.RequestedEffort.Length == 0 && role.EffortSupport == VerificationStatus.Verified)'
        Replace = '        if (role.RequestedEffort.Length == 0)'
        Filter  = 'FullyQualifiedName~SettingsVerifierTests'
    },
    @{
        Name    = 'where the effort was reported is not said'
        File    = 'src\Yav.Coordinator\SettingsVerifier.cs'
        Find    = '            Compare(ProfileSettings.Effort, role.RequestedEffort, effort, source: effortSource);'
        Replace = '            Compare(ProfileSettings.Effort, role.RequestedEffort, effort);'
        Filter  = 'FullyQualifiedName~SettingsVerifierTests'
    },
    @{
        Name    = 'a boundary that is known not to hold does not keep a review from being sent'
        File    = 'src\Yav.Coordinator\SettingsVerifier.cs'
        Find    = '        if (role.Role == AgentRole.Reviewer && early.BoundaryProblem is { } problem)'
        Replace = '        if (role.Role == AgentRole.Reviewer && policy.Strict && policy.QualityLock && early.BoundaryProblem is { } problem)'
        Filter  = 'FullyQualifiedName~SettingsVerifierTests|FullyQualifiedName~ClaudeAsAModelTests'
    },
    @{
        Name    = 'a model that is named later is missed before the turn'
        File    = 'src\Yav.Coordinator\SettingsVerifier.cs'
        Find    = '        if (!modelFollows)'
        Replace = '        if (modelFollows || model is not null)'
        Filter  = 'FullyQualifiedName~SettingsVerifierTests'
    },
    @{
        Name    = 'what claude says before a turn is not looked at'
        File    = 'src\Yav.Coordinator\TurnDriver.cs'
        Find    = '                if (verification.Violations.Count > 0)
                {
                    // Recorded only when the turn is not started'
        Replace = '                if (verification.Violations.Count > 99)
                {
                    // Recorded only when the turn is not started'
        Filter  = 'FullyQualifiedName~ClaudeAsAModelTests'
    },
    @{
        Name    = 'settings of the workspace that were loaded do not stand in the way of a review'
        File    = 'src\Yav.Adapters\Claude\ClaudeCliSession.cs'
        Find    = '        var planted = applied.SettingsSources.Where(WorkspaceSettings.Contains).ToList();'
        Replace = '        var planted = applied.SettingsSources.Where(source => source.Length == 0).ToList();'
        Filter  = 'FullyQualifiedName~ClaudeSessionTests|FullyQualifiedName~ClaudeAsAModelTests'
    },
    @{
        Name    = 'a review is confirmed although claude did not say which settings it loaded'
        File    = 'src\Yav.Adapters\Claude\ClaudeCliSession.cs'
        Find    = '        if (applied is null)
        {
            return "Claude Code did not say which settings it loaded'
        Replace = '        if (applied is null)
        {
            return null;
        }

        if (applied.SettingsSources.Count > 99)
        {
            return "Claude Code did not say which settings it loaded'
        Filter  = 'FullyQualifiedName~ClaudeSessionTests|FullyQualifiedName~ClaudeAsAModelTests'
    },
    @{
        Name    = 'what was asked before a turn is believed for the turn after it as well'
        File    = 'src\Yav.Adapters\Claude\ClaudeCliSession.cs'
        Find    = '            // What was asked holds for one prompt.
            _askedOf = null;'
        Replace = '            // What was asked holds for one prompt.
            _askedOf = _process;'
        Filter  = 'FullyQualifiedName~ClaudeSessionTests|FullyQualifiedName~ClaudeAsAModelTests'
    },
    @{
        Name    = 'a claude code that ended before it could be asked is taken for one that does not say'
        File    = 'src\Yav.Adapters\Claude\ClaudeCliSession.cs'
        Find    = '        if (ended)
        {
            throw new AgentProtocolException(await EndedBeforePromptAsync("before it said what it works with")'
        Replace = '        if (ended && applied is not null)
        {
            throw new AgentProtocolException(await EndedBeforePromptAsync("before it said what it works with")'
        Filter  = 'FullyQualifiedName~ClaudeAsAModelTests|FullyQualifiedName~TwoProviderTests'
    },
    @{
        Name    = 'the tool a result is handed back with is reported as work of the agent'
        File    = 'src\Yav.Adapters\Claude\ClaudeCliSession.cs'
        Find    = '                    else if (name != ResultTool)'
        Replace = '                    else'
        Filter  = 'FullyQualifiedName~ClaudeTurnTests'
    },
    @{
        Name    = 'a tool is shown a second time when it has ended'
        File    = 'src\Yav.Console\Rendering\RunEventFormatter.cs'
        Find    = '                if (_toolsShown.Remove(item))'
        Replace = '                if (_toolsShown.Remove(item) && tool.Tool.Length == 0)'
        Filter  = 'FullyQualifiedName~RunEventFormatterTests'
    },
    @{
        Name    = 'a request that was refused before anything began does not say why'
        File    = 'src\Yav.Console\Shell\InteractiveShell.cs'
        Find    = '        if (!run.SaidHowItEnded && !string.IsNullOrWhiteSpace(outcome.Reason) && outcome.Problems.Count == 0)'
        Replace = '        if (run.SaidHowItEnded && !string.IsNullOrWhiteSpace(outcome.Reason) && outcome.Problems.Count == 0)'
        Filter  = 'FullyQualifiedName~ShellRunCommandTests'
    },
    @{
        Name    = 'what a run said when it ended is said a second time'
        File    = 'src\Yav.Console\Shell\InteractiveShell.cs'
        Find    = '        if (!run.SaidHowItEnded && !string.IsNullOrWhiteSpace(outcome.Reason) && outcome.Problems.Count == 0)'
        Replace = '        if (!string.IsNullOrWhiteSpace(outcome.Reason) && outcome.Problems.Count == 0)'
        Filter  = 'FullyQualifiedName~ShellRunCommandTests'
    },
    @{
        Name    = 'a tool of an agent is recorded when it begins and again when it has ended'
        File    = 'src\Yav.Coordinator\RunPublisher.cs'
        Find    = 'ToolActivity e when e.Status is not ("inProgress" or "started")'
        Replace = 'ToolActivity e when e.Status is not "inProgress"'
        Filter  = 'FullyQualifiedName~ClaudeAsAModelTests'
    },
    @{
        Name    = 'a command waits for an agent without saying so'
        File    = 'src\Yav.Console\Shell\Commands.Models.cs'
        Find    = 'if (Coordinator.Catalog.Find(adapterId) is { } asked && (refresh || !Coordinator.Catalog.IsFresh(adapterId)))'
        Replace = 'if (Coordinator.Catalog.Find(adapterId) is { } asked && refresh && !Coordinator.Catalog.IsFresh(adapterId))'
        Filter  = 'FullyQualifiedName~ShellModelCommandTests'
    },
    @{
        Name    = 'an agent is said to be asked although what it said a moment ago is used'
        File    = 'src\Yav.Console\Shell\Commands.Models.cs'
        Find    = 'if (Coordinator.Catalog.Find(adapterId) is { } asked && (refresh || !Coordinator.Catalog.IsFresh(adapterId)))'
        Replace = 'if (Coordinator.Catalog.Find(adapterId) is { } asked)'
        Filter  = 'FullyQualifiedName~ShellModelCommandTests'
    },
    @{
        Name    = 'what is known about an agent counts as recent when it is old'
        File    = 'src\Yav.Coordinator\AdapterCatalog.cs'
        Find    = '_readings.TryGetValue(adapterId, out var reading) && _clock.GetElapsedTime(reading.Timestamp) <= _maxAge;'
        Replace = '_readings.TryGetValue(adapterId, out var reading) && _clock.GetElapsedTime(reading.Timestamp) > _maxAge;'
        Filter  = 'FullyQualifiedName~ShellModelCommandTests'
    },
    @{
        Name    = 'what the reviewer wrote is cut where it is to be read'
        File    = 'src\Yav.Console\Shell\Commands.Run.cs'
        Find    = '                        _ui.Quote(written);'
        Replace = '                        _ui.Quote(Shorten(written, 300));'
        Filter  = 'FullyQualifiedName~ShellRunCommandTests'
    },
    @{
        Name    = 'an api key in agent output is not removed'
        File    = 'src\Yav.Adapters\Claude\ClaudeCliSession.cs'
        Find    = '        if (frame.Span.IndexOf(secret) < 0)
        {
            return frame;
        }'
        Replace = '        if (frame.Span.IndexOf(secret) >= -1)
        {
            return frame;
        }'
        Filter  = 'FullyQualifiedName~ClaudeCredentialTests'
    },
    @{
        Name    = 'claude implements in an untrusted project with a key in its environment'
        File    = 'src\Yav.Adapters\Claude\ClaudeCliAdapter.cs'
        Find    = 'if (request.Role == AgentRole.Implementer && !request.ProjectTrusted && ApiKey() is not null)'
        Replace = 'if (request.Role == AgentRole.Implementer && !request.ProjectTrusted && ApiKey() is null)'
        Filter  = 'FullyQualifiedName~ClaudeCredentialTests'
    },
    @{
        Name    = 'repository-controlled settings are loaded for an untrusted project'
        File    = 'src\Yav.Adapters\Claude\ClaudeCliSession.cs'
        Find    = '"--setting-sources", _request.ProjectTrusted && !reviewer ? "user,project,local" : "user",'
        Replace = '"--setting-sources", !reviewer ? "user,project,local" : "user",'
        Filter  = 'FullyQualifiedName~ClaudeSessionTests'
    },
    @{
        Name    = 'codex is not told that the user decides about access'
        File    = 'src\Yav.Adapters\Codex\CodexAppServerAdapter.cs'
        Find    = '                writer.WriteString("approvalsReviewer", UserReviewer);'
        Replace = '                _ = UserReviewer;'
        Filter  = 'FullyQualifiedName~Yav.Tests.Adapters.Codex'
    },
    @{
        Name    = 'a conversation whose approvals codex decides by itself is used'
        File    = 'src\Yav.Adapters\Codex\CodexAppServerAdapter.cs'
        Find    = '        UserReviewer => null,'
        Replace = '        _ when approvalsReviewer is not null => null,'
        Filter  = 'FullyQualifiedName~would_decide_itself_is_refused|FullyQualifiedName~hands_its_approvals_to_its_own_reviewer'
    },
    @{
        Name    = 'network access of a sandbox of codex is reported as closed'
        File    = 'src\Yav.Adapters\Codex\CodexAppServerAdapter.cs'
        Find    = '                return new SandboxWidening(network, folders);'
        Replace = '                return new SandboxWidening(false, folders);'
        Filter  = 'FullyQualifiedName~Yav.Tests.Adapters.Codex'
    },
    @{
        Name    = 'an approval for a change of files names no file'
        File    = 'src\Yav.Adapters\Codex\CodexAppServerSession.cs'
        Find    = '            details.AddRange(DescribeChanges(parameters.Text("itemId")));'
        Replace = '            _ = DescribeChanges(parameters.Text("itemId"));'
        Filter  = 'FullyQualifiedName~Yav.Tests.Adapters.Codex'
    },
    @{
        Name    = 'an update of the limits that carries only some values wipes the others'
        File    = 'src\Yav.Adapters\Codex\CodexRateLimits.cs'
        Find    = '            Primary = update.Primary ?? known.Primary,'
        Replace = '            Primary = update.Primary,'
        Filter  = 'FullyQualifiedName~Yav.Tests.Adapters.Codex'
    },
    @{
        Name    = 'the turn of a conversation that was given up goes on'
        File    = 'src\Yav.Adapters\Codex\CodexAppServerSession.cs'
        Find    = '            if (_refusal is null || _activeTurn is null || _refusedTurnStopped)'
        Replace = '            if (_refusal is not null || _activeTurn is null || _refusedTurnStopped)'
        Filter  = 'FullyQualifiedName~Yav.Tests.Adapters.Codex|FullyQualifiedName~CodexApprovalsReviewerTests'
    },
    @{
        Name    = 'the configuration of codex is read without the directory of the conversation'
        File    = 'src\Yav.Adapters\Codex\CodexAppServerAdapter.cs'
        Find    = '                        writer.WriteString("cwd", directory);'
        Replace = '                        _ = directory;'
        Filter  = 'FullyQualifiedName~Yav.Tests.Adapters.Codex'
    },
    @{
        Name    = 'an approval that names no command is titled as if it ran one'
        File    = 'src\Yav.Adapters\Codex\CodexAppServerSession.cs'
        Find    = '        var named = !string.IsNullOrWhiteSpace(parameters.Text("command"));'
        Replace = '        var named = true;'
        Filter  = 'FullyQualifiedName~Yav.Tests.Adapters.Codex'
    },
    @{
        Name    = 'an agent whose requests somebody else decides is run when quality lock is off'
        File    = 'src\Yav.Coordinator\SettingsVerifier.cs'
        Find    = '            if (!user)
            {'
        Replace = '            if (!user && enforce)
            {'
        Filter  = 'FullyQualifiedName~SettingsVerifierTests'
    },
    @{
        Name    = 'folders an implementer may write outside the workspace stop nothing'
        File    = 'src\Yav.Coordinator\SettingsVerifier.cs'
        Find    = '        record(ProfileSettings.WritableOutside, Nothing, folders, VerificationStatus.Mismatch, null);
        if (enforce)'
        Replace = '        record(ProfileSettings.WritableOutside, Nothing, folders, VerificationStatus.Mismatch, null);
        if (enforce && folders.Length == 0)'
        Filter  = 'FullyQualifiedName~SettingsVerifierTests|FullyQualifiedName~ProfileEnforcementTests'
    },
    @{
        Name    = 'a sandbox that reaches the network is said to be closed'
        File    = 'src\Yav.Coordinator\SettingsVerifier.cs'
        Find    = 'record(ProfileSettings.NetworkAccess, null, widening.NetworkAccess ? "on" : "off", VerificationStatus.Verified, null);'
        Replace = 'record(ProfileSettings.NetworkAccess, null, "off", VerificationStatus.Verified, null);'
        Filter  = 'FullyQualifiedName~SettingsVerifierTests|FullyQualifiedName~ProfileEnforcementTests'
    },
    @{
        Name    = 'that a sandbox reaches the network is said with every turn'
        File    = 'src\Yav.Coordinator\TurnDriver.cs'
        Find    = '            said = effective.Widening is { NetworkAccess: true } && context.NetworkAccessSaid.Add(slot.Role.Role);'
        Replace = '            said = effective.Widening is { NetworkAccess: true };'
        Filter  = 'FullyQualifiedName~ProfileEnforcementTests|FullyQualifiedName~SafeguardTests'
    },
    @{
        Name    = 'only the limit that was named last counts'
        File    = 'src\Yav.Coordinator\RunCoordinator.cs'
        Find    = '            known = [.. context.RateLimitsByName.Values];'
        Replace = '            known = context.RateLimits is null ? [] : [context.RateLimits];'
        Filter  = 'FullyQualifiedName~StopAndLimitTests'
    },
    @{
        Name    = 'the second window of a limit is not looked at'
        File    = 'src\Yav.Coordinator\RunCoordinator.cs'
        Find    = '            foreach (var window in new[] { snapshot.Primary, snapshot.Secondary })'
        Replace = '            foreach (var window in new[] { snapshot.Primary })'
        Filter  = 'FullyQualifiedName~StopAndLimitTests'
    },
    @{
        Name    = 'an agent that ended while its settings were not honored is said to be blocked only'
        File    = 'src\Yav.Coordinator\RunCoordinator.cs'
        Find    = '        if (result.PolicyViolation is { } violation && !result.SessionLost)'
        Replace = '        if (result.PolicyViolation is { } violation)'
        Filter  = 'FullyQualifiedName~CodexApprovalsReviewerTests'
    },
    @{
        Name    = 'a sign-in to the console of claude is taken for a subscription'
        File    = 'src\Yav.Adapters\Claude\ClaudeCliAdapter.cs'
        Find    = '        var key = (keySource is { Length: > 0 } && keySource != "none") || method is "api_key" or "api_key_helper";'
        Replace = '        var key = method is "api_key" or "api_key_helper";'
        Filter  = 'FullyQualifiedName~ClaudeDetectionTests'
    },
    @{
        Name    = 'a token of which nobody knows the account is called a key that is billed per token'
        File    = 'src\Yav.Adapters\Claude\ClaudeCliAdapter.cs'
        Find    = '        if (plan is null && method != "claude.ai")'
        Replace = '        if (plan is null && method == "never")'
        Filter  = 'FullyQualifiedName~ClaudeDetectionTests'
    },
    @{
        Name    = 'what claude marks as not to be allowed by one key is passed on without the mark'
        File    = 'src\Yav.Adapters\Claude\ClaudeCliSession.cs'
        Find    = '                Deliberate: request.Value.Flag("default_to_no") == true || !complete))).ConfigureAwait(false);'
        Replace = '                Deliberate: !complete))).ConfigureAwait(false);'
        Filter  = 'FullyQualifiedName~ClaudeApprovalTests|FullyQualifiedName~Yav.Tests.Console.ShellTests'
    },
    @{
        Name    = 'what asks the user something that cannot be shown is put before the user as a question'
        File    = 'src\Yav.Adapters\Claude\ClaudeCliSession.cs'
        Find    = '        if (request.Value.Flag("requires_user_interaction") == true)'
        Replace = '        if (request.Value.Flag("requires_user_interaction") == false)'
        Filter  = 'FullyQualifiedName~ClaudeApprovalTests'
    },
    @{
        Name    = 'a conversation that works with another account than the one that was shown is run'
        File    = 'src\Yav.Coordinator\SettingsVerifier.cs'
        Find    = '            if (said != shown)
            {'
        Replace = '            if (said != shown && !comparable)
            {'
        Filter  = 'FullyQualifiedName~SettingsVerifierTests|FullyQualifiedName~ClaudeAsAModelTests'
    },
    @{
        Name    = 'an account of which the route was never shown is compared with it'
        File    = 'src\Yav.Coordinator\SettingsVerifier.cs'
        Find    = '        if (account is { Route: { } said } && comparable)'
        Replace = '        if (account is { Route: { } said })'
        Filter  = 'FullyQualifiedName~SettingsVerifierTests|FullyQualifiedName~ClaudeAsAModelTests'
    },
    @{
        Name    = 'that no key is named is called a confirmed route'
        File    = 'src\Yav.Coordinator\SettingsVerifier.cs'
        Find    = '            keyConfirmed = expectsKey == true;'
        Replace = '            keyConfirmed = true;'
        Filter  = 'FullyQualifiedName~SettingsVerifierTests|FullyQualifiedName~ClaudeAsAModelTests'
    },
    @{
        Name    = 'what the user has to know about the other route is left out'
        File    = 'src\Yav.Coordinator\SettingsVerifier.cs'
        Find    = '                    + Never + (account.Note is null ? string.Empty : " " + account.Note));'
        Replace = '                    + Never + (account.Note is null ? string.Empty : string.Empty));'
        Filter  = 'FullyQualifiedName~SettingsVerifierTests|FullyQualifiedName~ClaudeAsAModelTests'
    },
    @{
        Name    = 'the account is compared only when the prompt has been sent'
        File    = 'src\Yav.Coordinator\SettingsVerifier.cs'
        Find    = '        VerifyAccount(role, Name(role), null, early.Account, Record, violations);'
        Replace = '        VerifyAccount(role, Name(role), null, null, Record, violations);'
        Filter  = 'FullyQualifiedName~SettingsVerifierTests|FullyQualifiedName~ClaudeAsAModelTests'
    },
    @{
        Name    = 'claude is not asked which account the conversation works with'
        File    = 'src\Yav.Adapters\Claude\ClaudeCliSession.cs'
        Find    = '        if (_accountNotAnswered)
        {
            return (null, false);'
        Replace = '        if (!_accountNotAnswered)
        {
            return (null, false);'
        Filter  = 'FullyQualifiedName~ClaudeSessionTests|FullyQualifiedName~ClaudeAsAModelTests'
    },
    @{
        Name    = 'a claude that did not say which account it works with is asked before every turn'
        File    = 'src\Yav.Adapters\Claude\ClaudeCliSession.cs'
        Find    = '        _accountNotAnswered = true;'
        Replace = '        _accountNotAnswered = false;'
        Filter  = 'FullyQualifiedName~ClaudeSessionTests|FullyQualifiedName~ClaudeAsAModelTests'
    },
    @{
        Name    = 'a key the conversation works with is taken for the subscription'
        File    = 'src\Yav.Adapters\Claude\ClaudeCliSession.cs'
        Find    = '            (_, { } key, _) => (AccountRouteKind.ApiKey, $"API key ({key})"),'
        Replace = '            (_, { } key, _) => (AccountRouteKind.Subscription, $"API key ({key})"),'
        Filter  = 'FullyQualifiedName~ClaudeSessionTests|FullyQualifiedName~ClaudeAsAModelTests'
    },
    @{
        Name    = 'a cloud provider the conversation works with is not seen'
        File    = 'src\Yav.Adapters\Claude\ClaudeCliSession.cs'
        Find    = '            ({ } provider, _, _) when provider != "firstParty" => (AccountRouteKind.CloudProvider, $"cloud provider ({provider})"),'
        Replace = '            ({ } provider, _, _) when provider == "never" => (AccountRouteKind.CloudProvider, $"cloud provider ({provider})"),'
        Filter  = 'FullyQualifiedName~ClaudeSessionTests|FullyQualifiedName~ClaudeAsAModelTests'
    },
    @{
        Name    = 'a key source that says none is taken for a key'
        File    = 'src\Yav.Adapters\Claude\ClaudeCliSession.cs'
        Find    = '        static string? Named(string? text) => text is { Length: > 0 } and not "none" ? text : null;'
        Replace = '        static string? Named(string? text) => text is { Length: > 0 } ? text : null;'
        Filter  = 'FullyQualifiedName~ClaudeSessionTests|FullyQualifiedName~ClaudeAsAModelTests'
    },
    @{
        Name    = 'that a review runs without the settings of the user is said of an implementation'
        File    = 'src\Yav.Adapters\Claude\ClaudeCliSession.cs'
        Find    = '            _request.Role == AgentRole.Reviewer
                ? "Claude Code runs a review without the settings of the user'
        Replace = '            _request.Role != AgentRole.Reviewer
                ? "Claude Code runs a review without the settings of the user'
        Filter  = 'FullyQualifiedName~ClaudeSessionTests|FullyQualifiedName~ClaudeAsAModelTests'
    },
    @{
        Name    = 'what the conversation says about the account is not passed on with its settings'
        File    = 'src\Yav.Adapters\Claude\ClaudeCliSession.cs'
        Find    = '                    Account: _account);'
        Replace = '                    Account: null);'
        Filter  = 'FullyQualifiedName~ClaudeSessionTests|FullyQualifiedName~ClaudeAsAModelTests'
    },
    @{
        Name    = 'programs a packaged program starts leave the job'
        File    = 'src\Yav.Platform\Native\NativeMethods.cs'
        Find    = 'PROCESS_CREATION_DESKTOP_APP_BREAKAWAY_DISABLE_PROCESS_TREE = 0x00000002;'
        Replace = 'PROCESS_CREATION_DESKTOP_APP_BREAKAWAY_DISABLE_PROCESS_TREE = 0x00000001;'
        Filter  = 'FullyQualifiedName~Yav.Tests.Platform.PackagedProgramTests'
    },
    @{
        Name    = 'the refusal of the job is not recognized for the path of a packaged program'
        File    = 'src\Yav.Platform\Processes\NativeProcess.cs'
        Find    = 'error is NativeMethods.ERROR_ACCESS_DENIED or unchecked((int)0xC0070005);'
        Replace = 'error is NativeMethods.ERROR_ACCESS_DENIED;'
        Filter  = 'FullyQualifiedName~Yav.Tests.Platform.PackagedProgramTests'
    },
    @{
        Name    = 'a packaged program runs outside its job'
        File    = 'src\Yav.Platform\Processes\NativeProcess.cs'
        Find    = '            job.Assign(process);'
        Replace = '            GC.KeepAlive(process);'
        Filter  = 'FullyQualifiedName~Yav.Tests.Platform.PackagedProgramTests'
    },
    @{
        Name    = 'a packaged program inherits every handle that can be inherited'
        File    = 'src\Yav.Platform\Processes\NativeProcess.cs'
        Find    = 'if (!NativeMethods.UpdateProcThreadAttribute(attributeList, 0, NativeMethods.PROC_THREAD_ATTRIBUTE_HANDLE_LIST, (nint)pipes, nint.Size * launch.Pipes.Length, 0, 0))'
        Replace = 'if (launch.Pipes.Length < 0)'
        Filter  = 'FullyQualifiedName~Yav.Tests.Platform.PackagedProgramTests|FullyQualifiedName~ProcessRunnerTests'
    },
    @{
        Name    = 'a program that was put into its job before it runs is never let run'
        File    = 'src\Yav.Platform\Processes\NativeProcess.cs'
        Find    = '        if (NativeMethods.ResumeThread(thread) == uint.MaxValue)'
        Replace = '        if (thread == 0 && NativeMethods.ResumeThread(thread) == uint.MaxValue)'
        Filter  = 'FullyQualifiedName~Yav.Tests.Platform.PackagedProgramTests'
    },
    @{
        Name    = 'an agent that works in another directory than the workspace is run'
        File    = 'src\Yav.Coordinator\SettingsVerifier.cs'
        Find    = '        if (WorkspacePaths.SameDirectory(workspace, reported))'
        Replace = '        if (WorkspacePaths.SameDirectory(workspace, reported) || reported.Length > 0)'
        Filter  = 'FullyQualifiedName~SettingsVerifierTests|FullyQualifiedName~SafeguardTests'
    },
    @{
        Name    = 'the directory an agent works in is not looked at in a run'
        File    = 'src\Yav.Coordinator\TurnDriver.cs'
        Find    = '_services.Clock.GetUtcNow(), slot.WorkingDirectory);'
        Replace = '_services.Clock.GetUtcNow(), null);'
        Filter  = 'FullyQualifiedName~SafeguardTests'
    },
    @{
        Name    = 'a directory that is named by its short name is another directory'
        File    = 'src\Yav.Coordinator\WorkspacePaths.cs'
        Find    = 'WithoutPrefix(Path.TrimEndingDirectorySeparator(Path.GetFullPath(WithoutPrefix(path))));'
        Replace = 'WithoutPrefix(Path.TrimEndingDirectorySeparator(WithoutPrefix(path)));'
        Filter  = 'FullyQualifiedName~WorkspacePathsTests|FullyQualifiedName~SettingsVerifierTests'
    },
    @{
        Name    = 'a directory in other letters is another directory'
        File    = 'src\Yav.Coordinator\WorkspacePaths.cs'
        Find    = 'return string.Equals(Resolved(first), Resolved(second), StringComparison.OrdinalIgnoreCase);'
        Replace = 'return string.Equals(Resolved(first), Resolved(second), StringComparison.Ordinal);'
        Filter  = 'FullyQualifiedName~WorkspacePathsTests|FullyQualifiedName~SettingsVerifierTests'
    },
    @{
        Name    = 'an answer that did not reach the agent is recorded as given'
        File    = 'src\Yav.Coordinator\TurnDriver.cs'
        Find    = '                if (sent.IsCompletedSuccessfully)'
        Replace = '                if (sent.IsCompleted)'
        Filter  = 'FullyQualifiedName~SafeguardTests'
    },
    @{
        Name    = 'folders that can be written outside the workspace are learned from a table only'
        File    = 'src\Yav.Coordinator\TurnDriver.cs'
        Find    = '            outside = effective.Widening is { AdditionalWritableRoots.Count: > 0 } && context.WritableOutsideSaid.Add(slot.Role.Role);'
        Replace = '            outside = effective.Widening is { AdditionalWritableRoots.Count: > 99 } && context.WritableOutsideSaid.Add(slot.Role.Role);'
        Filter  = 'FullyQualifiedName~ProfileEnforcementTests'
    },
    @{
        Name    = 'folders that can be written outside the workspace are said with every turn'
        File    = 'src\Yav.Coordinator\TurnDriver.cs'
        Find    = '            outside = effective.Widening is { AdditionalWritableRoots.Count: > 0 } && context.WritableOutsideSaid.Add(slot.Role.Role);'
        Replace = '            outside = effective.Widening is { AdditionalWritableRoots.Count: > 0 } && context.WritableOutsideSaid.Count >= 0;'
        Filter  = 'FullyQualifiedName~ProfileEnforcementTests|FullyQualifiedName~SafeguardTests'
    },
    @{
        Name    = 'only the first line of a text a tool is given is listed'
        File    = 'src\Yav.Adapters\Claude\ClaudeCliSession.cs'
        Find    = '            lines.AddRange(text.Select(line => "  " + line));'
        Replace = '            lines.Add("  " + text[0]);'
        Filter  = 'FullyQualifiedName~ClaudeApprovalTests'
    },
    @{
        Name    = 'a field of what a tool is given that is not a text is left out'
        File    = 'src\Yav.Adapters\Claude\ClaudeCliSession.cs'
        Find    = '                lines.Add($"{name}: {Compact(field.Value)}");
                continue;'
        Replace = '                continue;'
        Filter  = 'FullyQualifiedName~ClaudeApprovalTests'
    },
    @{
        Name    = 'what is too much to be listed is cut without a word'
        File    = 'src\Yav.Adapters\Claude\ClaudeCliSession.cs'
        Find    = '            lines.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{more} more lines of what the tool is given are not listed."));'
        Replace = '            complete = more < 0;'
        Filter  = 'FullyQualifiedName~ClaudeApprovalTests'
    },
    @{
        Name    = 'a line that is too long to be listed is listed whole'
        File    = 'src\Yav.Adapters\Claude\ClaudeCliSession.cs'
        Find    = '            if (lines[i].Length > ListedCharacters)'
        Replace = '            if (lines[i].Length > ListedCharacters * 99)'
        Filter  = 'FullyQualifiedName~ClaudeApprovalTests'
    },
    @{
        Name    = 'what could not be listed completely is allowed by one key'
        File    = 'src\Yav.Adapters\Claude\ClaudeCliSession.cs'
        Find    = 'Deliberate: request.Value.Flag("default_to_no") == true || !complete))).ConfigureAwait(false);'
        Replace = 'Deliberate: request.Value.Flag("default_to_no") == true || (!complete && complete)))).ConfigureAwait(false);'
        Filter  = 'FullyQualifiedName~ClaudeApprovalTests'
    },
    @{
        Name    = 'what the agent says about its command is shown like what the command does'
        File    = 'src\Yav.Adapters\Claude\ClaudeCliSession.cs'
        Find    = '? "description, in the words of the agent" : field.Name;'
        Replace = '? field.Name : field.Name;'
        Filter  = 'FullyQualifiedName~ClaudeApprovalTests'
    },
    @{
        Name    = 'the command of a shell is listed again among what it is given'
        File    = 'src\Yav.Adapters\Claude\ClaudeCliSession.cs'
        Find    = '            if (field.Name == shownAsCommand)'
        Replace = '            if (field.Name == "never")'
        Filter  = 'FullyQualifiedName~ClaudeApprovalTests'
    },
    @{
        Name    = 'a repair is sent to claude as if the user had written it'
        File    = 'src\Yav.Adapters\Claude\ClaudeCliSession.cs'
        Find    = '                    if (_request.Role == AgentRole.Reviewer || request.QuotesOutput)'
        Replace = '                    if (_request.Role == AgentRole.Reviewer)'
        Filter  = 'FullyQualifiedName~ClaudeSessionTests|FullyQualifiedName~ClaudeAsAModelTests'
    },
    @{
        Name    = 'a repair does not say that it quotes what nobody checked'
        File    = 'src\Yav.Coordinator\RunCoordinator.Check.cs'
        Find    = 'ImplementAsync(context, prompt, SpanKind.Repair, quotesOutput: true, stop)'
        Replace = 'ImplementAsync(context, prompt, SpanKind.Repair, quotesOutput: false, stop)'
        Filter  = 'FullyQualifiedName~ClaudeAsAModelTests|FullyQualifiedName~SafeguardTests'
    },
    @{
        Name    = 'a claude code later than the one that was tried is called tested'
        File    = 'src\Yav.Adapters\Claude\ClaudeCliAdapter.cs'
        Find    = '                    tested = TestedReleases.Contains(parsed);'
        Replace = '                    tested = parsed >= TestedReleases[0];'
        Filter  = 'FullyQualifiedName~ClaudeDetectionTests'
    },
    @{
        Name    = 'an agent is told a version that is not the one of the product'
        File    = 'src\Yav.Adapters\AdapterOptions.cs'
        Find    = 'ClientVersion ?? typeof(AdapterOptions).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";'
        Replace = 'ClientVersion ?? "0.0.0";'
        Filter  = 'FullyQualifiedName~VersionTests'
    },
    @{
        Name    = 'every program the doctor looks up is said to come from the store'
        File    = 'src\Yav.Console\Doctor\Doctor.cs'
        Find    = '.Where(program => program.Path is not null && Yav.Platform.Processes.ExecutableResolver.IsPackaged(program.Path) && !StandsIn(program.Path))'
        Replace = '.Where(program => program.Path is not null && !StandsIn(program.Path))'
        Filter  = 'FullyQualifiedName~CliCommandTests'
    },
    @{
        Name    = 'the doctor does not look for programs of the store'
        File    = 'src\Yav.Console\Doctor\Doctor.cs'
        Find    = '        if (StorePrograms(name => services.Runner.Resolve(name)) is { } store)'
        Replace = '        if (StorePrograms(_ => null) is { } store)'
        Filter  = 'FullyQualifiedName~CliCommandTests'
    },
    @{
        Name    = 'a program of a package is known only when its folder is written in one way'
        File    = 'src\Yav.Platform\Processes\ExecutableResolver.cs'
        Find    = '.Contains("WindowsApps", StringComparer.OrdinalIgnoreCase);'
        Replace = '.Contains("WindowsApps", StringComparer.Ordinal);'
        Filter  = 'FullyQualifiedName~ExecutableResolverTests'
    },
    @{
        Name    = 'a file that is named like the folder of packages is taken for a program of a package'
        File    = 'src\Yav.Platform\Processes\ExecutableResolver.cs'
        Find    = '        (Path.GetDirectoryName(path) ?? string.Empty)'
        Replace = '        path'
        Filter  = 'FullyQualifiedName~ExecutableResolverTests'
    },
    @{
        Name    = 'pasted text answers an approval'
        File    = 'src\Yav.Console\Shell\ShellInput.cs'
        Find    = 'if (_openedAt is not { } openedAt || stroke.Pasted || Decided.Task.IsCompleted)'
        Replace = 'if (_openedAt is not { } openedAt || (stroke.Pasted && stroke.Key.KeyChar == ''?'') || Decided.Task.IsCompleted)'
        Filter  = 'FullyQualifiedName~Pasted_text'
    },
    @{
        Name    = 'an answer begun before the question could be read grants access'
        File    = 'src\Yav.Console\Shell\ShellInput.cs'
        Find    = '&& owner._clock.GetElapsedTime(openedAt, _begunAt) < Settle)'
        Replace = '&& owner._clock.GetElapsedTime(openedAt, _begunAt) < TimeSpan.Zero)'
        Filter  = 'FullyQualifiedName~begun_before_the_question_could_be_read'
    },
    @{
        Name    = 'the first letter of a word answers an approval'
        File    = 'src\Yav.Console\Input\ApprovalQuestion.cs'
        Find    = 'var text = answer.Trim().ToLowerInvariant();'
        Replace = 'var text = answer.Trim().ToLowerInvariant()[..1];'
        Filter  = 'FullyQualifiedName~Words_typed_for_another_purpose'
    },
    @{
        Name    = 'control c at a question only declines'
        File    = 'src\Yav.Console\Shell\ShellInput.cs'
        Find    = 'decided = ApprovalDecision.Cancel;'
        Replace = 'decided = ApprovalDecision.Decline;'
        Filter  = 'FullyQualifiedName~Control_c_declines_and_stops'
    },
    @{
        Name    = 'an answer has no bound'
        File    = 'src\Yav.Console\Shell\ShellInput.cs'
        Find    = '&& _answer.Length < LongestAnswer)'
        Replace = '&& _answer.Length < LongestAnswer * 99)'
        Filter  = 'FullyQualifiedName~An_answer_is_not_longer'
    },
    @{
        Name    = 'allowing for the conversation is offered where it cannot be given'
        File    = 'src\Yav.Console\Input\ApprovalQuestion.cs'
        Find    = 'CanAcceptForSession = session && !deliberate;'
        Replace = 'CanAcceptForSession = !deliberate;'
        Filter  = 'FullyQualifiedName~S_then_enter'
    },
    @{
        Name    = 'what must not be allowed by one key is allowed by one key'
        File    = 'src\Yav.Console\Input\ApprovalQuestion.cs'
        Find    = 'if (Deliberate)'
        Replace = 'if (Deliberate && answer.Length > 99)'
        Filter  = 'FullyQualifiedName~by_the_word_allow|FullyQualifiedName~only_the_word_allow_allows_it'
    },
    @{
        Name    = 'keys that waited before the question answer it'
        File    = 'src\Yav.Console\Input\LiveInputLine.cs'
        Find    = 'if (await DiscardWaitingKeysAsync(cancellationToken).ConfigureAwait(false) is { } ended)'
        Replace = 'if (await Task.FromResult<InputResult?>(null).ConfigureAwait(false) is { } ended)'
        Filter  = 'FullyQualifiedName~Keys_that_waited_in_the_input'
    },
    @{
        Name    = 'the time to read a question counts from when it was drawn'
        File    = 'src\Yav.Console\Input\LiveInputLine.cs'
        Find    = 'waiting?.Opened();'
        Replace = '_ = waiting;'
        Filter  = 'FullyQualifiedName~counts_from_when_keys_reach_it'
        Extra   = @(
            @{ Find = '        if (reading)
        {
            // Keys are read now, so they reach it from this moment on.'; Replace = '        if (true)
        {
            // Keys are read now, so they reach it from this moment on.' }
        )
    },
    @{
        Name    = 'in plain mode an answer is taken for a request'
        File    = 'src\Yav.Console\Input\LineSource.cs'
        Find    = 'served = _waiting.Find(w => w.Question) ?? _waiting.FirstOrDefault();'
        Replace = 'served = _waiting.FirstOrDefault();'
        Filter  = 'FullyQualifiedName~PlainApprovalTests'
    },
    @{
        Name    = 'in plain mode two questions are open at once'
        File    = 'src\Yav.Console\Shell\ShellInput.cs'
        Find    = '        // One question at a time: an answer belongs to the question it follows.
        private readonly SemaphoreSlim _one = new(1, 1);'
        Replace = '        // One question at a time: an answer belongs to the question it follows.
        private readonly SemaphoreSlim _one = new(2, 2);'
        Filter  = 'FullyQualifiedName~Two_questions_at_once'
    },
    @{
        Name    = 'a question is asked where nobody sees it'
        File    = 'src\Yav.Console\Shell\ShellInput.cs'
        Find    = 'new(screen, reader, capabilities.Interactive);'
        Replace = 'new(screen, reader, capabilities.InputIsTerminal);'
        Filter  = 'FullyQualifiedName~Somebody_is_asked_only_where'
    },
    @{
        Name    = 'what a terminal does not show is shown as nothing'
        File    = 'src\Yav.Core\Text\TerminalSanitizer.cs'
        Find    = 'if (char.IsSurrogate(c) || IsWrittenOut(c))'
        Replace = 'if (char.IsSurrogate(c))'
        Filter  = 'FullyQualifiedName~TerminalSanitizerTests'
    },
    @{
        Name    = 'characters of no width pass into what is shown'
        File    = 'src\Yav.Core\Text\TerminalSanitizer.cs'
        Find    = 'if (IsDirectionControl(c) || IsInvisible(c))'
        Replace = 'if (IsDirectionControl(c))'
        Filter  = 'FullyQualifiedName~Zero_width_and_invisible'
    },
    @{
        Name    = 'an escape swallows the line break after it'
        File    = 'src\Yav.Core\Text\TerminalSanitizer.cs'
        Find    = 'case State.Escape when c < '' '' && c != ''\u001b'':'
        Replace = 'case State.Escape when c < '' '' && c == ''\u001b'':'
        Filter  = 'FullyQualifiedName~An_escape_that_is_followed_by_a_control'
    },
    @{
        Name    = 'a command in a question hides what control sequences carry'
        File    = 'src\Yav.Console\Input\ApprovalQuestion.cs'
        Find    = 'var command = TerminalSanitizer.Visible(request.Command, out var writtenOut);'
        Replace = 'var command = TerminalSanitizer.Clean(request.Command); var writtenOut = false;'
        Filter  = 'FullyQualifiedName~hides_a_part_of_itself'
    },
    @{
        Name    = 'a question with hidden characters is allowed by one letter'
        File    = 'src\Yav.Console\Input\ApprovalQuestion.cs'
        Find    = 'var deliberate = request.Deliberate || shown.WrittenOut || shown.NotShown() is not null;'
        Replace = 'var deliberate = request.Deliberate || shown.NotShown() is not null;'
        Filter  = 'FullyQualifiedName~hides_a_part_of_itself|FullyQualifiedName~written_out_wherever_it_is'
    },
    @{
        Name    = 'json shortens the command an approval is about'
        File    = 'src\Yav.Console\Output\JsonOutput.cs'
        Find    = 'Verbatim(writer, "command", request.Command);'
        Replace = 'Text(writer, "command", request.Command);'
        Filter  = 'FullyQualifiedName~JsonOutputTests'
    },
    @{
        Name    = 'json passes on characters a terminal acts on'
        File    = 'src\Yav.Console\Output\JsonOutput.cs'
        Find    = 'if (TerminalSanitizer.IsWrittenOut(c))'
        Replace = 'if (c == ''\0'')'
        Filter  = 'FullyQualifiedName~JsonOutputTests'
    },
    @{
        Name    = 'a command line of a run hides what control sequences carry'
        File    = 'src\Yav.Console\Rendering\RunEventFormatter.cs'
        Find    = 'new Segment(TerminalSanitizer.VisibleSingleLine(started.Command), Tone.Muted)'
        Replace = 'new Segment(started.Command, Tone.Muted)'
        Filter  = 'FullyQualifiedName~RunEventFormatterTests'
    },
    @{
        Name    = 'what a tool was given is shown in whatever length it has'
        File    = 'src\Yav.Console\Rendering\RunEventFormatter.cs'
        Find    = '        var given = TerminalSanitizer.VisibleSingleLine(tool.Given, 300);'
        Replace = '        var given = tool.Given;'
        Filter  = 'FullyQualifiedName~RunEventFormatterTests'
    },
    @{
        Name    = 'what a tool was given hides what control sequences carry'
        File    = 'src\Yav.Console\Rendering\RunEventFormatter.cs'
        Find    = '        var given = TerminalSanitizer.VisibleSingleLine(tool.Given, 300);'
        Replace = '        var given = TerminalSanitizer.CleanSingleLine(tool.Given, 300);'
        Filter  = 'FullyQualifiedName~What_a_tool_was_given_is_shown_as_it_is'
    },
    @{
        Name    = 'the record of a request hides what control sequences carry'
        File    = 'src\Yav.Coordinator\RunPublisher.cs'
        Find    = 'Quoted(e.Request.Title), Visible(e.Request.Command)),'
        Replace = 'Quoted(e.Request.Title), e.Request.Command),'
        Filter  = 'FullyQualifiedName~RecordedRequestTests'
    },
    @{
        Name    = 'yav run names what was asked for without what control sequences carry'
        File    = 'src\Yav.Console\Cli\RunCommand.cs'
        Find    = 'yield return Pair("Asked", "\"" + TerminalSanitizer.VisibleSingleLine(approval.Command ?? approval.Title, 200) + "\"");'
        Replace = 'yield return Pair("Asked", TerminalSanitizer.CleanSingleLine(approval.Command ?? approval.Title, 200));'
        Filter  = 'FullyQualifiedName~What_was_asked_for_is_named_as_it_is'
    },
    @{
        Name    = 'a command of any length is shown whole'
        File    = 'src\Yav.Console\Input\ApprovalQuestion.cs'
        Find    = 'foreach (var row in rows.Take(LinesShown))'
        Replace = 'foreach (var row in rows)'
        Filter  = 'FullyQualifiedName~two_hundred_lines'
    },
    @{
        Name    = 'what is not shown of a command is not said'
        File    = 'src\Yav.Console\Input\ApprovalQuestion.cs'
        Find    = 'if (shown.NotShown() is { } notShown)'
        Replace = 'if (shown.NotShown() is { } notShown && notShown.Length > 9999)'
        Filter  = 'FullyQualifiedName~two_hundred_lines|FullyQualifiedName~longer_than_a_line_may_be'
    },
    @{
        Name    = 'the name of a tool has no bound'
        File    = 'src\Yav.Console\Rendering\RunEventFormatter.cs'
        Find    = 'var name = TerminalSanitizer.VisibleSingleLine(tool.Tool, LongestToolName);'
        Replace = 'var name = TerminalSanitizer.VisibleSingleLine(tool.Tool);'
        Filter  = 'FullyQualifiedName~The_name_of_a_tool_is_bounded'
    },
    @{
        Name    = 'what an approval is about is cut after a few lines without a word'
        File    = 'src\Yav.Console\Input\ApprovalQuestion.cs'
        Find    = '        if (request.Details.Count > DetailsShown)'
        Replace = '        if (request.Details.Count > DetailsShown * 99)'
        Filter  = 'FullyQualifiedName~ApprovalDescriptionTests'
    },
    @{
        Name    = 'a file change that failed leaves no line'
        File    = 'src\Yav.Console\Rendering\RunEventFormatter.cs'
        Find    = 'case FilesChanged { Status: "failed" or "declined" } files:'
        Replace = 'case FilesChanged { Status: "never" } files:'
        Filter  = 'FullyQualifiedName~A_file_change_that_failed_or_was_declined'
    },
    @{
        Name    = 'a command that failed without an exit code is shown as ordinary'
        File    = 'src\Yav.Console\Rendering\RunEventFormatter.cs'
        Find    = '|| (completed.ExitCode is null && completed.Status == "failed");'
        Replace = '|| (completed.ExitCode is null && completed.Status == "never");'
        Filter  = 'FullyQualifiedName~A_command_that_failed_without_an_exit_code'
    },
    @{
        Name    = 'review show is refused while a run is active'
        File    = 'src\Yav.Console\Commands\CommandCatalog.cs'
        Find    = 'with { LookingVerbs = ["show"] }'
        Replace = 'with { LookingVerbs = [] }'
        Filter  = 'FullyQualifiedName~Review_show'
    },
    @{
        Name    = 'what a command only looks at is not told apart'
        File    = 'src\Yav.Console\Shell\Commands.cs'
        Find    = 'info = info.For(command.Arguments);'
        Replace = 'info = info.For([]);'
        Filter  = 'FullyQualifiedName~Review_show_changes_nothing'
    },
    @{
        Name    = 'where nobody can be asked the advice is to decide when asked'
        File    = 'src\Yav.Console\Shell\InteractiveShell.cs'
        Find    = 'case RunOutcomeKind.ApprovalRequired when _input.CanAsk:'
        Replace = 'case RunOutcomeKind.ApprovalRequired when !_input.CanAsk:'
        Filter  = 'FullyQualifiedName~Where_nobody_sees_the_question'
    },
    @{
        Name    = 'why a refused run ended is said twice'
        File    = 'src\Yav.Console\Shell\InteractiveShell.cs'
        Find    = 'run.SaidHowItEnded |= outcome.Reason is { } said && run.HasNoted(said);'
        Replace = 'run.SaidHowItEnded |= outcome.Reason is { } said && said.Length > 99999;'
        Filter  = 'FullyQualifiedName~says_why_once'
    },
    @{
        Name    = 'the title an agent gave its request reads as a line of yav'
        File    = 'src\Yav.Coordinator\TurnDriver.cs'
        Find    = '$"{RunPublisher.Quoted(request.Title)}: {decision} ({decidedBy})",'
        Replace = '$"{request.Title}: {decision} ({decidedBy})",'
        Filter  = 'FullyQualifiedName~RecordedRequestTests'
    },
    @{
        Name    = 'every line entered during a run leaves a continuation behind'
        File    = 'src\Yav.Console\Shell\InteractiveShell.cs'
        Find    = ': CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, active.Ended);'
        Replace = ': CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); if (active is not null) { var woken = wake; _ = active.Task.ContinueWith(_ => woken.Cancel(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default); }'
        Filter  = 'FullyQualifiedName~leave_nothing_behind_that_fails'
    },
    @{
        Name    = 'a run that ended can still be stopped'
        File    = 'src\Yav.Console\Shell\InteractiveShell.cs'
        Find    = '            _released = true;'
        Replace = '            _released = _stopRequested && !_stopRequested;'
        Filter  = 'FullyQualifiedName~Control_c_that_arrives_as_the_run_ends'
    },
    @{
        Name    = 'a route of another kind than the one agreed to is acknowledged'
        File    = 'tests\Yav.Tests\Live\LiveRunTests.cs'
        Find    = 'public string Question => $"Use ''{Kinds[(Provider, Kind)].Label}";'
        Replace = 'public string Question => ShellDriver.RouteQuestion;'
        Filter  = 'FullyQualifiedName~LiveRunPartTests.A_route_of_another_kind'
    },
    @{
        Name    = 'the part setup goes on when the route model b needs was not agreed to'
        File    = 'tests\Yav.Tests\Live\LiveRunTests.cs'
        Find    = 'var routes = new[] { modelA, modelB }.Select(ProviderOf)'
        Replace = 'var routes = new[] { modelA }.Select(ProviderOf)'
        Filter  = 'FullyQualifiedName~LiveRunPartTests.A_route_the_models_need'
    },
    @{
        Name    = 'a provider the live run does not know is taken as a route agreed to'
        File    = 'scripts\live-run.ps1'
        Find    = 'if ($provider -notin ''codex'', ''claude'') {'
        Replace = 'if ($false) {'
        Filter  = 'FullyQualifiedName~LiveRunScriptTests.A_route_the_live_run_does_not_know'
    },
    @{
        Name    = 'the script sets up a run whose models need a route that was not agreed to'
        File    = 'scripts\live-run.ps1'
        Find    = '    Assert-LiveRoutesCover -Routes $routes -ModelA $ModelA -ModelB $ModelB'
        Replace = '    $null = $routes'
        Filter  = 'FullyQualifiedName~LiveRunScriptTests.Setup_is_refused_before_anything'
    },
    @{
        Name    = 'live-run.json keeps the routes without the labels they were acknowledged with'
        File    = 'scripts\live-run.ps1'
        Find    = '[ordered]@{ provider = $_.provider; kind = $_.kind; label = $_.label; route = $_.route }'
        Replace = '[ordered]@{ provider = $_.provider; kind = $_.kind; route = $_.route }'
        Filter  = 'FullyQualifiedName~LiveRunScriptTests.The_routes_the_setup_acknowledged'
    },
    @{
        Name    = 'a run that yav ended as failed or invalid counts as gone through'
        File    = 'scripts\live-run.ps1'
        Find    = 'needs_reconciliation = 7 }'
        Replace = 'needs_reconciliation = 7; failed = 5; invalid = 64 }'
        Filter  = 'FullyQualifiedName~LiveRunScriptTests.What_follows_a_run|FullyQualifiedName~LiveRunScriptTests.A_run_that_yav_ended'
    },
    @{
        Name    = 'the script ends with 0 after a run that did not go through'
        File    = 'scripts\live-run.ps1'
        Find    = '        $exitCode = $after.ExitCode'
        Replace = '        $exitCode = 0'
        Filter  = 'FullyQualifiedName~LiveRunScriptTests.A_run_that_yav_ended'
    },
    @{
        Name    = 'the program of a run is said to be tied to the script but is not put into its job'
        File    = 'scripts\live-run.ps1'
        Find    = 'try { [YavLiveRun.ProgramJob]::Assign($job, $process.Handle); $tied = $true } catch {'
        Replace = 'try { $tied = $true } catch {'
        Filter  = 'FullyQualifiedName~LiveRunScriptTests.The_program_of_a_run_ends_with_the_script'
    },
    @{
        Name    = 'the program of a run goes on after the script stopped waiting for it'
        File    = 'scripts\live-run.ps1'
        Find    = 'if ($null -ne $process -and -not $process.HasExited) { $stop = Stop-LiveProgram -Process $process -Seconds 30 }'
        Replace = '$stop = $null'
        Filter  = 'FullyQualifiedName~LiveRunScriptTests.The_program_of_a_run_is_ended_when'
    },
    @{
        Name    = 'a run that was begun and left no summary is taken for no run'
        File    = 'scripts\live-run.ps1'
        Find    = 'foreach ($name in ''run-begun.json'', ''run-output.jsonl'') {'
        Replace = 'foreach ($name in @()) {'
        Filter  = 'FullyQualifiedName~LiveRunScriptTests.A_second_run'
    },
    @{
        Name    = 'the wait for a run is one long wait that control+c cannot stop'
        File    = 'scripts\live-run.ps1'
        Find    = 'while (-not $Process.WaitForExit($Step)) {'
        Replace = 'while (-not $Process.WaitForExit($Milliseconds)) {'
        Filter  = 'FullyQualifiedName~LiveRunScriptTests.The_wait_for_a_run'
    },
    @{
        Name    = 'the wait for a program that was told to end has no bound'
        File    = 'scripts\live-run.ps1'
        Find    = '$ended = [bool]$Process.WaitForExit($Seconds * 1000)'
        Replace = '$ended = [bool]$Process.WaitForExit(-1)'
        Filter  = 'FullyQualifiedName~LiveRunScriptTests.A_program_that_does_not_end'
    },
    @{
        Name    = 'a program that is to be stopped is only waited for, never ended'
        File    = 'scripts\live-run.ps1'
        Find    = '    try { $Process.Kill() }'
        Replace = '    try { $null = $Process.Id }'
        Filter  = 'FullyQualifiedName~LiveRunScriptTests.A_program_that'
    },
    @{
        Name    = 'the script goes on with a task file that was changed after the setup approved it'
        File    = 'scripts\live-run.ps1'
        Find    = '    if ($Approved -ne $Found) {'
        Replace = '    if ($false) {'
        Filter  = 'FullyQualifiedName~LiveRunScriptTests.A_part_after_setup_does_nothing'
    },
    @{
        Name    = 'a part in the shell takes a task file that is not the one approved at setup'
        File    = 'tests\Yav.Tests\Live\LiveRunTests.cs'
        Find    = 'if (!string.Equals(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))), approved, StringComparison.OrdinalIgnoreCase))'
        Replace = 'if (approved.Length == 0)'
        Filter  = 'FullyQualifiedName~LiveRunRulesTests.The_task_is_taken'
    },
    @{
        Name    = 'the setup does not record the hash of the task file it approved'
        File    = 'scripts\live-run.ps1'
        Find    = '        taskHash      = $TaskFile.Hash'
        Replace = '        taskHash      = $null'
        Filter  = 'FullyQualifiedName~LiveRunScriptTests.What_the_setup_approves'
    },
    @{
        Name    = 'the tests of the product include the parts of a live run'
        File    = 'scripts\test.ps1'
        Find    = '    $filters += ''(FullyQualifiedName!~Yav.Tests.Live.LiveRunTests.)'''
        Replace = '    $filters += ''(Category!=Nothing)'''
        Filter  = 'FullyQualifiedName~LiveRunScriptTests.The_tests_of_the_product'
    },
    @{
        Name    = 'a part that asks the models runs when the authorization variable says anything'
        File    = 'tests\Yav.Tests\Live\LiveRunTests.cs'
        Find    = 'return asksModels && variable(Authorized) != "1"'
        Replace = 'return asksModels && variable(Authorized) == null'
        Filter  = 'FullyQualifiedName~LiveRunRulesTests.A_part_runs_only'
    },
    @{
        Name    = 'the part that continues a run goes on without being told that the usage was authorized'
        File    = 'tests\Yav.Tests\Live\LiveRunTests.cs'
        Find    = 'if (part.Get(LiveRunFactAttribute.Authorized) != "1")'
        Replace = 'if (part.Get(LiveRunFactAttribute.Authorized) == "no")'
        Filter  = 'FullyQualifiedName~LiveRunRulesTests.The_part_that_continues_a_run_does_nothing'
    },
    @{
        Name    = 'parts that ask no model are told that the usage was authorized'
        File    = 'scripts\live-run.ps1'
        Find    = '$(if ($UsageAuthorized -and $Name -eq ''resume'') { ''1'' }'
        Replace = '$(if ($UsageAuthorized) { ''1'' }'
        Filter  = 'FullyQualifiedName~LiveRunScriptTests.Only_a_part'
    },
    @{
        Name    = 'a part after setup goes on in a directory outside the folder of the runs'
        File    = 'scripts\live-run.ps1'
        Find    = 'if (-not $full.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) {'
        Replace = 'if ($false) {'
        Filter  = 'FullyQualifiedName~LiveRunScriptTests.A_part_after_setup_goes_on_only|FullyQualifiedName~LiveRunScriptTests.A_run_is_not_made|FullyQualifiedName~LiveRunScriptTests.What_a_run_left'
    },
    @{
        Name    = 'a directory without live-run.json is taken for one the setup made'
        File    = 'scripts\live-run.ps1'
        Find    = 'if (-not (Test-Path -LiteralPath (Join-Path $full ''live-run.json''))) {'
        Replace = 'if ($false) {'
        Filter  = 'FullyQualifiedName~LiveRunScriptTests.A_part_after_setup_goes_on_only'
    },
    @{
        Name    = '/apply is typed without the number of the run'
        File    = 'tests\Yav.Tests\Live\LiveRunTests.cs'
        Find    = 'new[] { $"/apply {runId}", "/status" }'
        Replace = 'new[] { "/apply", "/status" }'
        Filter  = 'FullyQualifiedName~LiveRunRulesTests.Apply_is_typed'
    },
    @{
        Name    = 'checks that fail after /apply are judged to pass'
        File    = 'tests\Yav.Tests\Live\LiveRunTests.cs'
        Find    = ': (false, $"After /apply these checks do not pass'
        Replace = ': (true, $"After /apply these checks do not pass'
        Filter  = 'FullyQualifiedName~LiveRunRulesTests.After_apply|FullyQualifiedName~LiveRunRulesTests.With_apply'
    },
    @{
        Name    = 'a check that fails after /apply does not fail the part inspect'
        File    = 'tests\Yav.Tests\Live\LiveRunTests.cs'
        Find    = '        if (!passed)'
        Replace = '        if (passed && !passed)'
        Filter  = 'FullyQualifiedName~LiveRunRulesTests.With_apply'
    },
    @{
        Name    = 'the record of a continued run does not say what was granted'
        File    = 'tests\Yav.Tests\Live\LiveRunTests.cs'
        Find    = '        if (granted.Count > 0)'
        Replace = '        if (granted.Count > 99)'
        Filter  = 'FullyQualifiedName~LiveRunRulesTests.The_record_says'
    },
    @{
        Name    = 'the record of a continued run counts the decisions made before the part'
        File    = 'tests\Yav.Tests\Live\LiveRunTests.cs'
        Find    = 'WHERE run_id = $run AND id > $after ORDER BY id;'
        Replace = 'WHERE run_id = $run AND id > 0 ORDER BY id;'
        Filter  = 'FullyQualifiedName~LiveRunRulesTests.The_record_says'
    },
    @{
        Name    = 'the script does not require powershell 7.2'
        File    = 'scripts\live-run.ps1'
        Find    = '#Requires -Version 7.2'
        Replace = '# Requires nothing'
        Filter  = 'FullyQualifiedName~LiveRunScriptTests.The_script_says_that_it_needs|FullyQualifiedName~LiveRunScriptTests.Windows_PowerShell'
    },
    @{
        Name    = 'two parts that start at once in one directory get the same records'
        File    = 'scripts\live-run.ps1'
        Find    = '            [IO.File]::Open($log, [IO.FileMode]::CreateNew).Dispose()'
        Replace = '            $null = $log'
        Filter  = 'FullyQualifiedName~LiveRunScriptTests.Two_parts'
    },
    @{
        Name    = 'a resume that did not go through does not say that usage may have been consumed'
        File    = 'scripts\live-run.ps1'
        Find    = 'so usage may have been consumed'
        Replace = 'so nothing else was done'
        Filter  = 'FullyQualifiedName~LiveRunScriptTests.A_part_that_did_not_go_through'
    },
    @{
        Name    = 'the variables a part is given are removed afterwards instead of set back'
        File    = 'scripts\live-run.ps1'
        Find    = 'foreach ($variable in $before.Keys) { Set-LiveVariable $variable $before[$variable] }'
        Replace = 'foreach ($variable in $before.Keys) { Set-LiveVariable $variable $null }'
        Filter  = 'FullyQualifiedName~LiveRunScriptTests.The_variables_a_part'
    },
    @{
        Name    = 'a variable that is to be removed is left there empty'
        File    = 'scripts\live-run.ps1'
        Find    = 'if ($null -eq $Value) { [Environment]::SetEnvironmentVariable($Name, [NullString]::Value) }'
        Replace = 'if ($null -eq $Value) { [Environment]::SetEnvironmentVariable($Name, $null) }'
        Filter  = 'FullyQualifiedName~LiveRunScriptTests.Every_variable|FullyQualifiedName~LiveRunScriptTests.The_variables'
    },
    @{
        Name    = 'a second run is made in a directory that holds a run'
        File    = 'scripts\live-run.ps1'
        Find    = '    if ($Part -eq ''run'') { Assert-NoRunYet -Directory $Directory }'
        Replace = '    if ($Part -eq ''nothing'') { Assert-NoRunYet -Directory $Directory }'
        Filter  = 'FullyQualifiedName~LiveRunScriptTests.A_second_run_in_a_directory_is_refused_before'
    },
    @{
        Name    = 'the effort a hosting claude code session sets is not taken out'
        File    = 'scripts\live-run.ps1'
        Find    = '''CLAUDE_PID'', ''CLAUDE_EFFORT'')'
        Replace = '''CLAUDE_PID'')'
        Filter  = 'FullyQualifiedName~LiveRunScriptTests.Every_variable'
    },
    @{
        Name    = 'the variables of a hosting session are not put back when the script ends'
        File    = 'scripts\live-run.ps1'
        Find    = '    Restore-HostingSessionVariables $takenOut'
        Replace = '    $null = $takenOut'
        Filter  = 'FullyQualifiedName~LiveRunScriptTests.The_variables_of_a_hosting_session'
    },
    @{
        Name    = 'what was named again is looked up with a method the parameters of the script lack'
        File    = 'scripts\live-run.ps1'
        Find    = 'if ($namedKeys -notcontains $parameter) { continue }'
        Replace = 'if (-not $Named.Contains($parameter)) { continue }'
        Filter  = 'FullyQualifiedName~LiveRunScriptTests.What_the_script_was_given'
    },
    @{
        Name    = 'the part resume does not refuse a run that was begun and left no summary'
        File    = 'scripts\live-run.ps1'
        Find    = '    if ($Part -eq ''resume'') { Assert-LiveRunToContinue -Directory $Directory -Program $Yav }'
        Replace = '    if ($Part -eq ''nothing'') { Assert-LiveRunToContinue -Directory $Directory -Program $Yav }'
        Filter  = 'FullyQualifiedName~LiveRunScriptTests.The_program_of_a_run_ends_with_the_script'
    },
    @{
        Name    = 'the exit code expected of a blocked run is not the one yav ends it with'
        File    = 'scripts\live-run.ps1'
        Find    = 'blocked = 2;'
        Replace = 'blocked = 0;'
        Filter  = 'FullyQualifiedName~LiveRunScriptTests.The_exit_code_the_script_expects'
    },
    @{
        Name    = 'what a request is about and is not shown is allowed by one letter'
        File    = 'src\Yav.Console\Input\ApprovalQuestion.cs'
        Find    = '            var lines = LinesNotShown + DetailsNotShown;'
        Replace = '            var lines = LinesNotShown;'
        Filter  = 'FullyQualifiedName~ApprovalDescriptionTests'
        Extra   = @(
            @{ Find = 'if (LinesNotShown + DetailsNotShown > 0)'; Replace = 'if (LinesNotShown > 0)' }
        )
    },
    @{
        Name    = 'a claude code that ended while it was asked is started again and given the prompt'
        File    = 'src\Yav.Adapters\Claude\ClaudeCliSession.cs'
        Find    = '            throw new AgentProtocolException(await EndedBeforePromptAsync("before it said what it works with").ConfigureAwait(false));'
        Replace = '            return null;'
        Filter  = 'FullyQualifiedName~ClaudeAsAModelTests'
    },
    @{
        Name    = 'a claude code that ended after it was asked is started again and given the prompt'
        File    = 'src\Yav.Adapters\Claude\ClaudeCliSession.cs'
        Find    = '            if (_askedOf is { } asked)
            {
                // Asked for this turn: the prompt is for that process and no other'
        Replace = '            if (_askedOf is { } asked && asked.ProcessId < 0)
            {
                // Asked for this turn: the prompt is for that process and no other'
        Filter  = 'FullyQualifiedName~A_claude_code_that_ended_after_it_was_asked_is_not_started_again_for_the_prompt|FullyQualifiedName~ClaudeAsAModelTests'
    },
    @{
        Name    = 'a kept conversation is used again on another account route'
        File    = 'src\Yav.Coordinator\RunContext.cs'
        Find    = '        && Role.AccountRoute == role.AccountRoute
        && string.Equals(Role.AccountRouteLabel, role.AccountRouteLabel, StringComparison.Ordinal)
'
        Replace = ''
        Filter  = 'FullyQualifiedName~SessionSlotTests'
    },
    @{
        Name    = 'a continuation after a crash is marked as quoting what nobody checked'
        File    = 'src\Yav.Coordinator\RunCoordinator.Recover.cs'
        Find    = 'if (await ImplementAsync(context, prompt, kind, quotesOutput: false, stop)'
        Replace = 'if (await ImplementAsync(context, prompt, kind, quotesOutput: true, stop)'
        Filter  = 'FullyQualifiedName~SafeguardTests'
    },
    @{
        Name    = 'the stand-in windows puts where python is missing is taken for a program of the store'
        File    = 'src\Yav.Console\Doctor\Doctor.cs'
        Find    = '            || readAlias(path)?.PackageFamily.StartsWith(AppInstaller, StringComparison.OrdinalIgnoreCase) == true;'
        Replace = '            || false;'
        Filter  = 'FullyQualifiedName~CliCommandTests'
    },
    @{
        Name    = 'a directory that is reached through a junction is another directory'
        File    = 'src\Yav.Coordinator\WorkspacePaths.cs'
        Find    = 'if (parts[i].AsSpan().IndexOfAny(''*'', ''?'') >= 0 || new DirectoryInfo(resolved).LinkTarget is not { } link)'
        Replace = 'if (parts[i].Length >= 0 || new DirectoryInfo(resolved).LinkTarget is not { } link)'
        Filter  = 'FullyQualifiedName~WorkspacePathsTests'
    },
    @{
        Name    = 'a conversation that was given up is recorded as an ended process'
        File    = 'src\Yav.Coordinator\RunPublisher.cs'
        Find    = 'e.ExitCode is null ? "The agent''s conversation ended" : $"The agent process ended with exit code {e.ExitCode}"'
        Replace = 'e.ExitCode is null ? "The agent process ended" : $"The agent process ended with exit code {e.ExitCode}"'
        Filter  = 'FullyQualifiedName~SafeguardTests'
    },
    @{
        Name    = 'an answer to a request that was taken back is given to a new one of the same name'
        File    = 'src\Yav.Coordinator\TurnDriver.cs'
        Find    = '                && ReferenceEquals(open, pending)'
        Replace = '                && open is not null'
        Filter  = 'FullyQualifiedName~SafeguardTests'
    },
    @{
        Name    = 'a message of codex that yav could not handle ends the connection of every conversation'
        File    = 'src\Yav.Adapters\Protocol\JsonRpcConnection.cs'
        Find    = '                catch (Exception ex) when (!_closing.IsCancellationRequested)'
        Replace = '                catch (Exception ex) when (ex is ArgumentNullException && !_closing.IsCancellationRequested)'
        Filter  = 'FullyQualifiedName~CodexConnectionTests'
    },
    @{
        Name    = 'what waits for codex is not told that its output ended'
        File    = 'src\Yav.Adapters\Protocol\JsonRpcConnection.cs'
        Find    = '            await EndAsync(reason).ConfigureAwait(false);'
        Replace = '            await Task.CompletedTask.ConfigureAwait(false);'
        Filter  = 'FullyQualifiedName~CodexConnectionTests'
    },
    @{
        Name    = 'a request made after codex ended waits for an answer instead of failing at once'
        File    = 'src\Yav.Adapters\Protocol\JsonRpcConnection.cs'
        Find    = '            if (_ended is { } ended)'
        Replace = '            if (_ended is { } ended && ended.Length < 0)'
        Filter  = 'FullyQualifiedName~CodexConnectionTests.A_request_made_after'
    },
    @{
        Name    = 'an answer that cannot reach codex any more is not reported to the user'
        File    = 'src\Yav.Adapters\Codex\CodexAppServerSession.cs'
        Find    = '        catch (AgentProtocolException ex)
        {
            await PublishAsync(new AgentNotice(Now, $"YAV could not answer'
        Replace = '        catch (AgentProtocolException ex) when (ex.Code == 1)
        {
            await PublishAsync(new AgentNotice(Now, $"YAV could not answer'
        Filter  = 'FullyQualifiedName~An_answer_the_agent_can_no_longer_receive'
    },
    @{
        Name    = 'a conversation of which codex does not say who decides about access is used'
        File    = 'src\Yav.Adapters\Codex\CodexAppServerAdapter.cs'
        Find    = '        null => "Codex does not say who decides the requests for access in this conversation, so nobody can say that it is you. "
            + "YAV lets only you decide about access, so the conversation is not used.",'
        Replace = '        null => null,'
        Filter  = 'FullyQualifiedName~who_decides'
    },
    @{
        Name    = 'a sandbox of codex of a kind yav asks for whose reach cannot be read is accepted'
        File    = 'src\Yav.Adapters\Codex\CodexAppServerAdapter.cs'
        Find    = '        if (type is not ("readOnly" or "workspaceWrite") || ReadWidening(given, out var unreadable) is not null)'
        Replace = '        if (type is not ("readOnly" or "workspaceWrite") || ReadWidening(given, out var unreadable) is not null || type.Length > 0)'
        Filter  = 'FullyQualifiedName~cannot_be_read_is_refused|FullyQualifiedName~does_not_say_what_widens_it_is_refused|FullyQualifiedName~reports_a_sandbox_that_cannot_be_read'
    },
    @{
        Name    = 'the sandbox codex reports for a conversation it opens is not looked at'
        File    = 'src\Yav.Adapters\Codex\CodexAppServerAdapter.cs'
        Find    = '        if ((RefusalFor(effective.ApprovalsReviewer) ?? SandboxRefusal(result.Child("sandbox"))) is { } refusal)'
        Replace = '        if (RefusalFor(effective.ApprovalsReviewer) is { } refusal)'
        Filter  = 'FullyQualifiedName~names_no_sandbox_is_refused|FullyQualifiedName~cannot_be_read_is_refused'
    },
    @{
        Name    = 'a conversation for which codex names no sandbox is used'
        File    = 'src\Yav.Adapters\Codex\CodexAppServerAdapter.cs'
        Find    = '        if (policy is not { ValueKind: JsonValueKind.Object } given || given.Text("type") is not { } type)
        {
            return "Codex does not say which sandbox'
        Replace = '        if (policy is not { ValueKind: JsonValueKind.Object } given || given.Text("type") is not { } type)
        {
            return null;
        }

        if (type.Length < 0)
        {
            return "Codex does not say which sandbox'
        Filter  = 'FullyQualifiedName~names_no_sandbox_is_refused'
    },
    @{
        Name    = 'a conversation in which the own reviewer of codex takes part is only warned about and not given up'
        File    = 'src\Yav.Adapters\Codex\CodexAppServerSession.cs'
        Find    = '            case "guardianWarning":
                await RefuseAsync(AutoReviewRefusal(method, parameters)).ConfigureAwait(false);'
        Replace = '            case "guardianWarning":
                await PublishAsync(new AgentNotice(Now, AutoReviewRefusal(method, parameters), IsWarning: true)).ConfigureAwait(false);'
        Filter  = 'FullyQualifiedName~its_own_reviewer_takes_part'
    },
    @{
        Name    = 'a conversation is used after the own reviewer of codex decided for one of its sub-agents'
        File    = 'src\Yav.Adapters\Codex\CodexServer.cs'
        Find    = '                await owner.SubAgentReviewedAsync(threadId, method, parameters).ConfigureAwait(false);'
        Replace = '                _ = owner;'
        Filter  = 'FullyQualifiedName~decided_for_one_of_its_sub_agents'
    },
    @{
        Name    = 'a turn of codex is asked to stop again each time it is interrupted'
        File    = 'src\Yav.Adapters\Codex\CodexAppServerSession.cs'
        Find    = '            if (turn is not null && !ClaimInterrupt(turn))'
        Replace = '            if (turn is not null && !ClaimInterrupt(turn) && turn.Length < 0)'
        Filter  = 'FullyQualifiedName~asked_to_stop_once'
    },
    @{
        Name    = 'what codex asks in a conversation that was given up is not answered with cancel'
        File    = 'src\Yav.Adapters\Codex\CodexAppServerSession.cs'
        Find    = '        if (refused)
        {'
        Replace = '        if (refused && rawId.Length < 0)
        {'
        Filter  = 'FullyQualifiedName~asked_afterwards_is_answered_with_cancel'
    },
    @{
        Name    = 'what was left unanswered when a conversation of codex was given up is never answered'
        File    = 'src\Yav.Adapters\Codex\CodexAppServerSession.cs'
        Find    = '                await CancelAsync(pending.RawId, pending.Method).ConfigureAwait(false);'
        Replace = '                _ = pending;'
        Filter  = 'FullyQualifiedName~left_unanswered_when_a_conversation_was_given_up'
    },
    @{
        Name    = 'a command approval of a kind yav does not know can be given for the whole conversation'
        File    = 'src\Yav.Adapters\Codex\CodexAppServerSession.cs'
        Find    = '            ApprovalKind.CommandExecution => parameters.Text("kind") is not (null or "command" or "writeStdin"),'
        Replace = '            ApprovalKind.CommandExecution => false,'
        Filter  = 'FullyQualifiedName~Only_an_approval_of_a_kind_yav_knows'
    },
    @{
        Name    = 'an approval of a change yav cannot describe completely can be given for the whole conversation'
        File    = 'src\Yav.Adapters\Codex\CodexAppServerSession.cs'
        Find    = '            ApprovalKind.FileChange => parameters.Text("itemId") is { } changed
                && _announcedChanges.TryGetValue(changed, out var announced)
                && (!announced.All(IsKnownChange) || announced.Count > NamedFiles),'
        Replace = '            ApprovalKind.FileChange => false,'
        Filter  = 'FullyQualifiedName~A_change_of_a_kind_yav_does_not_know|FullyQualifiedName~An_approval_for_many_files_names_twenty'
    },
    @{
        Name    = 'a change of a kind yav does not know is described as an update'
        File    = 'src\Yav.Adapters\Codex\CodexAppServerSession.cs'
        Find    = '            var other => $"Change {change.Path} in the way Codex calls ''{other}'', which this version of YAV does not know"'
        Replace = '            var other => "Update " + change.Path + other[..0]'
        Filter  = 'FullyQualifiedName~A_change_of_a_kind_yav_does_not_know'
    },
    @{
        Name    = 'a warning of codex that names no conversation is not kept for the conversations opened later'
        File    = 'src\Yav.Adapters\Codex\CodexServer.cs'
        Find    = '            _warnings.Enqueue((number, text));'
        Replace = '            _ = number;'
        Filter  = 'FullyQualifiedName~CodexWarningTests'
    },
    @{
        Name    = 'what codex sends about a conversation right after opening it is not kept for it'
        File    = 'src\Yav.Adapters\Codex\CodexAppServerAdapter.cs'
        Find    = '                    server.Opening(named);'
        Replace = '                    _ = named;'
        Filter  = 'FullyQualifiedName~right_after_opening'
    },
    @{
        Name    = 'more than the last twenty warnings of codex are kept for the conversations opened later'
        File    = 'src\Yav.Adapters\Codex\CodexServer.cs'
        Find    = '            while (_warnings.Count > KeptWarnings)'
        Replace = '            while (_warnings.Count > KeptWarnings * 100)'
        Filter  = 'FullyQualifiedName~Only_the_last_twenty'
    },
    @{
        Name    = 'the warning of codex about folders its windows sandbox cannot protect is dropped'
        File    = 'src\Yav.Adapters\Codex\CodexServer.cs'
        Find    = '            case "windows/worldWritableWarning":'
        Replace = '            case "windows/worldWritableWarning" when parameters.ValueKind == JsonValueKind.Null:'
        Filter  = 'FullyQualifiedName~windows_sandbox_cannot_protect'
    },
    @{
        Name    = 'what codex asks for a conversation that is refused while it is opened is never answered'
        File    = 'src\Yav.Adapters\Codex\CodexServer.cs'
        Find    = '            _opening.Remove(threadId, out kept);'
        Replace = '            _opening.Remove(threadId, out kept);
            kept = null;'
        Filter  = 'FullyQualifiedName~refused_while_it_is_opened_is_still_answered'
    },
    @{
        Name    = 'a reading of the limits of codex overwrites an update that followed it'
        File    = 'src\Yav.Adapters\Codex\CodexAppServerAdapter.cs'
        Find    = '            RateLimitSnapshot? own = null;
            await connection.RequestAsync('
        Replace = '            RateLimitSnapshot? own = null;
            var reading = await connection.RequestAsync('
        Filter  = 'FullyQualifiedName~follows_a_reading_of_the_limits'
        Extra   = @(
            @{ Find = '                onAnswer: result => own = _rateLimits.Replace(result, "account/rateLimits/read", _clock.GetUtcNow())).ConfigureAwait(false);
            return own;'; Replace = '                onAnswer: null).ConfigureAwait(false);
            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
            return own ?? _rateLimits.Replace(reading, "account/rateLimits/read", _clock.GetUtcNow());' }
        )
    },
    @{
        Name    = 'what is done with an answer of codex is done after the messages that follow it'
        File    = 'src\Yav.Adapters\Protocol\JsonRpcConnection.cs'
        Find    = '                        pending.OnAnswer?.Invoke(result);
                        pending.Waiter.TrySetResult(result);'
        Replace = '                        pending.Waiter.TrySetResult(result);
                        _ = Task.Run(async () => { await Task.Delay(200); pending.OnAnswer?.Invoke(result); });'
        Filter  = 'FullyQualifiedName~CodexConnectionTests.What_is_done_with_an_answer'
    },
    @{
        Name    = 'a credit balance an update of the limits no longer gives is kept from before'
        File    = 'src\Yav.Adapters\Codex\CodexRateLimits.cs'
        Find    = '                    CreditBalance = credits ? update.CreditBalance : known.CreditBalance,'
        Replace = '                    CreditBalance = update.CreditBalance ?? known.CreditBalance,'
        Filter  = 'FullyQualifiedName~Credits_are_taken_whole'
    },
    @{
        Name    = 'developer instructions of codex that could not be read are kept as none and not read again'
        File    = 'src\Yav.Adapters\Codex\CodexAppServerAdapter.cs'
        Find    = '                own = null;
                problem = $"The developer instructions'
        Replace = '                own = null;
                server.DeveloperInstructions[directory] = own;
                problem = $"The developer instructions'
        Filter  = 'FullyQualifiedName~configuration_cannot_be_read'
    },
    @{
        Name    = 'a conversation is not told that the developer instructions of codex could not be read'
        File    = 'src\Yav.Adapters\Codex\CodexAppServerAdapter.cs'
        Find    = '        foreach (var said in new[] { problem, accountProblem })'
        Replace = '        foreach (var said in new[] { accountProblem })'
        Filter  = 'FullyQualifiedName~configuration_cannot_be_read'
    },
    @{
        Name    = 'a conversation of codex does not say which account it works with'
        File    = 'src\Yav.Adapters\Codex\CodexAppServerAdapter.cs'
        Find    = '        effective = effective with { Account = account };'
        Replace = '        _ = account;'
        Filter  = 'FullyQualifiedName~which_account_it_works_with|FullyQualifiedName~not_signed_in_says_so'
    },
    @{
        Name    = 'a change of the account of codex is not reported by its conversations'
        File    = 'src\Yav.Adapters\Codex\CodexServer.cs'
        Find    = '            case "account/updated":'
        Replace = '            case "account/updated" when parameters.ValueKind == JsonValueKind.Null:'
        Filter  = 'FullyQualifiedName~its_account_changed'
    },
    @{
        Name    = 'any failure to probe a conversation of codex is taken for a conversation codex does not know'
        File    = 'src\Yav.Adapters\Codex\CodexAppServerAdapter.cs'
        Find    = '        catch (AgentProtocolException ex) when (ex.Code == InvalidRequest)'
        Replace = '        catch (AgentProtocolException ex)'
        Filter  = 'FullyQualifiedName~reported_as_the_failure_it_is'
    },
    @{
        Name    = 'the user is not told that a question of an mcp server was declined'
        File    = 'src\Yav.Adapters\Codex\CodexAppServerSession.cs'
        Find    = '                await PublishAsync(new AgentNotice(Now, ElicitationDeclined(parameters), IsWarning: true)).ConfigureAwait(false);'
        Replace = '                _ = ElicitationDeclined(parameters);'
        Filter  = 'FullyQualifiedName~A_question_of_an_mcp_server'
    },
    @{
        Name    = 'the user is not told that a request of a sub-agent of codex was refused'
        File    = 'src\Yav.Adapters\Codex\CodexServer.cs'
        Find    = '            await owner.SubAgentAskedAsync(threadId!, method).ConfigureAwait(false);'
        Replace = '            _ = method;'
        Filter  = 'FullyQualifiedName~A_sub_agent_that_asks_for_approval'
    },
    @{
        Name    = 'the user is not told which request of codex yav does not handle'
        File    = 'src\Yav.Adapters\Codex\CodexAppServerSession.cs'
        Find    = '$"Codex asked for something this version of YAV does not handle (''{method}''), so it was refused; the turn goes on without it.",'
        Replace = '"Codex asked for something this version of YAV does not handle, so it was refused; the turn goes on without it.",'
        Filter  = 'FullyQualifiedName~does_not_handle_is_refused_and_the_user_is_told'
    },
    @{
        Name    = 'a conversation of codex is resumed with its whole history'
        File    = 'src\Yav.Adapters\Codex\CodexAppServerAdapter.cs'
        Find    = '                    writer.WriteBoolean("excludeTurns", true);'
        Replace = '                    writer.WriteBoolean("excludeTurns", false);'
        Filter  = 'FullyQualifiedName~Resuming_asks_for_the_thread_without_its_history'
    },
    @{
        Name    = 'a resumed conversation of codex is sent the name of the client as if it were new'
        File    = 'src\Yav.Adapters\Codex\CodexAppServerAdapter.cs'
        Find    = '                else
                {
                    // Only a new thread takes the name of the client that started it.
                    writer.WriteString("serviceName", ClientName);
                }'
        Replace = '                writer.WriteString("serviceName", ClientName);'
        Filter  = 'FullyQualifiedName~Resuming_asks_for_the_thread_without_its_history'
    },
    @{
        Name    = 'a turn of codex recorded as in progress in a conversation nothing runs is reported as running'
        File    = 'src\Yav.Adapters\Codex\CodexAppServerAdapter.cs'
        Find    = '                        SessionProbeState.LastTurnInterrupted, "The last turn is recorded as in progress'
        Replace = '                        SessionProbeState.Active, "The last turn is recorded as in progress'
        Filter  = 'FullyQualifiedName~nothing_runs_is_not_reported_as_running'
    },
    @{
        Name    = 'permissions the user grants for the conversation are granted to codex beyond the turn'
        File    = 'src\Yav.Adapters\Codex\CodexAppServerSession.cs'
        Find    = '                    writer.WriteString("scope", "turn");'
        Replace = '                    writer.WriteString("scope", decision == ApprovalDecision.AcceptForSession ? "session" : "turn");'
        Filter  = 'FullyQualifiedName~Permissions_the_user_grants'
    },
    @{
        Name    = 'a conversation codex closed does not end'
        File    = 'src\Yav.Adapters\Codex\CodexAppServerSession.cs'
        Find    = '            case "thread/closed":'
        Replace = '            case "thread/closed" when parameters.ValueKind == JsonValueKind.Null:'
        Filter  = 'FullyQualifiedName~A_conversation_codex_closed_ends'
    },
    @{
        Name    = 'a request of codex yav does not handle is refused on the connection of another process'
        File    = 'src\Yav.Adapters\Codex\CodexServer.cs'
        Find    = '        await Connection.RespondErrorAsync(rawId, -32601, $"YAV Shell does not handle ''{method}''.", CancellationToken.None).ConfigureAwait(false);'
        Replace = '        await s_newest!.Connection.RespondErrorAsync(rawId, -32601, $"YAV Shell does not handle ''{method}''.", CancellationToken.None).ConfigureAwait(false);'
        Filter  = 'FullyQualifiedName~Giving_up_a_process_ends_only_its_own'
        Extra   = @(
            @{ Find = '    public CodexServer(CodexAppServerAdapter adapter) => _adapter = adapter;'; Replace = '    private static CodexServer? s_newest;

    public CodexServer(CodexAppServerAdapter adapter)
    {
        _adapter = adapter;
        s_newest = this;
    }' }
        )
    },
    @{
        Name    = 'the end of one codex process ends the conversations of another'
        File    = 'src\Yav.Adapters\Codex\CodexServer.cs'
        Find    = '            sessions = [.. _sessions.Values];
            _sessions.Clear();'
        Replace = '            sessions = [.. _sessions.Values, .. (s_newest is { } newest && newest != this ? newest.Sessions() : [])];
            _sessions.Clear();'
        Filter  = 'FullyQualifiedName~Giving_up_a_process_ends_only_its_own'
        Extra   = @(
            @{ Find = '    public CodexServer(CodexAppServerAdapter adapter) => _adapter = adapter;'; Replace = '    private static CodexServer? s_newest;

    public CodexServer(CodexAppServerAdapter adapter)
    {
        _adapter = adapter;
        s_newest = this;
    }' }
        )
    },
    @{
        Name    = 'codex exec is said to work in the directory yav asked for, which it never reported'
        File    = 'src\Yav.Adapters\Codex\CodexExecAdapter.cs'
        Find    = '            null, null, null, null, null, null, null, null, [], [],'
        Replace = '            null, null, null, null, null, request.WorkingDirectory, null, null, [], [],'
        Filter  = 'FullyQualifiedName~CodexExecAdapterTests'
    },
    @{
        Name    = 'an approval for more files than are named can be given for the whole conversation'
        File    = 'src\Yav.Adapters\Codex\CodexAppServerSession.cs'
        Find    = '                && (!announced.All(IsKnownChange) || announced.Count > NamedFiles),'
        Replace = '                && !announced.All(IsKnownChange),'
        Filter  = 'FullyQualifiedName~An_approval_for_many_files_names_twenty'
    },
    @{
        Name    = 'a declined codex approval is sent as accepted'
        File    = 'src\Yav.Adapters\Codex\CodexAppServerSession.cs'
        Find    = '            ApprovalDecision.Cancel => "cancel",
            _ => "decline",'
        Replace = '            ApprovalDecision.Cancel => "cancel",
            _ => "accept",'
        Filter  = 'FullyQualifiedName~CodexApprovalAndControlTests'
    },
    @{
        Name    = 'the codex reviewer is asked to the user instead of being declined'
        File    = 'src\Yav.Adapters\Codex\CodexAppServerSession.cs'
        Find    = '        if (_request.Approvals == ApprovalMode.NeverAsk)
        {
            // The read-only reviewer never gets more access, whatever is asked.'
        Replace = '        if (_request.Approvals == ApprovalMode.NeverAsk && rawId.Length > 100000)
        {
            // The read-only reviewer never gets more access, whatever is asked.'
        Filter  = 'FullyQualifiedName~CodexApprovalAndControlTests'
    },
    @{
        Name    = 'the codex reviewer session is opened writable'
        File    = 'src\Yav.Adapters\Codex\CodexAppServerAdapter.cs'
        Find    = 'writer.WriteString("sandbox", request.Sandbox == SandboxLevel.ReadOnly ? "read-only" : "workspace-write");'
        Replace = 'writer.WriteString("sandbox", "workspace-write");'
        Filter  = 'FullyQualifiedName~CodexSessionTests'
    },
    @{
        Name    = 'codex cached tokens are counted on top of input tokens'
        File    = 'src\Yav.Adapters\Codex\CodexAppServerSession.cs'
        Find    = '            ? TokenCounts.FromInclusiveCounters(
                b.Number("inputTokens"), b.Number("cachedInputTokens"), b.Number("outputTokens"), b.Number("reasoningOutputTokens"),
                b.Number("cacheWriteInputTokens") is > 0 ? b.Number("cacheWriteInputTokens") : null)'
        Replace = '            ? TokenCounts.FromExclusiveCounters(
                b.Number("inputTokens"), b.Number("cachedInputTokens"), null, b.Number("outputTokens"), b.Number("reasoningOutputTokens"))'
        Filter  = 'FullyQualifiedName~CodexTurnTests'
    },
    @{
        Name    = 'the prompt is passed on the command line in exec mode'
        File    = 'src\Yav.Adapters\Codex\CodexExecAdapter.cs'
        Find    = '            // "-" makes Codex read the prompt from standard input.
            arguments.Add("-");'
        Replace = '            arguments.Add(prompt.Replace("\r", " ").Replace("\n", " "));'
        Filter  = 'FullyQualifiedName~CodexExecAdapterTests'
    },
    @{
        Name    = 'a frame that is too large is buffered without bound'
        File    = 'src\Yav.Adapters\Protocol\JsonLineReader.cs'
        Find    = '            if (_end - _start > _maxFrameBytes)
            {
                throw new FrameTooLargeException(_maxFrameBytes);
            }'
        Replace = '            if (_end - _start > int.MaxValue - 10)
            {
                throw new FrameTooLargeException(_maxFrameBytes);
            }'
        Filter  = 'FullyQualifiedName~JsonLineReaderTests'
        Extra   = @(
            @{ Find = 'var size = Math.Min((long)_buffer.Length * 2, (long)_maxFrameBytes + 4096);'; Replace = 'var size = (long)_buffer.Length * 2;' }
        )
    },
    @{
        Name    = 'a candidate with unresolved issues is offered for application'
        File    = 'src\Yav.Coordinator\RunCoordinator.Check.cs'
        Find    = '            var decision = check.Decision!;
            if (decision.Accepted)'
        Replace = '            var decision = check.Decision!;
            if (decision.Accepted || decision.Issues.Count < 1000)'
        Filter  = 'FullyQualifiedName~RepairLoopTests'
    },
    @{
        Name    = 'what the reviewer wrote becomes part of the candidate'
        File    = 'src\Yav.Coordinator\RunCoordinator.Check.cs'
        Find    = '            if (!ChecksRunInCandidate(context))
            {
                // Nothing but the reviewer could have written here, and the reviewer is not a writer.'
        Replace = '            if (ChecksRunInCandidate(context))
            {
                // Nothing but the reviewer could have written here, and the reviewer is not a writer.'
        Filter  = 'FullyQualifiedName~ReviewerBoundaryTests'
        Extra   = @(
            @{ Find = '        if (!string.Equals(current, candidate.Fingerprint, StringComparison.Ordinal)
            && await RestoreCandidateAsync(context, candidate, "Source changed while the candidate was being checked", stop).ConfigureAwait(false))'; Replace = '        if (current.Length == 0
            && await RestoreCandidateAsync(context, candidate, "Source changed while the candidate was being checked", stop).ConfigureAwait(false))' }
        )
    },
    @{
        Name    = 'a result obtained while the source changed is kept'
        File    = 'src\Yav.Coordinator\RunCoordinator.Check.cs'
        Find    = '            if (inCandidate && !stop.IsCancellationRequested'
        Replace = '            if (inCandidate && stop.IsCancellationRequested'
        Filter  = 'FullyQualifiedName~ChecksAndSourceTests'
    },
    @{
        Name    = 'the repair loop has no limit'
        File    = 'src\Yav.Coordinator\RunCoordinator.Check.cs'
        Find    = '            if (context.RepairCyclesUsed >= maxCycles)'
        Replace = '            if (context.RepairCyclesUsed >= maxCycles + 1000)'
        Filter  = 'FullyQualifiedName~RepairLoopTests'
    },
    @{
        Name    = 'a run goes on although the provider did not honor its settings'
        File    = 'src\Yav.Coordinator\TurnDriver.cs'
        Find    = '        if (verification.Violations.Count > 0)
        {
            state.Violation ??='
        Replace = '        if (verification.Violations.Count > 1000)
        {
            state.Violation ??='
        Filter  = 'FullyQualifiedName~ProfileEnforcementTests'
    },
    @{
        Name    = 'a model the provider swapped in is accepted'
        File    = 'src\Yav.Coordinator\TurnDriver.cs'
        Find    = '            case ModelRerouted rerouted when context.Profile.Policy.QualityLock:'
        Replace = '            case ModelRerouted rerouted when !context.Profile.Policy.QualityLock:'
        Filter  = 'FullyQualifiedName~ProfileEnforcementTests'
    },
    @{
        Name    = 'a reviewer request for more access is shown to the user'
        File    = 'src\Yav.Coordinator\TurnDriver.cs'
        Find    = '        if (role == AgentRole.Reviewer || slot.Role.Approvals == ApprovalMode.NeverAsk)'
        Replace = '        if (role == AgentRole.Reviewer && request.ApprovalId.Length > 100000)'
        Filter  = 'FullyQualifiedName~SafeguardTests'
    },
    @{
        Name    = 'a run nobody can answer grants the approval'
        File    = 'src\Yav.Coordinator\TurnDriver.cs'
        Find    = '            Answer(context, slot, request, ApprovalDecision.Cancel, "policy: non-interactive run", now, state);'
        Replace = '            Answer(context, slot, request, ApprovalDecision.Accept, "policy: non-interactive run", now, state);'
        Filter  = 'FullyQualifiedName~SafeguardTests|FullyQualifiedName~ApprovalTests'
    },
    @{
        Name    = 'an answer to a withdrawn request is sent to the agent'
        File    = 'src\Yav.Coordinator\TurnDriver.cs'
        Find    = '                && state.Pending.Remove(pending.Request.ApprovalId);'
        Replace = '                && state.Pending.Remove(pending.Request.ApprovalId) || pending.Request.ApprovalId.Length > 0;'
        Filter  = 'FullyQualifiedName~SafeguardTests'
    },
    @{
        Name    = 'an agent that died in a turn is treated as a failed turn'
        File    = 'src\Yav.Coordinator\TurnDriver.cs'
        Find    = '            case TurnCompleted { ErrorCode: TurnErrorCodes.AgentExited } exited:'
        Replace = '            case TurnCompleted { ErrorCode: "never" } exited:'
        Filter  = 'FullyQualifiedName~StopAndLimitTests'
    },
    @{
        Name    = 'the limits of a run are ignored'
        File    = 'src\Yav.Coordinator\RunCoordinator.cs'
        Find    = '            if (elapsed >= elapsedLimit)'
        Replace = '            if (elapsed >= elapsedLimit + TimeSpan.FromDays(999))'
        Filter  = 'FullyQualifiedName~StopAndLimitTests'
        Extra   = @(
            @{ Find = '            if (known && used >= tokenLimit)'; Replace = '            if (known && used >= tokenLimit + (long.MaxValue / 2))' },
            @{ Find = 'is { } most && most.UsedPercent >= percent)'; Replace = 'is { } most && most.UsedPercent >= percent + 1000)' }
        )
    },
    @{
        Name    = 'a second writer starts in a project that has an active run'
        File    = 'src\Yav.Coordinator\RunCoordinator.cs'
        Find    = '        if (!_activeByProject.TryAdd(project, runId))'
        Replace = '        if (!_activeByProject.TryAdd(project, runId) && runId.Length > 1000)'
        Filter  = 'FullyQualifiedName~FollowUpTests'
    },
    @{
        Name    = 'a follow-up is charged with the earlier turns of its conversation'
        File    = 'src\Yav.Coordinator\RunCoordinator.cs'
        Find    = 'new SessionUsageTracker(continues ? stored!.CumulativeTokens : null, continues ? stored!.CumulativeCostUsd : null, sessionIsNew: !continues));'
        Replace = 'new SessionUsageTracker(null, null, sessionIsNew: true));'
        Filter  = 'FullyQualifiedName~UsageRecordingTests'
    },
    @{
        Name    = 'an apply trusts the evidence of the moment the run became ready'
        File    = 'src\Yav.Coordinator\RunCoordinator.Deliver.cs'
        Find    = '        if (!decision.Accepted)
        {
            var reason = "The evidence no longer covers this candidate'
        Replace = '        if (!decision.Accepted && decision.Issues.Count > 1000)
        {
            var reason = "The evidence no longer covers this candidate'
        Filter  = 'FullyQualifiedName~DeliveryTests'
    },
    @{
        Name    = 'undo reverses an older apply although a newer one did not finish'
        File    = 'src\Yav.Coordinator\RunCoordinator.Deliver.cs'
        Find    = '            if (candidate.State is JournalState.Prepared or JournalState.InProgress or JournalState.Interrupted)'
        Replace = '            if (candidate.State is JournalState.Prepared && candidate.RunId.Length > 1000)'
        Filter  = 'FullyQualifiedName~SafeguardTests'
    },
    @{
        Name    = 'a path outside the workspace is offered as a reference'
        File    = 'src\Yav.Coordinator\StartingReferences.cs'
        Find    = '            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))'
        Replace = '            if (full.Length == 0)'
        Filter  = 'FullyQualifiedName~StartingReferenceTests'
    },
    @{
        Name    = 'a reviewer without a confirmed boundary is accepted'
        File    = 'src\Yav.Coordinator\SettingsVerifier.cs'
        Find    = '            if (reported is not null && Same(reported, ReadOnly))'
        Replace = '            if (reported is null || Same(reported, ReadOnly))'
        Filter  = 'FullyQualifiedName~SettingsVerifierTests'
    },
    @{
        Name    = 'a paid tier that was not requested is accepted'
        File    = 'src\Yav.Coordinator\SettingsVerifier.cs'
        Find    = '            if (reportedStandard)'
        Replace = '            if (reportedStandard || reported is not null)'
        Filter  = 'FullyQualifiedName~SettingsVerifierTests'
    },
    @{
        Name    = 'a changed billing route is accepted'
        File    = 'src\Yav.Coordinator\SettingsVerifier.cs'
        Find    = '            if (expectsKey is { } expected && expected != reportsKey)'
        Replace = '            if (expectsKey is { } expected && expected != reportsKey && reported.Length == 0)'
        Filter  = 'FullyQualifiedName~SettingsVerifierTests'
    },
    @{
        Name    = 'reference data can end its own quotation'
        File    = 'src\Yav.Core\Templates\RuntimeTemplates.cs'
        Find    = 'builder.AppendLine(DelimiterLookalike().Replace(content.TrimEnd(), "&lt;$1"));'
        Replace = 'builder.AppendLine(DelimiterLookalike().Replace(content.TrimEnd(), "<$1"));'
        Filter  = 'FullyQualifiedName~PromptBuilderTests'
    },
    @{
        Name    = 'a failure that was already there is sent for repair without being asked'
        File    = 'src\Yav.Core\Runs\AcceptanceGate.cs'
        Find    = '                GateStatus.Failed when !preExisting || repairPreExisting => IssueResolution.RepairByImplementer,'
        Replace = '                GateStatus.Failed => IssueResolution.RepairByImplementer,'
        Filter  = 'FullyQualifiedName~AcceptanceGateTests'
    },
    @{
        Name    = 'model a is accepted without its settings being confirmed'
        File    = 'src\Yav.Core\Runs\AcceptanceGate.cs'
        Find    = '            if (role == AgentRole.Implementer && !input.ImplementerInvolved)'
        Replace = '            if (role == AgentRole.Implementer)'
        Filter  = 'FullyQualifiedName~AcceptanceGateTests'
    },
    @{
        Name    = 'an effort the application cannot rank is chosen for the user'
        File    = 'src\Yav.Core\Profiles\ProfileResolver.cs'
        Find    = '            return (string.Empty, VerificationStatus.RequestedUnverified);'
        Replace = '            return (supported[^1], VerificationStatus.RequestedUnverified);'
        Filter  = 'FullyQualifiedName~ProfileResolverTests'
    },
    @{
        Name    = 'an agent that cannot report its settings passes a strict preflight'
        File    = 'src\Yav.Core\Profiles\ProfileResolver.cs'
        Find    = '        if (!adapter.Capabilities.Has(AdapterFeatures.EffortReadback))'
        Replace = '        if (!adapter.Capabilities.Has(AdapterFeatures.EffortReadback) && name.Length > 1000)'
        Filter  = 'FullyQualifiedName~ProfileResolverTests'
    },
    @{
        Name    = 'a turn that will not stop keeps its process'
        File    = 'src\Yav.Adapters\Codex\CodexAppServerSession.cs'
        Find    = '        await _adapter.AbandonAsync(_connection).ConfigureAwait(false);'
        Replace = '        await Task.CompletedTask.ConfigureAwait(false);'
        Filter  = 'FullyQualifiedName~CodexApprovalAndControlTests'
    },
    @{
        Name    = 'the faster tier of the current catalog is not recognized'
        File    = 'src\Yav.Adapters\Codex\CodexAppServerAdapter.cs'
        Find    = 'internal static bool IsFasterTier(string? tierId) => tierId is "priority" or "fast";'
        Replace = 'internal static bool IsFasterTier(string? tierId) => tierId is "fast";'
        Filter  = 'FullyQualifiedName~CodexDetectionTests'
    },
    @{
        Name    = 'files that do not belong to the candidate stay after it was put back'
        File    = 'src\Yav.Workspace\WorkspaceService.Freeze.cs'
        Find    = '            if (hash is null)
            {
                if (File.Exists(target))
                {
                    DeleteFile(target);
                }
            }'
        Replace = '            if (hash is null)
            {
                if (File.Exists(target) && target.Length > 100000)
                {
                    DeleteFile(target);
                }
            }'
        Filter  = 'FullyQualifiedName~RestoreCandidateTests'
    },
    @{
        Name    = 'a run in the project itself is rewritten to match a candidate'
        File    = 'src\Yav.Workspace\WorkspaceService.Freeze.cs'
        Find    = '        if (workspace.Mode == WorkspaceMode.InPlace)
        {
            const string Reason = "This run works directly in the project.'
        Replace = '        if (workspace.Mode == WorkspaceMode.InPlace && workspace.WorkspaceId.Length > 1000)
        {
            const string Reason = "This run works directly in the project.'
        Filter  = 'FullyQualifiedName~RestoreCandidateTests'
    },
    @{
        Name    = 'accepting the gaps of a project covers gaps that appear later'
        File    = 'src\Yav.Storage\YavDatabase.Trust.cs'
        Find    = '        IsAcknowledged(GapsKind, ProjectKey(projectPath) + "|" + gapsFingerprint);'
        Replace = '        IsAcknowledged(GapsKind, ProjectKey(projectPath));'
        Filter  = 'FullyQualifiedName~TrustStoreTests'
        Extra   = @(
            @{ Find = '        Acknowledge(GapsKind, ProjectKey(projectPath) + "|" + gapsFingerprint, statement);'; Replace = '        Acknowledge(GapsKind, ProjectKey(projectPath), statement);' }
        )
    },
    @{
        Name    = 'a continued row begins in the first column'
        File    = 'src\Yav.Console\Rendering\Line.cs'
        Find    = 'rows.Add(Row(cells, start, end, first ? null : Hang));'
        Replace = 'rows.Add(Row(cells, start, end, null));'
        Filter  = 'FullyQualifiedName~ScreenTests'
    },
    @{
        Name    = 'quoted text loses its mark where it continues'
        File    = 'src\Yav.Console\Rendering\Line.cs'
        Find    = 'return (quotes ? first : new Segment(new string('' '', width)), 1);'
        Replace = 'return (new Segment(new string('' '', width)), 1);'
        Filter  = 'FullyQualifiedName~ScreenTests'
    },
    @{
        Name    = 'a line is written without being cleaned'
        File    = 'src\Yav.Console\Rendering\Line.cs'
        Find    = 'private static string Clean(string text) => TerminalSanitizer.CleanLine(text);'
        Replace = 'private static string Clean(string text) => text.Length > 100000 ? TerminalSanitizer.CleanLine(text) : text;'
        Filter  = 'FullyQualifiedName~ScreenTests|FullyQualifiedName~RunEventFormatterTests'
    },
    @{
        Name    = 'a line break that was pasted sends the input'
        File    = 'src\Yav.Console\Input\LiveInputLine.cs'
        Find    = '            if (stroke.Pasted)
            {
                // Pasted text is text.'
        Replace = '            if (stroke.Pasted && key.Key != ConsoleKey.Enter)
            {
                // Pasted text is text.'
        Filter  = 'FullyQualifiedName~LiveInputTests'
    },
    @{
        Name    = 'a command that is not known is run by guess'
        File    = 'src\Yav.Console\Shell\Commands.cs'
        Find    = 'var info = CommandCatalog.Find(command.Name);'
        Replace = 'var info = CommandCatalog.Find(command.Name) ?? CommandCatalog.Find(CommandCatalog.Suggest(command.Name).FirstOrDefault() ?? string.Empty);'
        Filter  = 'FullyQualifiedName~An_unknown_command_does_nothing'
    },
    @{
        Name    = 'a command changes a run that is active'
        File    = 'src\Yav.Console\Shell\Commands.cs'
        Find    = 'if (_session.Active is not null && !info.AllowedDuringRun)'
        Replace = 'if (_session.Active is not null && !info.AllowedDuringRun && info.Name.Length > 1000)'
        Filter  = 'FullyQualifiedName~A_command_that_would_change_the_active_run'
    },
    @{
        Name    = 'the implementer is not told which checks yav runs itself'
        File    = 'src\Yav.Core\Templates\RuntimeTemplates.cs'
        Find    = 'var required = checks?.Where(c => c.Required).ToList() ?? [];'
        Replace = 'var required = new List<GateDefinition>();'
        Filter  = 'FullyQualifiedName~PromptBuilderTests'
    },
    @{
        Name    = 'checks that are not required are promised to the implementer as well'
        File    = 'src\Yav.Core\Templates\RuntimeTemplates.cs'
        Find    = 'var required = checks?.Where(c => c.Required).ToList() ?? [];'
        Replace = 'var required = checks?.ToList() ?? [];'
        Filter  = 'FullyQualifiedName~PromptBuilderTests'
    },
    @{
        Name    = 'the task is sent without the checks yav runs itself'
        File    = 'src\Yav.Coordinator\RunCoordinator.cs'
        Find    = 'PromptBuilder.ImplementationRequest(context.Requirements, references, context.Project.RequiredGates.ToList())'
        Replace = 'PromptBuilder.ImplementationRequest(context.Requirements, references)'
        Filter  = 'FullyQualifiedName~The_implementer_is_told_in_its_task_and_in_a_repair'
    },
    @{
        Name    = 'a repair is sent without the checks yav runs itself'
        File    = 'src\Yav.Coordinator\RunCoordinator.Check.cs'
        Find    = 'maxCycles, context.Project.RequiredGates.ToList());'
        Replace = 'maxCycles);'
        Filter  = 'FullyQualifiedName~The_implementer_is_told_in_its_task_and_in_a_repair'
    },
    @{
        Name    = 'a continuation is sent without the checks yav runs itself'
        File    = 'src\Yav.Coordinator\RunCoordinator.Recover.cs'
        Find    = 'PromptBuilder.ContinuationRequest(context.Requirements, context.Project.RequiredGates.ToList());'
        Replace = 'PromptBuilder.ContinuationRequest(context.Requirements);'
        Filter  = 'FullyQualifiedName~A_turn_that_did_not_finish_is_continued_in_its_own_conversation'
    },
    @{
        Name    = 'a codex of a release series that was not tried is called tested'
        File    = 'src\Yav.Adapters\Codex\CodexAppServerAdapter.cs'
        Find    = 'version.StartsWith(series[..^1], StringComparison.Ordinal)'
        Replace = 'version.StartsWith(series[..2], StringComparison.Ordinal)'
        Filter  = 'FullyQualifiedName~CodexDetectionTests'
    },
    @{
        Name    = 'codex exec calls a version tested that the app server does not'
        File    = 'src\Yav.Adapters\Codex\CodexExecAdapter.cs'
        Find    = 'CodexAppServerAdapter.IsTestedVersion(version),'
        Replace = 'version is not null,'
        Filter  = 'FullyQualifiedName~A_version_is_called_tested_as_it_is_for_the_app_server'
    },
    @{
        Name    = 'a turn that is ended with its process is reported as the ending agent says'
        File    = 'src\Yav.Adapters\Codex\CodexAppServerSession.cs'
        Find    = '_abandoned = true;'
        Replace = '_abandoned = false;'
        Filter  = 'FullyQualifiedName~A_turn_that_ignores_the_interrupt_is_ended_with_the_agents_own_process'
    },
    @{
        Name    = 'a shell is given a console that is not there'
        File    = 'src\Yav.Console\Shell\Commands.Local.cs'
        Find    = 'if (!_input.CanAsk || _host is not { Capabilities.Interactive: true })'
        Replace = 'if (!_input.CanAsk)'
        Filter  = 'FullyQualifiedName~Shell_starts_nothing_where_there_is_no_console_to_give_to_it'
    },
    @{
        Name    = 'what is typed during a run is added to the running turn'
        File    = 'src\Yav.Console\Shell\InteractiveShell.cs'
        Find    = '            Enqueue(text, isFollowUp: true);
            return;
        }

        if (_services.Settings.Adaptive'
        Replace = '            await Coordinator.SteerAsync(_session.Active.RunId ?? string.Empty, text, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (_services.Settings.Adaptive'
        Filter  = 'FullyQualifiedName~What_is_typed_during_a_run_waits'
    },
    @{
        Name    = 'the prompt waits for the agents'
        File    = 'src\Yav.Console\Shell\InteractiveShell.cs'
        Find    = '_ = Task.Run(WarmUpAgentsAsync, cancellationToken);'
        Replace = 'await WarmUpAgentsAsync().ConfigureAwait(false);'
        Filter  = 'FullyQualifiedName~The_prompt_does_not_wait_for_the_agents'
    },
    @{
        Name    = 'input from a pipe does not wait for the run'
        File    = 'src\Yav.Console\Shell\InteractiveShell.cs'
        Find    = 'if (active is not null && !_input.CanAsk)'
        Replace = 'if (active is not null && !_input.CanAsk && active.Request.Length > 100000)'
        Filter  = 'FullyQualifiedName~ExecutableTests.The_shell_reads_lines_from_a_pipe|FullyQualifiedName~ExecutableTests.The_end_of_piped_input'
    },
    @{
        Name    = 'the faster tier is turned on without a yes'
        File    = 'src\Yav.Console\Shell\Commands.Models.cs'
        Find    = 'if (!await ConfirmAsync("Use the faster tier for the roles above, with the billing the provider describes?", cancellationToken).ConfigureAwait(false))'
        Replace = 'if (offers.Count > 1000)'
        Filter  = 'FullyQualifiedName~Paid_speed'
    },
    @{
        Name    = 'the effort of a task is lowered without asking'
        File    = 'src\Yav.Console\Shell\InteractiveShell.cs'
        Find    = 'var answer = await _input.AskAsync($"Effort for this task, or Enter for {current}: ", cancellationToken).ConfigureAwait(false);'
        Replace = 'string? answer = lower[0]; await Task.Yield();'
        Filter  = 'FullyQualifiedName~In_adaptive_mode_a_task_runs_at_maximum_effort|FullyQualifiedName~With_adaptive_mode_off'
    },
    @{
        Name    = 'a lower effort is used although adaptive mode is off'
        File    = 'src\Yav.Core\Profiles\ProfileResolver.cs'
        Find    = '            if (request.Policy.Adaptive)
            {
                implementerSelection ='
        Replace = '            if (request.Policy.Adaptive || approved.Length > 0)
            {
                implementerSelection ='
        Filter  = 'FullyQualifiedName~ProfileResolverTests|FullyQualifiedName~AdaptiveModeTests'
    },
    @{
        Name    = 'in adaptive mode the review is lowered as well'
        File    = 'src\Yav.Core\Profiles\ProfileResolver.cs'
        Find    = 'var reviewerSelection = request.Reviewer ?? request.Implementer;'
        Replace = 'var reviewerSelection = request.Reviewer is null ? implementerSelection : request.Reviewer with { EffortPreference = implementerSelection?.EffortPreference ?? request.Reviewer.EffortPreference };'
        Filter  = 'FullyQualifiedName~ProfileResolverTests|FullyQualifiedName~AdaptiveModeTests'
    },
    @{
        Name    = 'a secret is shared between data directories'
        File    = 'src\Yav.Platform\Security\WindowsCredentialStore.cs'
        Find    = '_prefix = scope is null ? Prefix : Prefix + Checked(scope, "scope") + "/";'
        Replace = '_prefix = scope is null ? Prefix : Prefix + Checked(scope, "scope")[..0];'
        Filter  = 'FullyQualifiedName~CredentialStoreTests'
    },
    @{
        Name    = 'json carries the control sequences of an agent'
        File    = 'src\Yav.Console\Output\JsonOutput.cs'
        Find    = '            writer.WriteString(name, TerminalSanitizer.Clean(value));'
        Replace = '            writer.WriteString(name, value);'
        Filter  = 'FullyQualifiedName~JsonOutputTests|FullyQualifiedName~CliCommandTests'
    },
    @{
        Name    = 'a run without a prompt grants what an agent asks for'
        File    = 'src\Yav.Console\Cli\RunCommand.cs'
        Find    = 'public bool CanAsk => false;'
        Replace = 'public bool CanAsk => true;'
        Filter  = 'FullyQualifiedName~A_request_for_approval_that_nobody_can_answer'
        Extra   = @(
            @{ Find = 'Task.FromResult(ApprovalDecision.Cancel);'; Replace = 'Task.FromResult(ApprovalDecision.Accept);' }
        )
    },
    @{
        Name    = 'in a window the terminal breaks the rows of a run without a prompt'
        File    = 'src\Yav.Console\Cli\RunCommand.cs'
        Find    = '                screen.WriteLines(formatter.Format(runEvent));'
        Replace = '                foreach (var line in formatter.Format(runEvent)) { screen.WriteBlock(line.PlainText.Length == 0 ? " " : line.PlainText); }'
        Filter  = 'FullyQualifiedName~In_a_window_what_an_agent_wrote'
    },
    @{
        Name    = 'a file that exists is replaced by an export'
        File    = 'src\Yav.Console\Shell\Commands.Run.cs'
        Find    = 'if (File.Exists(target) || Directory.Exists(target))'
        Replace = 'if (Directory.Exists(target))'
        Filter  = 'FullyQualifiedName~Diff_is_written_to_a_file|FullyQualifiedName~A_run_is_exported_to_a_file'
    },
    @{
        Name    = 'a path with a blank is cut off at the blank'
        File    = 'src\Yav.Console\Shell\Commands.Run.cs'
        Find    = 'return at < 0 ? string.Empty : raw[(at + word.Length)..].Trim().Trim(''"'');'
        Replace = 'return at < 0 ? string.Empty : raw[(at + word.Length)..].Trim().Split('' '')[0].Trim(''"'');'
        Filter  = 'FullyQualifiedName~Diff_is_written_to_a_file|FullyQualifiedName~A_run_is_exported_to_a_file'
    },
    @{
        Name    = 'a file of the workspace is shown with the path it has there'
        File    = 'src\Yav.Coordinator\WorkspacePaths.cs'
        Find    = '            ? full[root.Length..].Replace(''\\'', ''/'')'
        Replace = '            ? full'
        Filter  = 'FullyQualifiedName~A_file_the_agent_changed_is_named'
    },
    @{
        Name    = 'a file outside the workspace is shown like a file of the project'
        File    = 'src\Yav.Coordinator\WorkspacePaths.cs'
        Find    = '            : full;'
        Replace = '            : Path.GetFileName(full);'
        Filter  = 'FullyQualifiedName~A_file_the_agent_changed_is_named'
    },
    @{
        Name    = 'what was added to a turn is not kept as a requirement'
        File    = 'src\Yav.Coordinator\RunCoordinator.cs'
        Find    = '            _services.Store.SaveRequirement(context.TaskId, requirement);
            _services.Store.UpdateRun(runId, run => run with { AcceptanceVersion = requirement.Version });'
        Replace = '            _services.Store.UpdateRun(runId, run => run with { AcceptanceVersion = requirement.Version });'
        Filter  = 'FullyQualifiedName~SteeringTests'
    },
    @{
        Name    = 'a candidate is checked against fewer requirements than the agent was given'
        File    = 'src\Yav.Coordinator\RunContext.cs'
        Find    = '            _requirements.Add(requirement);
            return requirement;'
        Replace = '            return requirement;'
        Filter  = 'FullyQualifiedName~SteeringTests'
    },
    @{
        Name    = 'what the agent refused is a requirement all the same'
        File    = 'src\Yav.Coordinator\RunCoordinator.cs'
        Find    = '            if (!await slot.Session.SteerAsync(text, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }'
        Replace = '            _ = await slot.Session.SteerAsync(text, cancellationToken).ConfigureAwait(false);'
        Filter  = 'FullyQualifiedName~SteeringTests'
    },
    @{
        Name    = 'the part a completion replaces leaves out the caret'
        File    = 'src\Yav.Console\Input\InputCompletion.cs'
        Find    = 'public static CompletionResult NothingAt(int caret) => new(caret, 0, []);'
        Replace = 'public static CompletionResult NothingAt(int caret) => new(caret - caret, 0, []);'
        Filter  = 'FullyQualifiedName~InputCompletionTests'
    },
    @{
        Name    = 'an editor that fails takes the shell with it'
        File    = 'src\Yav.Console\Shell\ShellInput.cs'
        Find    = 'catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException or NotSupportedException)'
        Replace = 'catch (Exception ex) when (ex is NotSupportedException)'
        Filter  = 'FullyQualifiedName~An_editor_that_fails'
    },
    @{
        Name    = 'enter takes a completion instead of sending'
        File    = 'src\Yav.Console\Input\IdlePrompt.cs'
        Find    = 'commitCompletion: new KeyPressPatterns(new KeyPressPattern(ConsoleKey.Tab)),'
        Replace = 'commitCompletion: new KeyPressPatterns(new KeyPressPattern(ConsoleKey.Tab), new KeyPressPattern(ConsoleKey.Enter)),'
        Filter  = 'FullyQualifiedName~Commands_are_completed_and_a_command_that_was_typed_in_full|FullyQualifiedName~A_request_is_typed_checked_and_applied'
    },
    @{
        Name    = 'control c becomes a signal once a line was sent'
        File    = 'src\Yav.Console\Input\IdlePrompt.cs'
        Find    = '            get => true;
            set
            {
            }'
        Replace = '            get => System.Console.TreatControlCAsInput;
            set => System.Console.TreatControlCAsInput = value;'
        Filter  = 'FullyQualifiedName~Control_c_during_a_run_stops_the_run_and_not_yav'
    },
    @{
        Name    = 'a line feed sends the input'
        File    = 'src\Yav.Console\Input\IdlePrompt.cs'
        Find    = '                    new KeyPressPattern(ConsoleModifiers.Control, ConsoleKey.J),
                    new KeyPressPattern(ConsoleModifiers.Control, ConsoleKey.Enter))),'
        Replace = '                    new KeyPressPattern(ConsoleModifiers.Control, ConsoleKey.J))),'
        Filter  = 'FullyQualifiedName~A_new_line_is_started_without_sending'
        Extra   = @(
            @{ Find = 'submitPrompt: new KeyPressPatterns(new KeyPressPattern(ConsoleKey.Enter)),'; Replace = 'submitPrompt: new KeyPressPatterns(new KeyPressPattern(ConsoleKey.Enter), new KeyPressPattern(ConsoleModifiers.Control, ConsoleKey.Enter)),' }
        )
    },
    @{
        Name    = 'the completion list is colored although no color was asked for'
        File    = 'src\Yav.Console\Input\IdlePrompt.cs'
        Find    = '                : new FormattedString(">"),'
        Replace = '                : new FormattedString(">", new FormatSpan(0, 1, AnsiColor.Cyan)),'
        Filter  = 'FullyQualifiedName~Without_color_nothing_is_colored'
    },
    @{
        Name    = 'what a failed run consumed is left out of the cost of a successful task'
        File    = 'bench\Yav.Bench\BenchStatistics.cs'
        Find    = 'decimal? cost = results.Count > 0 && withoutCost == 0 ? results.Sum(r => r.CostUsd!.Value) : null;'
        Replace = 'decimal? cost = results.Count > 0 && withoutCost == 0 ? results.Where(r => r.Succeeded).Sum(r => r.CostUsd!.Value) : null;'
        Filter  = 'FullyQualifiedName~BenchStatisticsTests'
    },
    @{
        Name    = 'usage that was not reported counts as nothing'
        File    = 'bench\Yav.Bench\BenchStatistics.cs'
        Find    = 'long? tokens = results.Count > 0 && withoutUsage == 0 ? results.Sum(r => r.TotalTokens!.Value) : null;'
        Replace = 'long? tokens = results.Sum(r => r.TotalTokens ?? 0);'
        Filter  = 'FullyQualifiedName~BenchStatisticsTests'
    },
    @{
        Name    = 'times are compared although one of the two did not succeed'
        File    = 'bench\Yav.Bench\BenchStatistics.cs'
        Find    = '            .Where(r => r.Succeeded && others.ContainsKey((r.TaskId, r.Step, r.Repetition)))'
        Replace = '            .Where(r => others.ContainsKey((r.TaskId, r.Step, r.Repetition)))'
        Filter  = 'FullyQualifiedName~BenchStatisticsTests'
    },
    @{
        Name    = 'a benchmark run is judged by what the run says and not by the checks'
        File    = 'bench\Yav.Bench\Arms.cs'
        Find    = '        return (failed.Count == 0, failed, regressions);'
        Replace = '        return (true, failed, regressions);'
        Filter  = 'FullyQualifiedName~A_task_that_was_not_done_is_reported_as_not_succeeded'
    },
    @{
        Name    = 'the benchmark looks for a program where a run works'
        File    = 'bench\Yav.Bench\Program.cs'
        Find    = 'options[name] = Path.GetFullPath(given);'
        Replace = 'options[name] = given;'
        Filter  = 'FullyQualifiedName~The_tool_takes_the_paths'
    },
    @{
        Name    = 'an event of an agent says who before it says what'
        File    = 'src\Yav.Console\Output\JsonOutput.cs'
        Find    = "            writer.WriteString(`"event`", name);`n            writer.WriteString(`"role`", Snake(role.ToString()));"
        Replace = "            writer.WriteString(`"role`", Snake(role.ToString()));`n            writer.WriteString(`"event`", name);"
        Filter  = 'FullyQualifiedName~JsonOutputTests'
    },
    @{
        Name    = 'the line break a prompt file ends with is part of the request'
        File    = 'src\Yav.Console\Cli\RunCommand.cs'
        Find    = ".TrimEnd('\r', '\n');"
        Replace = ';'
        Filter  = 'FullyQualifiedName~CliCommandTests'
    },
    @{
        Name    = 'a package that is not the one its checksum was written for is accepted'
        File    = 'scripts\package-tools.ps1'
        Find    = "if (`$actual -ne `$parts.Groups['hash'].Value.ToLowerInvariant()) {"
        Replace = 'if ($false) {'
        Filter  = 'FullyQualifiedName~BuildScriptTests'
    },
    @{
        Name    = 'the hash of a file needs a command that is not always there'
        File    = 'scripts\package-tools.ps1'
        Find    = "    [System.BitConverter]::ToString(`$hash).Replace('-', '').ToLowerInvariant()"
        Replace = '    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()'
        Filter  = 'FullyQualifiedName~BuildScriptTests'
    },
    @{
        Name    = 'the checksum of another file is taken for the checksum of the package'
        File    = 'scripts\package-tools.ps1'
        Find    = "if (`$parts.Groups['name'].Value -ne `$name) {"
        Replace = 'if ($false) {'
        Filter  = 'FullyQualifiedName~BuildScriptTests'
    },
    @{
        Name    = 'the checksum is written in a form sha256sum does not read'
        File    = 'scripts\package-tools.ps1'
        Find    = '"$hash  $([System.IO.Path]::GetFileName($full))`n"'
        Replace = '"$hash $([System.IO.Path]::GetFileName($full))`r`n"'
        Filter  = 'FullyQualifiedName~BuildScriptTests'
    },
    @{
        Name    = 'the build scripts take the version of the assembly for the version of the product'
        File    = 'scripts\package-tools.ps1'
        Find    = 'ForEach-Object { [string]$_.Version })'
        Replace = 'ForEach-Object { [string]$_.AssemblyVersion })'
        Filter  = 'FullyQualifiedName~BuildScriptTests'
    },
    @{
        Name    = 'a test that was not run is counted as failed in the table of the classes'
        File    = 'scripts\summarize-tests.ps1'
        Find    = 'default { $classes[$name].NotRun++ }'
        Replace = 'default { $classes[$name].Failed++ }'
        Filter  = 'FullyQualifiedName~The_summary_of_the_tests_is_written'
    },
    @{
        Name    = 'the package is published as a program with its libraries beside it'
        File    = 'scripts\package.ps1'
        Find    = "    '-p:PublishSingleFile=true',"
        Replace = "    '-p:PublishSingleFile=false',"
        Filter  = 'FullyQualifiedName~BuildScriptTests'
    },
    @{
        Name    = 'the native library of the package is left beside the program'
        File    = 'scripts\package.ps1'
        Find    = "    '-p:IncludeNativeLibrariesForSelfExtract=true',"
        Replace = "    '-p:IncludeNativeLibrariesForSelfExtract=false',"
        Filter  = 'FullyQualifiedName~BuildScriptTests'
    },
    @{
        Name    = 'a text is replaced although it occurs more often than was said'
        File    = 'src\Yav.Console\Shell\Commands.Run.cs'
        Find    = 'var edit = new MechanicalEditRequest(file, text, replacement, expected, AllOccurrences: expected != 1);'
        Replace = 'var edit = new MechanicalEditRequest(file, text, replacement, null, AllOccurrences: true);'
        Filter  = 'FullyQualifiedName~ShellRunCommandTests.Replace_'
    },
    @{
        Name    = 'a replacement is started for a file outside the project'
        File    = 'src\Yav.Console\Shell\Commands.Run.cs'
        Find    = 'if (!IsInside(project, file))'
        Replace = 'if (file.Length == 0)'
        Filter  = 'FullyQualifiedName~ShellRunCommandTests.Replace_'
    },
    @{
        Name    = 'what follows the replacement is taken for text'
        File    = 'src\Yav.Console\Shell\Commands.Run.cs'
        Find    = "                default:`n                    _ui.Warn(usage);`n                    return;"
        Replace = "                default:`n                    break;"
        Filter  = 'FullyQualifiedName~ShellRunCommandTests.Replace_'
    },
    @{
        Name    = 'a local edit takes its path from the root of the repository'
        File    = 'src\Yav.Coordinator\RunCoordinator.cs'
        Find    = 'var located = WorkspacePaths.Locate(edit.Path, workspace);'
        Replace = 'string? located = edit.Path;'
        Filter  = 'FullyQualifiedName~MechanicalEditTests'
    },
    @{
        Name    = 'a local edit leaves the directory that was opened'
        File    = 'src\Yav.Coordinator\WorkspacePaths.cs'
        Find    = "        return full.StartsWith(opened, StringComparison.OrdinalIgnoreCase)`n            ? Path.GetRelativePath(workspace.RootPath, full)`n            : null;"
        Replace = "        return Path.GetRelativePath(workspace.RootPath, full);"
        Filter  = 'FullyQualifiedName~MechanicalEditTests'
    },
    @{
        Name    = 'what was measured before a run was recorded is lost'
        File    = 'src\Yav.Coordinator\RunCoordinator.cs'
        Find    = '                SaveSpan(context, earlier);'
        Replace = '                _ = earlier;'
        Filter  = 'FullyQualifiedName~ChecksAndSourceTests'
    },
    @{
        Name    = 'the time a candidate waited counts as time in which something was done'
        File    = 'src\Yav.Coordinator\RunCoordinator.Deliver.cs'
        Find    = '        SaveWaiting(run, "until /apply");'
        Replace = '        _ = run;'
        Filter  = 'FullyQualifiedName~ChecksAndSourceTests'
    },
    @{
        Name    = 'time that no stage accounts for is hidden'
        File    = 'src\Yav.Core\Timing\Timing.cs'
        Find    = '            BetweenStages = between,'
        Replace = '            BetweenStages = between is null ? null : TimeSpan.Zero,'
        Filter  = 'FullyQualifiedName~Yav.Tests.Core.TimingTests'
    },
    @{
        Name    = 'a run begins when it was recorded and not when it began'
        File    = 'src\Yav.Core\Timing\Timing.cs'
        Find    = '            runStarted = first;'
        Replace = '            _ = first;'
        Filter  = 'FullyQualifiedName~Yav.Tests.Core.TimingTests'
    },
    @{
        Name    = 'the questions about a repository are asked one after the other'
        File    = 'src\Yav.Workspace\WorkspaceService.Inspect.cs'
        Find    = '        var askingHead = _git.ResolveHeadAsync(root, cancellationToken);'
        Replace = '        var askingHead = Task.FromResult(await _git.ResolveHeadAsync(root, cancellationToken).ConfigureAwait(false));'
        Filter  = 'FullyQualifiedName~InspectionTests'
    },
    @{
        Name    = 'the shell takes a screen of its own and leaves the scrollback behind'
        File    = 'src\Yav.Console\Shell\InteractiveShell.cs'
        Find    = "        var settings = _services.Settings;`n        _ui.Lines(`n        [`n            Line.Of(`"YAV Shell`", Tone.Accent, bold: true),"
        Replace = "        var settings = _services.Settings;`n        _screen.Terminal.Write(`"\u001b[?1049h`");`n        _ui.Lines(`n        [`n            Line.Of(`"YAV Shell`", Tone.Accent, bold: true),"
        Filter  = 'FullyQualifiedName~What_was_written_stays_in_the_console'
    },
    @{
        Name    = 'the keys that are explained are not the keys that work'
        File    = 'src\Yav.Console\Input\IdlePrompt.cs'
        Find    = '            persistentHistoryFilepath: historyFile,'
        Replace = '            persistentHistoryFilepath: null,'
        Filter  = 'FullyQualifiedName~Earlier_input_comes_back'
    },
    @{
        Name    = 'the time an agent spends in its tools is not measured'
        File    = 'src\Yav.Coordinator\TurnDriver.cs'
        Find    = '                BeginTool(context, role, started.ItemId, "command", started.At, state);'
        Replace = '                _ = started;'
        Filter  = 'FullyQualifiedName~ChecksAndSourceTests|FullyQualifiedName~Yav.Tests.Coordinator.ToolTime'
    },
    @{
        Name    = 'what a command was is kept with the measurement of it'
        File    = 'src\Yav.Coordinator\TurnDriver.cs'
        Find    = '                BeginTool(context, role, started.ItemId, "command", started.At, state);'
        Replace = '                BeginTool(context, role, started.ItemId, started.Command, started.At, state);'
        Filter  = 'FullyQualifiedName~ChecksAndSourceTests|FullyQualifiedName~Yav.Tests.Coordinator.ToolTime|FullyQualifiedName~ShellRecordCommandTests.Latency_tells'
    },
    @{
        Name    = 'a tool that had not ended with its turn is not measured at all'
        File    = 'src\Yav.Coordinator\TurnDriver.cs'
        Find    = "            foreach (var tool in state.Tools.Values)`n            {`n                tool.Dispose();`n            }"
        Replace = "            _ = state.Tools.Count;"
        Filter  = 'FullyQualifiedName~ChecksAndSourceTests'
    },
    @{
        Name    = 'the time in tools is left out of what is shown'
        File    = 'src\Yav.Core\Timing\Timing.cs'
        Find    = '            InTools = WallClock(spans.Where(s => s.Kind == SpanKind.ToolActivity)),'
        Replace = '            InTools = TimeSpan.Zero,'
        Filter  = 'FullyQualifiedName~ChecksAndSourceTests|FullyQualifiedName~Yav.Tests.Coordinator.ToolTime'
    },
    @{
        Name    = 'long paths are left to what the repository says about them'
        File    = 'src\Yav.Workspace\GitClient.cs'
        Find    = '        "-c", "core.longpaths=true",'
        Replace = '        "-c", "core.longpaths=false",'
        Filter  = 'FullyQualifiedName~LongPathTests'
    },
    @{
        Name    = 'the tables switch ANSI on for a pipe where GITHUB_ACTIONS is set'
        File    = 'src\Yav.Console\Rendering\Ui.cs'
        Find    = "            Enrichment = new ProfileEnrichment { UseDefaultEnrichers = false },`n            Out = new AnsiConsoleOutput(writer),"
        Replace = "            Out = new AnsiConsoleOutput(writer),"
        Filter  = 'FullyQualifiedName~ExecutableTests.Tables_reach_a_pipe_as_plain_text'
    },
    @{
        Name    = 'the doctor names Windows Server after its build'
        File    = 'src\Yav.Console\Doctor\Doctor.cs'
        Find    = '        if (server)'
        Replace = '        if (server && build < Windows11Build)'
        Filter  = 'FullyQualifiedName~CliCommandTests.Doctor_names_windows_server'
    }
)

$project = Join-Path $RepoRoot 'tests\Yav.Tests\Yav.Tests.csproj'
$results = Join-Path $RepoRoot 'artifacts\test-results\mutation-check'
$trx = Join-Path $results 'mutation.trx'
$survived = @()
$killed = 0

function Get-MutationEdits {
    param([hashtable]$Mutation)
    $edits = @(@{ Find = $Mutation.Find; Replace = $Mutation.Replace })
    if ($Mutation.ContainsKey('Extra')) { $edits += $Mutation.Extra }
    return $edits
}

function Get-HangReport {
    <#
        Null when the time limit did not end the tests; otherwise the names of the tests that were running then,
        as far as the test platform names them. Its blame collector records in the results that the limit was
        reached. On the console, at the verbosity used here, the end looks like a crash of the test host.
    #>
    param([string]$Results, [string]$Output)

    if (-not (Test-Path -LiteralPath $Results)) { return $null }
    [xml]$xml = Get-Content -LiteralPath $Results -Raw
    $reached = @($xml.GetElementsByTagName('RunInfo') | Where-Object { $_.InnerText -match 'inactivity time of \d+ seconds has elapsed' })
    if ($reached.Count -eq 0) { return $null }

    $names = @()
    if ($Output -match '(?s)when the crash occurred:\s*(.*?)(\r?\n\s*\r?\n|$)') {
        $names = @(($Matches[1] -split "`r?`n") | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    }

    if ($names.Count -eq 0) { return 'a test' }
    return $names -join ', '
}

$testArguments = @('--results-directory', $results, '--logger', 'trx;LogFileName=mutation.trx')
if ($HangSeconds -gt 0) { $testArguments += @('--blame-hang-timeout', "${HangSeconds}s", '--blame-hang-dump-type', 'none') }

# Every mutation is looked at before the first one is made: a text that is not in its file any more, or is
# there more than once, would end the run somewhere in the middle, hours after it began.
$stale = @()
$seen = @{}
foreach ($mutation in $mutations) {
    if ($seen.ContainsKey($mutation.Name)) { $stale += "'$($mutation.Name)' is there twice" }
    $seen[$mutation.Name] = $true
    $path = Join-Path $RepoRoot $mutation.File
    if (-not (Test-Path -LiteralPath $path)) {
        $stale += "'$($mutation.Name)': the file is not there: $($mutation.File)"
        continue
    }

    $source = [System.IO.File]::ReadAllText($path) -replace "`r`n", "`n"
    foreach ($edit in (Get-MutationEdits $mutation)) {
        $find = $edit.Find -replace "`r`n", "`n"
        $count = ([regex]::Matches($source, [regex]::Escape($find))).Count
        if ($count -ne 1) { $stale += "'$($mutation.Name)': its text is $count time(s) in $($mutation.File): $find" }
        if ($find -eq ($edit.Replace -replace "`r`n", "`n")) { $stale += "'$($mutation.Name)': what it writes is what is there" }
    }
}

if ($stale.Count -gt 0) {
    Write-Host ("{0} mutation(s) cannot be made. Nothing was run." -f $stale.Count) -ForegroundColor Red
    $stale | ForEach-Object { Write-Host "  $_" }
    exit 2
}

$selected = @($mutations | Select-Object -Skip $Skip)
if ($Take -gt 0) { $selected = @($selected | Select-Object -First $Take) }
if ($Only) { $selected = @($selected | Where-Object { $_.Name -like "*$Only*" }) }
Write-Host ("{0} of {1} mutation(s)." -f $selected.Count, $mutations.Count)

foreach ($mutation in $selected) {
    $path = Join-Path $RepoRoot $mutation.File
    $original = [System.IO.File]::ReadAllText($path)
    $mutated = $original -replace "`r`n", "`n"
    foreach ($edit in (Get-MutationEdits $mutation)) {
        $find = $edit.Find -replace "`r`n", "`n"
        if (-not $mutated.Contains($find)) { throw "Mutation '$($mutation.Name)': fragment not found in $($mutation.File): $find" }
        $mutated = $mutated.Replace($find, ($edit.Replace -replace "`r`n", "`n"))
    }

    try {
        [System.IO.File]::WriteAllText($path, $mutated)
        if (Test-Path -LiteralPath $trx) { Remove-Item -LiteralPath $trx -Force }
        $output = & $DotNet test $project -c Debug --nologo -v quiet --filter $mutation.Filter @testArguments 2>&1 | Out-String
        $failed = $LASTEXITCODE -ne 0

        # Whatever kept the code from being built: an error of the compiler, of an analyzer or of the build.
        # Tests that were never run noticed nothing. Where tests were run, what looks like such an error is
        # something a test wrote. Tests that the time limit ended were run.
        $hung = if ($failed) { Get-HangReport -Results $trx -Output $output } else { $null }
        $ran = $null -ne $hung -or $output -match '(Passed|Failed)!\s+-\s+Failed:'
        $notBuilt = -not $ran -and ($output -match '(?m)\berror\s+[A-Z]+\d+\b' -or $output -match 'Build FAILED')
        $count = if ($output -match 'Failed!\s+-\s+Failed:\s+(\d+)') { [int]$Matches[1] } else { 0 }
        if ($notBuilt) {
            Write-Host ("INVALID   {0}  (the mutation does not compile)" -f $mutation.Name) -ForegroundColor Yellow
            $survived += "$($mutation.Name) [did not compile]"
        }
        elseif ($failed -and $null -ne $hung) {
            # A test that does not end did not pass: the mutation is noticed, by the time limit and not by an assertion.
            $failures = if ($count -gt 0) { "{0} test(s) failed, and " -f $count } else { '' }
            Write-Host ("KILLED    {0}  ({1}killed by the time limit: no test began or ended for {2} s; still running: {3})" -f $mutation.Name, $failures, $HangSeconds, $hung) -ForegroundColor Green
            $killed++
        }
        elseif ($failed -and $count -gt 0) {
            Write-Host ("KILLED    {0}  ({1} test(s) failed)" -f $mutation.Name, $count) -ForegroundColor Green
            $killed++
        }
        elseif ($failed) {
            # It ended with an error and no test is said to have failed: nobody knows what the tests would have said.
            Write-Host ("UNCLEAR   {0}  (the tests ended with an error, and none of them is reported as failed)" -f $mutation.Name) -ForegroundColor Yellow
            $survived += "$($mutation.Name) [the tests did not say]"
        }
        else {
            Write-Host ("SURVIVED  {0}" -f $mutation.Name) -ForegroundColor Red
            $survived += $mutation.Name
        }
    }
    finally {
        [System.IO.File]::WriteAllText($path, $original)
    }
}

Write-Host ''
Write-Host ("{0} mutation(s) killed, {1} survived." -f $killed, $survived.Count)
if ($survived.Count -gt 0) {
    $survived | ForEach-Object { Write-Host "  not protected: $_" }
    exit 1
}

exit 0
