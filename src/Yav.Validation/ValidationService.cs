using System.Globalization;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Yav.Core;
using Yav.Core.Ports;
using Yav.Core.Runs;
using Yav.Core.Text;
using Yav.Platform.Processes;

namespace Yav.Validation;

/// <summary>
/// Runs trusted, explicit project gates and records the evidence. The gate definitions always come from
/// the version the user approved, never from a file an agent could have edited during the run.
/// </summary>
public sealed class ValidationService : IValidationService
{
    private const int TailCharacters = 12_000;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly IProcessRunner _runner;
    private readonly IProjectTrustStore _trust;
    private readonly TimeProvider _clock;
    private readonly string _yavVersion;

    public ValidationService(IProcessRunner runner, IProjectTrustStore trust, TimeProvider clock, string yavVersion)
    {
        _runner = runner;
        _trust = trust;
        _clock = clock;
        _yavVersion = yavVersion;
    }

    public string ConfigurationFileName => "yav.project.json";

    public ProjectConfiguration Parse(string json, out IReadOnlyList<string> errors) => ConfigurationParser.Parse(json, out errors);

    public string Serialize(ProjectConfiguration configuration) => ConfigurationParser.Serialize(configuration);

    /// <summary>The hash of what a configuration means, so that reformatting the file is not a change.</summary>
    public string Hash(ProjectConfiguration configuration) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Serialize(configuration))));

    public ProjectConfigurationState LoadConfiguration(string projectPath)
    {
        var trusted = _trust.GetTrustedConfiguration(projectPath);
        var effective = ProjectConfiguration.Empty;
        string? trustedHash = null;
        var errors = new List<string>();
        if (trusted is { } stored)
        {
            var parsed = Parse(stored.Json, out var storedErrors);
            if (storedErrors.Count == 0)
            {
                effective = parsed;
                trustedHash = stored.Hash;
            }
            else
            {
                errors.Add("The approved configuration stored by YAV can no longer be read: " + storedErrors[0]);
            }
        }

        var file = Path.Combine(projectPath, ConfigurationFileName);
        if (!File.Exists(file))
        {
            return new ProjectConfigurationState(
                trustedHash is null ? ConfigurationTrust.None : ConfigurationTrust.Trusted,
                effective, null, trustedHash, null, null, errors);
        }

        string text;
        try
        {
            text = File.ReadAllText(file, Utf8NoBom);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            errors.Add($"{ConfigurationFileName} could not be read: {ex.Message}");
            return new ProjectConfigurationState(ConfigurationTrust.Invalid, effective, null, trustedHash, null, file, errors);
        }

        var pending = Parse(text, out var fileErrors);
        if (fileErrors.Count > 0)
        {
            errors.AddRange(fileErrors);
            return new ProjectConfigurationState(ConfigurationTrust.Invalid, effective, null, trustedHash, null, file, errors);
        }

        var fileHash = Hash(pending);
        if (trustedHash is not null && string.Equals(trustedHash, fileHash, StringComparison.Ordinal))
        {
            return new ProjectConfigurationState(ConfigurationTrust.Trusted, effective, null, trustedHash, fileHash, file, errors);
        }

        return new ProjectConfigurationState(
            trustedHash is null ? ConfigurationTrust.Untrusted : ConfigurationTrust.Changed,
            effective, pending, trustedHash, fileHash, file, errors);
    }

    public void TrustConfiguration(string projectPath, ProjectConfiguration configuration, string json)
    {
        // What is stored is the canonical form of what was approved, so the stored text and its hash always agree.
        var canonical = Serialize(configuration);
        _trust.SaveTrustedConfiguration(projectPath, Hash(configuration), canonical);
    }

    public string ComputeEnvironmentFingerprint(IReadOnlyList<GateDefinition> gates, string? configurationHash)
    {
        var builder = new StringBuilder();
        builder.Append("os=").Append(Environment.OSVersion.VersionString).Append('\n');
        builder.Append("yav=").Append(_yavVersion).Append('\n');
        builder.Append("config=").Append(configurationHash ?? "none").Append('\n');
        foreach (var gate in gates.OrderBy(g => g.Id, StringComparer.Ordinal))
        {
            builder.Append("gate=").Append(gate.Id).Append('|').Append(gate.Command).Append('|');

            // A path inside the project belongs to the candidate, which has its own fingerprint.
            var isProjectScript = gate.Command.StartsWith('.') && !Path.IsPathRooted(gate.Command);
            var resolved = isProjectScript ? null : _runner.Resolve(gate.Command);
            if (isProjectScript)
            {
                builder.Append("project-script");
            }
            else if (resolved is null)
            {
                builder.Append("unresolved");
            }
            else
            {
                var info = new FileInfo(resolved);
                builder.Append(resolved.ToLowerInvariant()).Append('|')
                    .Append(info.Length.ToString(CultureInfo.InvariantCulture)).Append('|')
                    .Append(info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture));
            }

            builder.Append('\n');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    public async Task<GateResult> RunGateAsync(GateDefinition gate, GateRunContext context, Action<string>? onOutputLine, CancellationToken cancellationToken)
    {
        var started = _clock.GetUtcNow();
        var timestamp = _clock.GetTimestamp();
        var directory = ResolveDirectory(context.WorkingRoot, gate.WorkingDirectory);

        GateResult Finish(GateStatus status, int? exitCode, string? limitation, string? outputPath, string tail, long bytes) => new(
            ResultId: Ids.NewId("g"),
            RunId: context.RunId,
            GateId: gate.Id,
            GateTitle: gate.Title,
            Kind: gate.Kind,
            Required: gate.Required,
            Binding: context.Binding,
            Status: status,
            ExitCode: exitCode,
            CommandLine: gate.DisplayCommand,
            WorkingDirectory: directory ?? context.WorkingRoot,
            StartedAt: started,
            DurationMs: (long)_clock.GetElapsedTime(timestamp).TotalMilliseconds,
            OutputPath: outputPath,
            OutputTail: tail,
            OutputBytes: bytes,
            Limitation: limitation,
            FailsOnBaseline: null,
            IsBaselineRun: context.IsBaselineRun);

        if (directory is null || !Directory.Exists(directory))
        {
            return Finish(GateStatus.Error, null, $"The working directory '{gate.WorkingDirectory}' does not exist in the checked copy.", null, string.Empty, 0);
        }

        var unmet = await CheckRequirementsAsync(gate, directory, cancellationToken).ConfigureAwait(false);
        if (unmet.Count > 0)
        {
            // The gate is not run. A check that cannot be performed is reported as exactly that.
            return Finish(GateStatus.Unverified, null, string.Join("; ", unmet), null, string.Empty, 0);
        }

        var executable = _runner.Resolve(gate.Command, directory);
        if (executable is null)
        {
            return Finish(GateStatus.Error, null, $"The command '{gate.Command}' was not found on PATH.", null, string.Empty, 0);
        }

        Directory.CreateDirectory(context.EvidenceDirectory);
        var logName = $"gate-{gate.Id}-{Shorten(context.Binding.CandidateFingerprint)}{(context.IsBaselineRun ? "-baseline" : string.Empty)}-{started:HHmmssfff}.log";
        var logPath = Path.Combine(context.EvidenceDirectory, logName);

        var tail = new BoundedTextBuffer(TailCharacters * 2);
        var tailGate = new Lock();
        var environment = gate.Environment.ToDictionary(p => p.Key, p => (string?)p.Value, StringComparer.OrdinalIgnoreCase);

        ProcessResult result;
        try
        {
            result = await _runner.RunAsync(
                new ProcessSpec(executable, gate.Arguments, directory, environment, Label: "gate " + gate.Id),
                new CaptureOptions(
                    Timeout: TimeSpan.FromSeconds(gate.TimeoutSeconds),
                    MaxCapturedCharacters: 64 * 1024,
                    LogPath: logPath,
                    OnOutputLine: line =>
                    {
                        // The log keeps the raw bytes as text; what is shown and stored as the tail is sanitized.
                        var clean = TerminalSanitizer.Clean(line);
                        lock (tailGate)
                        {
                            tail.AppendLine(clean);
                        }

                        onOutputLine?.Invoke(clean);
                    }),
                cancellationToken).ConfigureAwait(false);
        }
        catch (UnsafeArgumentException ex)
        {
            return Finish(GateStatus.Error, null, ex.Message, null, string.Empty, 0);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            return Finish(GateStatus.Error, null, $"The command could not be started: {ex.Message}", null, string.Empty, 0);
        }

        string shown;
        long total;
        lock (tailGate)
        {
            shown = tail.Tail(TailCharacters);
            total = tail.TotalCharacters;
        }

        if (result.Cancelled)
        {
            return Finish(GateStatus.Cancelled, null, "The check was stopped before it finished.", logPath, shown, total);
        }

        if (result.TimedOut)
        {
            return Finish(GateStatus.TimedOut, null, $"The check did not finish within {gate.TimeoutSeconds} seconds and was ended.", logPath, shown, total);
        }

        var passed = gate.SuccessExitCodes.Contains(result.ExitCode);
        return Finish(passed ? GateStatus.Passed : GateStatus.Failed, result.ExitCode, null, logPath, shown, total);
    }

    public IReadOnlyList<GateProposal> Detect(string projectPath) => GateDetector.Detect(projectPath);

    private async Task<List<string>> CheckRequirementsAsync(GateDefinition gate, string directory, CancellationToken cancellationToken)
    {
        var unmet = new List<string>();
        foreach (var requirement in gate.Requires)
        {
            var separator = requirement.IndexOf(':');
            var kind = requirement[..separator];
            var value = requirement[(separator + 1)..];
            switch (kind)
            {
                case "env":
                    if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(value)) && !gate.Environment.ContainsKey(value))
                    {
                        unmet.Add($"the environment variable {value} is not set");
                    }

                    break;

                case "tool":
                    if (_runner.Resolve(value) is null)
                    {
                        unmet.Add($"the tool '{value}' was not found on PATH");
                    }

                    break;

                case "file":
                    var path = Path.GetFullPath(Path.Combine(directory, value));
                    if (!File.Exists(path) && !Directory.Exists(path))
                    {
                        unmet.Add($"'{value}' does not exist");
                    }

                    break;

                case "tcp":
                    if (!await ReachableAsync(value, cancellationToken).ConfigureAwait(false))
                    {
                        unmet.Add($"the service at {value} is not reachable");
                    }

                    break;

                case "manual":
                    unmet.Add($"requires manual inspection: {value}");
                    break;
            }
        }

        return unmet;
    }

    private static async Task<bool> ReachableAsync(string endpoint, CancellationToken cancellationToken)
    {
        var separator = endpoint.LastIndexOf(':');
        if (separator <= 0 || !int.TryParse(endpoint[(separator + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var port))
        {
            return false;
        }

        try
        {
            using var client = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            await client.ConnectAsync(endpoint[..separator], port, timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ArgumentException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return false;
        }
    }

    private static string? ResolveDirectory(string root, string relative)
    {
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        if (string.IsNullOrEmpty(relative))
        {
            return rootFull;
        }

        var full = Path.GetFullPath(Path.Combine(rootFull, relative.Replace('/', Path.DirectorySeparatorChar)));
        return full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    private static string Shorten(string fingerprint) => fingerprint.Length > 12 ? fingerprint[..12] : fingerprint;
}

/// <summary>Suggests gates from what a project contains. A suggestion never runs until the user approves it.</summary>
internal static class GateDetector
{
    private const string PlaceholderTest = "no test specified";

    public static IReadOnlyList<GateProposal> Detect(string projectPath)
    {
        var proposals = new List<GateProposal>();
        if (!Directory.Exists(projectPath))
        {
            return proposals;
        }

        bool Has(string name) => File.Exists(Path.Combine(projectPath, name));
        bool Any(string pattern) => Directory.EnumerateFiles(projectPath, pattern, SearchOption.TopDirectoryOnly).Any();

        if (Any("*.sln") || Any("*.slnx") || Any("*.csproj") || Any("*.fsproj"))
        {
            proposals.Add(Propose("build", GateKind.Build, "Build", "dotnet", ["build", "--nologo"], 1800, "A .NET solution or project is in the project root."));
            proposals.Add(Propose("test", GateKind.Test, "Tests", "dotnet", ["test", "--nologo"], 3600, "A .NET solution or project is in the project root."));
            return proposals;
        }

        if (Has("package.json"))
        {
            AddNode(projectPath, proposals);
            return proposals;
        }

        if (Has("Cargo.toml"))
        {
            proposals.Add(Propose("build", GateKind.Build, "Build", "cargo", ["build"], 3600, "Cargo.toml is in the project root."));
            proposals.Add(Propose("test", GateKind.Test, "Tests", "cargo", ["test"], 3600, "Cargo.toml is in the project root."));
            return proposals;
        }

        if (Has("go.mod"))
        {
            proposals.Add(Propose("build", GateKind.Build, "Build", "go", ["build", "./..."], 1800, "go.mod is in the project root."));
            proposals.Add(Propose("vet", GateKind.Lint, "Vet", "go", ["vet", "./..."], 1800, "go.mod is in the project root."));
            proposals.Add(Propose("test", GateKind.Test, "Tests", "go", ["test", "./..."], 3600, "go.mod is in the project root."));
            return proposals;
        }

        if (Has("pyproject.toml") || Has("pytest.ini") || Has("setup.py") || Has("requirements.txt"))
        {
            proposals.Add(Propose("test", GateKind.Test, "Tests", "python", ["-m", "pytest"], 3600, "A Python project file is in the project root."));
            return proposals;
        }

        return proposals;
    }

    private static void AddNode(string projectPath, List<GateProposal> proposals)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(projectPath, "package.json")),
                new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return;
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("scripts", out var scripts) || scripts.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            var manager = File.Exists(Path.Combine(projectPath, "pnpm-lock.yaml")) ? "pnpm"
                : File.Exists(Path.Combine(projectPath, "yarn.lock")) ? "yarn"
                : File.Exists(Path.Combine(projectPath, "bun.lockb")) || File.Exists(Path.Combine(projectPath, "bun.lock")) ? "bun"
                : "npm";

            foreach (var (script, id, kind, title) in new[]
            {
                ("build", "build", GateKind.Build, "Build"),
                ("lint", "lint", GateKind.Lint, "Lint"),
                ("typecheck", "typecheck", GateKind.TypeCheck, "Type check"),
                ("type-check", "typecheck", GateKind.TypeCheck, "Type check"),
                ("test", "test", GateKind.Test, "Tests"),
            })
            {
                if (!scripts.TryGetProperty(script, out var body) || body.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var text = body.GetString() ?? string.Empty;
                if (text.Contains(PlaceholderTest, StringComparison.OrdinalIgnoreCase) || proposals.Any(p => p.Gate.Id == id))
                {
                    continue;
                }

                proposals.Add(Propose(id, kind, title, manager, ["run", script], 3600, $"package.json defines the script \"{script}\"."));
            }
        }
    }

    private static GateProposal Propose(string id, GateKind kind, string title, string command, string[] arguments, int timeout, string reason) =>
        new(new GateDefinition(id, kind, title, command, arguments, string.Empty, timeout, true, new Dictionary<string, string>(), [0], []), reason);
}
