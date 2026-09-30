using System.Globalization;
using System.Text;
using System.Text.Json;
using Yav.Core.Settings;

namespace Yav.Console.Composition;

public sealed record LoadedSettings(AppSettings Settings, IReadOnlyList<string> Problems);

/// <summary>
/// Reads and writes YAV's own settings. They live outside every project, so nothing in a repository can
/// change them, and they never contain a secret.
/// </summary>
public sealed class SettingsStore
{
    private static readonly string[] SecretWords = ["KEY", "TOKEN", "SECRET", "PASSWORD", "PASSWD", "CREDENTIAL"];
    private static readonly string[] Shells = ["pwsh", "powershell", "cmd"];
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly YavPaths _paths;
    private bool _writtenByNewerVersion;

    public SettingsStore(YavPaths paths)
    {
        _paths = paths;
    }

    public string Path => _paths.SettingsFile;

    public LoadedSettings Load()
    {
        var problems = new List<string>();
        if (!File.Exists(_paths.SettingsFile))
        {
            return new LoadedSettings(new AppSettings(), problems);
        }

        string text;
        try
        {
            text = File.ReadAllText(_paths.SettingsFile, Utf8NoBom);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problems.Add($"{_paths.SettingsFile} could not be read ({ex.Message}). The defaults are used for this session.");
            _writtenByNewerVersion = true;
            return new LoadedSettings(new AppSettings(), problems);
        }

        AppSettings settings;
        try
        {
            using (var document = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }))
            {
                if (document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty("schemaVersion", out var version)
                    && version.TryGetInt32(out var number)
                    && number > AppSettings.CurrentSchemaVersion)
                {
                    _writtenByNewerVersion = true;
                    problems.Add(
                        $"{_paths.SettingsFile} was written by a newer version of YAV (format {number}). It is read as far as this version understands it "
                        + "and it is not changed; settings you change now apply to this session only.");
                }
            }

            settings = AppSettings.FromJson(text);
        }
        catch (JsonException ex)
        {
            var kept = KeepAside(text);
            problems.Add(
                $"settings.json is not valid ({ex.Message.Split('\n')[0].Trim()}). The defaults are used. "
                + $"The file was kept as {System.IO.Path.GetFileName(kept)}.");
            return new LoadedSettings(new AppSettings(), problems);
        }

        return new LoadedSettings(Normalize(settings, problems), problems);
    }

    /// <summary>False when the file on disk must not be replaced, because a newer version of YAV wrote it.</summary>
    public bool Save(AppSettings settings)
    {
        if (_writtenByNewerVersion)
        {
            return false;
        }

        var safe = WithoutSecrets(settings) with { SchemaVersion = AppSettings.CurrentSchemaVersion };
        Directory.CreateDirectory(_paths.Home);
        var temporary = _paths.SettingsFile + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            File.WriteAllText(temporary, safe.ToJson(), Utf8NoBom);
            File.Move(temporary, _paths.SettingsFile, overwrite: true);
            return true;
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    public static bool LooksLikeSecret(string name) =>
        SecretWords.Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase));

    private string KeepAside(string text)
    {
        var name = string.Create(CultureInfo.InvariantCulture, $"settings.unreadable-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");
        var path = System.IO.Path.Combine(_paths.Home, name);
        File.WriteAllText(path, text, Utf8NoBom);
        return path;
    }

    private static AppSettings WithoutSecrets(AppSettings settings)
    {
        var adapters = new Dictionary<string, AdapterSettings>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, adapter) in settings.Adapters)
        {
            var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, value) in adapter.Environment)
            {
                if (!LooksLikeSecret(name))
                {
                    environment[name] = value;
                }
            }

            adapters[id] = adapter with { Environment = environment };
        }

        return settings with { Adapters = adapters };
    }

    private static AppSettings Normalize(AppSettings settings, List<string> problems)
    {
        var limits = settings.Limits ?? new LimitSettings();
        var repairs = Clamp(limits.MaxRepairCycles, 0, 10, "limits.maxRepairCycles", problems);
        var minutes = Clamp(limits.MaxElapsedMinutes, 0, 7 * 24 * 60, "limits.maxElapsedMinutes", problems);
        var percent = Clamp(limits.StopAtRateLimitPercent, 0, 100, "limits.stopAtRateLimitPercent", problems);
        var queue = Clamp(limits.MaxQueueLength, 0, 1000, "limits.maxQueueLength", problems);
        var tokens = limits.MaxRunTokens;
        if (tokens < 0)
        {
            problems.Add("limits.maxRunTokens was negative and is read as 0, which means no limit.");
            tokens = 0;
        }

        var shell = settings.Shell;
        if (!Shells.Contains(shell, StringComparer.OrdinalIgnoreCase))
        {
            problems.Add($"shell '{shell}' is not one of {string.Join(", ", Shells)}; pwsh is used.");
            shell = "pwsh";
        }

        var adapters = new Dictionary<string, AdapterSettings>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, adapter) in settings.Adapters ?? [])
        {
            var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, value) in adapter.Environment ?? [])
            {
                if (LooksLikeSecret(name))
                {
                    problems.Add($"adapters.{id}.environment.{name} looks like a credential and is ignored. Credentials are kept with /login, never in settings.");
                    continue;
                }

                environment[name] = value;
            }

            adapters[id] = adapter with { Environment = environment };
        }

        return settings with
        {
            Limits = limits with
            {
                MaxRepairCycles = repairs,
                MaxElapsedMinutes = minutes,
                StopAtRateLimitPercent = percent,
                MaxQueueLength = queue,
                MaxRunTokens = tokens,
            },
            Retention = settings.Retention ?? new RetentionSettings(),
            Shell = shell.ToLowerInvariant(),
            Adapters = adapters,
        };
    }

    private static int Clamp(int value, int minimum, int maximum, string name, List<string> problems)
    {
        var clamped = Math.Clamp(value, minimum, maximum);
        if (clamped != value)
        {
            problems.Add($"{name} was {value}; it is read as {clamped} (allowed: {minimum} to {maximum}).");
        }

        return clamped;
    }
}
