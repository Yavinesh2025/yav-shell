using System.Text.Json;
using System.Text.Json.Serialization;
using Yav.Core.Ports;

namespace Yav.Workspace;

public sealed class WorkspaceUnsupportedException(string message, IReadOnlyList<string> reasons) : InvalidOperationException(message)
{
    public IReadOnlyList<string> Reasons { get; } = reasons;
}

/// <summary>What YAV remembers about an isolated workspace. Stored next to it, outside every project.</summary>
public sealed record WorkspaceRecord
{
    public required string WorkspaceId { get; init; }

    public required string TaskId { get; init; }

    public required WorkspaceMode Mode { get; init; }

    public required string OriginalPath { get; init; }

    // The repository root when the project is inside a Git repository; otherwise the project itself.
    public required string OriginalRoot { get; init; }

    public required string RootPath { get; init; }

    public required string BaselineFingerprint { get; init; }

    public string? BaseCommit { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public int FileCount { get; init; }

    public List<string> Notes { get; init; } = [];

    // Branches, tags, stash and HEAD of the shared repository before the run, as "name sha" lines.
    public List<string> RepositoryReferences { get; init; } = [];

    public List<string> ReplicatedIgnored { get; init; } = [];

    public string ToJson() => JsonSerializer.Serialize(this, WorkspaceJson.Default.WorkspaceRecord);

    public static WorkspaceRecord FromJson(string json) =>
        JsonSerializer.Deserialize(json, WorkspaceJson.Default.WorkspaceRecord)
        ?? throw new InvalidDataException("The workspace record is empty.");
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(WorkspaceRecord))]
internal sealed partial class WorkspaceJson : JsonSerializerContext;

/// <summary>The file operations an apply or undo performs on the project.</summary>
public interface IFileOperations
{
    /// <summary>Replaces the target with stored content in one step.</summary>
    void Write(BlobStore store, string hash, string targetPath);

    void Delete(string path);
}

public sealed class FileOperations : IFileOperations
{
    public void Write(BlobStore store, string hash, string targetPath) => store.RestoreTo(hash, targetPath);

    public void Delete(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReadOnly) != 0)
        {
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        }

        File.Delete(path);
    }
}
