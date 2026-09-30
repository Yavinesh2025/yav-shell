using Yav.Console.Composition;
using Yav.Core.Profiles;
using Yav.Core.Settings;
using Yav.Tests.Support;

namespace Yav.Tests.Console;

public class SettingsStoreTests
{
    private static (SettingsStore Store, YavPaths Paths, TempDirectory Home) Store()
    {
        var home = new TempDirectory("settings");
        var paths = new YavPaths(home.Path);
        return (new SettingsStore(paths), paths, home);
    }

    [Fact]
    public void Without_a_file_the_defaults_apply_and_quality_lock_is_on()
    {
        var (store, _, home) = Store();
        using var cleanup = home;

        var loaded = store.Load();

        Assert.Empty(loaded.Problems);
        Assert.True(loaded.Settings.QualityLock);
        Assert.True(loaded.Settings.Strict);
        Assert.True(loaded.Settings.RequireGates);
        Assert.False(loaded.Settings.Adaptive);
        Assert.False(loaded.Settings.Telemetry);
        Assert.Equal(ProviderSpeedMode.Standard, loaded.Settings.Speed);
        Assert.Null(loaded.Settings.ModelA);
        Assert.Equal(2, loaded.Settings.Limits.MaxRepairCycles);
    }

    [Fact]
    public void What_was_saved_is_read_back()
    {
        var (store, paths, home) = Store();
        using var cleanup = home;
        var settings = new AppSettings
        {
            ModelA = new RoleSelection("codex-app-server", "gpt-6-astra", "max"),
            ModelB = new RoleSelection("claude-cli", "opus"),
            Strict = false,
            Limits = new LimitSettings { MaxElapsedMinutes = 90, MaxRepairCycles = 3 },
            LastProject = @"C:\Projects\My App ünï",
        };

        store.Save(settings);
        var loaded = new SettingsStore(paths).Load();

        Assert.Empty(loaded.Problems);
        Assert.Equal(settings.ModelA, loaded.Settings.ModelA);
        Assert.Equal(settings.ModelB, loaded.Settings.ModelB);
        Assert.False(loaded.Settings.Strict);
        Assert.Equal(90, loaded.Settings.Limits.MaxElapsedMinutes);
        Assert.Equal(3, loaded.Settings.Limits.MaxRepairCycles);
        Assert.Equal(@"C:\Projects\My App ünï", loaded.Settings.LastProject);
    }

    [Fact]
    public void A_file_that_cannot_be_read_is_kept_aside_and_never_overwritten_in_silence()
    {
        var (store, paths, home) = Store();
        using var cleanup = home;
        File.WriteAllText(paths.SettingsFile, "{ \"qualityLock\": fal");

        var loaded = store.Load();
        store.Save(loaded.Settings);

        var problem = Assert.Single(loaded.Problems);
        Assert.Contains("settings.json", problem, StringComparison.Ordinal);
        Assert.True(loaded.Settings.QualityLock);
        var kept = Assert.Single(Directory.GetFiles(paths.Home, "settings.unreadable-*.json"));
        Assert.Equal("{ \"qualityLock\": fal", File.ReadAllText(kept));
    }

    [Fact]
    public void Settings_written_by_a_newer_version_are_not_overwritten()
    {
        var (store, paths, home) = Store();
        using var cleanup = home;
        File.WriteAllText(paths.SettingsFile, "{ \"schemaVersion\": 99, \"qualityLock\": false, \"somethingNew\": true }");

        var loaded = store.Load();
        var saved = store.Save(loaded.Settings with { Strict = false });

        Assert.Contains(loaded.Problems, p => p.Contains("newer version", StringComparison.Ordinal));
        Assert.False(saved);
        Assert.Contains("somethingNew", File.ReadAllText(paths.SettingsFile), StringComparison.Ordinal);
    }

    [Fact]
    public void Values_outside_their_range_are_corrected_and_reported()
    {
        var (store, paths, home) = Store();
        using var cleanup = home;
        File.WriteAllText(
            paths.SettingsFile,
            "{ \"limits\": { \"maxRepairCycles\": 500, \"maxElapsedMinutes\": -5, \"stopAtRateLimitPercent\": 250 }, \"shell\": \"bash\" }");

        var loaded = store.Load();

        Assert.Equal(10, loaded.Settings.Limits.MaxRepairCycles);
        Assert.Equal(0, loaded.Settings.Limits.MaxElapsedMinutes);
        Assert.Equal(100, loaded.Settings.Limits.StopAtRateLimitPercent);
        Assert.Equal("pwsh", loaded.Settings.Shell);
        Assert.Equal(4, loaded.Problems.Count);
    }

    [Fact]
    public void A_setting_that_looks_like_a_secret_is_never_written()
    {
        var (store, paths, home) = Store();
        using var cleanup = home;
        var settings = new AppSettings
        {
            Adapters =
            {
                ["claude-cli"] = new AdapterSettings
                {
                    Environment = new Dictionary<string, string>
                    {
                        ["ANTHROPIC_API_KEY"] = "sk-ant-secret-value",
                        ["OPENAI_API_KEY"] = "sk-secret",
                        ["MY_TOKEN"] = "abc",
                        ["CODEX_HOME"] = @"D:\codex",
                    },
                },
            },
        };

        store.Save(settings);

        var text = File.ReadAllText(paths.SettingsFile);
        Assert.DoesNotContain("sk-ant-secret-value", text, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-secret", text, StringComparison.Ordinal);
        Assert.DoesNotContain("MY_TOKEN", text, StringComparison.Ordinal);
        Assert.Contains("CODEX_HOME", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Saving_replaces_the_file_as_a_whole()
    {
        var (store, paths, home) = Store();
        using var cleanup = home;
        store.Save(new AppSettings { LastProject = new string('x', 5000) });

        store.Save(new AppSettings { LastProject = "short" });

        Assert.Equal("short", new SettingsStore(paths).Load().Settings.LastProject);
        Assert.Empty(Directory.GetFiles(paths.Home, "settings.json.tmp*"));
    }
}
