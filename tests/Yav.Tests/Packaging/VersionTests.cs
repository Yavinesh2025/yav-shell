using System.Text.RegularExpressions;
using System.Xml.Linq;
using Yav.Adapters;
using Yav.Tests.Support;

namespace Yav.Tests.Packaging;

/// <summary>
/// The version is written in more than one place, because not every place can read it from another. They
/// have to say the same, or a package names one version and what is in it another.
/// </summary>
public class VersionTests
{
    private static string Repository
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "YavShell.slnx")))
            {
                directory = directory.Parent;
            }

            return directory!.FullName;
        }
    }

    [Fact]
    public void The_manifest_of_the_program_names_the_version_of_the_product()
    {
        XNamespace assembly = "urn:schemas-microsoft-com:asm.v1";
        var manifest = XDocument.Load(Path.Combine(Repository, "src", "Yav.Console", "app.manifest"));

        var identity = manifest.Root!.Element(assembly + "assemblyIdentity")!;

        Assert.Equal(Fixtures.ProductVersion + ".0", identity.Attribute("version")!.Value);
    }

    [Fact]
    public void The_installer_is_built_for_the_version_of_the_product_when_it_is_given_none()
    {
        var script = File.ReadAllText(Path.Combine(Repository, "installer", "yav-shell.iss"));

        var written = Regex.Match(script, "#define AppVersion \"([^\"]+)\"");

        Assert.True(written.Success, "The installer script does not say for which version it is built.");
        Assert.Equal(Fixtures.ProductVersion, written.Groups[1].Value);
    }

    [Fact]
    public void An_agent_is_told_the_version_of_the_product_unless_another_one_was_set()
    {
        Assert.Equal(Fixtures.ProductVersion, new AdapterOptions().EffectiveClientVersion);
        Assert.Equal("1.2.3-test", new AdapterOptions(ClientVersion: "1.2.3-test").EffectiveClientVersion);
    }
}
