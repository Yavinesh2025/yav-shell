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
    public void Every_version_of_the_build_names_the_version_of_the_product()
    {
        // yav --version reports InformationalVersion; Windows shows FileVersion; .NET binds by AssemblyVersion.
        var props = XDocument.Load(Path.Combine(Repository, "Directory.Build.props"));
        string Named(string name) => Assert.Single(props.Descendants(name)).Value;

        var version = Named("Version");
        Assert.Equal(Fixtures.ProductVersion, version);
        Assert.Equal(version, Named("InformationalVersion"));
        Assert.Equal(version + ".0", Named("AssemblyVersion"));
        Assert.Equal(version + ".0", Named("FileVersion"));
    }

    [Fact]
    public void An_agent_is_told_the_version_of_the_product_unless_another_one_was_set()
    {
        Assert.Equal(Fixtures.ProductVersion, new AdapterOptions().EffectiveClientVersion);
        Assert.Equal("1.2.3-test", new AdapterOptions(ClientVersion: "1.2.3-test").EffectiveClientVersion);
    }
}
