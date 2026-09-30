using Yav.Core.Agents;
using Yav.Core.Ports;

namespace Yav.Coordinator;

/// <summary>
/// Names files the way the user knows them. An agent works in the isolated workspace and reports paths
/// inside it; the user knows the same files by their place in the project.
/// </summary>
internal static class WorkspacePaths
{
    /// <summary>The event as it is shown and stored. What the coordinator decides on is the event as it arrived.</summary>
    public static AgentEvent ForDisplay(AgentEvent agentEvent, string? workspaceRoot)
    {
        if (workspaceRoot is not null && agentEvent is ToolActivity { Path: { Length: > 0 } where } tool)
        {
            return tool with { Path = Pointed(where, workspaceRoot) };
        }

        if (workspaceRoot is null || agentEvent is not FilesChanged files)
        {
            return agentEvent;
        }

        return files with
        {
            Changes = files.Changes
                .Select(change => change with
                {
                    Path = InProject(change.Path, workspaceRoot),
                    MovedTo = change.MovedTo is null ? null : InProject(change.MovedTo, workspaceRoot),
                })
                .ToList(),
        };
    }

    /// <summary>
    /// Where in the workspace a path is that the user wrote. It starts at the directory the user opened,
    /// which need not be the root of what the workspace holds. Null when the path is not below that directory.
    /// </summary>
    public static string? Locate(string path, IsolatedWorkspace workspace)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.Contains(':'))
        {
            return null;
        }

        string full;
        string opened;
        try
        {
            opened = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace.AgentDirectory)) + Path.DirectorySeparatorChar;
            full = Path.GetFullPath(Path.Combine(opened, path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        return full.StartsWith(opened, StringComparison.OrdinalIgnoreCase)
            ? Path.GetRelativePath(workspace.RootPath, full)
            : null;
    }

    /// <summary>How many links are followed for one path. More than that is a loop, or not a directory YAV made.</summary>
    private const int LinksFollowed = 32;

    /// <summary>
    /// True when both name the same directory. A directory has more than one name: in upper and lower case,
    /// with a separator at its end, by the short names Windows keeps for names that are long or contain a
    /// blank, and through junctions and symbolic links. Claude Code reports the directory it works in with
    /// every junction resolved; Codex reports it as it was given. A drive made with subst is not resolved.
    /// </summary>
    public static bool SameDirectory(string first, string second)
    {
        try
        {
            return string.Equals(Resolved(first), Resolved(second), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException)
        {
            // What cannot be looked at is not said to be the same.
            return false;
        }
    }

    /// <summary>
    /// The full path with the long names Windows keeps, and with every junction and symbolic link on the way
    /// resolved. Parts that are not there are kept as they are written.
    /// </summary>
    private static string Resolved(string path)
    {
        var pending = Plain(path);
        for (var followed = 0; ; followed++)
        {
            var root = Path.GetPathRoot(pending);
            if (string.IsNullOrEmpty(root))
            {
                return pending;
            }

            var resolved = root;
            string? target = null;
            var parts = pending[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < parts.Length; i++)
            {
                resolved = Path.Combine(resolved, parts[i]);

                // A name with a wildcard names no directory; it is not looked up.
                if (parts[i].AsSpan().IndexOfAny('*', '?') >= 0 || new DirectoryInfo(resolved).LinkTarget is not { } link)
                {
                    continue;
                }

                // A target that is written relative to the link is relative to the directory that holds it.
                target = Path.Combine([Path.GetDirectoryName(resolved) ?? root, WithoutPrefix(link), .. parts[(i + 1)..]]);
                break;
            }

            if (target is null)
            {
                return resolved;
            }

            if (followed == LinksFollowed)
            {
                throw new IOException($"More than {LinksFollowed} links lead from '{path}'.");
            }

            pending = Plain(target);
        }
    }

    /// <summary>The full path, without a separator at its end and without the prefixes of a path that is passed to Windows as it is.</summary>
    private static string Plain(string path) =>
        WithoutPrefix(Path.TrimEndingDirectorySeparator(Path.GetFullPath(WithoutPrefix(path))));

    private static string WithoutPrefix(string path)
    {
        foreach (var prefix in new[] { @"\\?\UNC\", @"\??\UNC\" })
        {
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return @"\\" + path[prefix.Length..];
            }
        }

        return path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\??\", StringComparison.Ordinal) ? path[4..] : path;
    }

    /// <summary>What a tool was pointed at: like <see cref="InProject"/>, and "." for the workspace itself.</summary>
    public static string Pointed(string path, string workspaceRoot)
    {
        var shown = InProject(path, workspaceRoot);
        if (shown.Length == 0)
        {
            return ".";
        }

        try
        {
            return Path.IsPathFullyQualified(shown)
                && string.Equals(
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(shown)),
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRoot)),
                    StringComparison.OrdinalIgnoreCase)
                ? "."
                : shown;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return shown;
        }
    }

    /// <summary>
    /// The path relative to the project for a file inside the workspace. A file outside of it keeps its
    /// full path, so that it cannot be mistaken for a file of the project.
    /// </summary>
    public static string InProject(string path, string workspaceRoot)
    {
        if (!Path.IsPathFullyQualified(path))
        {
            return path.Replace('\\', '/');
        }

        string full;
        string root;
        try
        {
            full = Path.GetFullPath(path);
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRoot)) + Path.DirectorySeparatorChar;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }

        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            ? full[root.Length..].Replace('\\', '/')
            : full;
    }
}
