using Yav.Core.Settings;
using Yav.Platform.Security;

namespace Yav.Tests.Platform;

/// <summary>
/// These tests use the Credential Manager of the user who runs them, under names that exist for the
/// duration of one test only.
/// </summary>
public sealed class CredentialStoreTests : IDisposable
{
    private readonly string _name = "test-" + Guid.NewGuid().ToString("N");
    private readonly string _scope = "t" + Guid.NewGuid().ToString("N")[..11];
    private readonly List<(WindowsCredentialStore Store, string Name)> _written = [];

    public void Dispose()
    {
        foreach (var (store, name) in _written)
        {
            store.Delete(name);
        }
    }

    private WindowsCredentialStore Store(string? scope)
    {
        var store = new WindowsCredentialStore(scope);
        _written.Add((store, _name));
        return store;
    }

    [Fact]
    public void A_secret_that_was_stored_can_be_read_and_removed()
    {
        var store = Store(_scope);

        store.Write(_name, "sk-ant-not-a-real-key", "test");

        Assert.True(store.Exists(_name));
        Assert.Equal("sk-ant-not-a-real-key", store.Read(_name));
        Assert.True(store.Delete(_name));
        Assert.False(store.Exists(_name));
        Assert.Null(store.Read(_name));
    }

    [Fact]
    public void A_secret_that_was_never_stored_reads_as_nothing_and_removing_it_changes_nothing()
    {
        var store = Store(_scope);

        Assert.False(store.Exists(_name));
        Assert.Null(store.Read(_name));
        Assert.False(store.Delete(_name));
    }

    [Fact]
    public void Storing_again_replaces_the_secret()
    {
        var store = Store(_scope);
        store.Write(_name, "first", "test");

        store.Write(_name, "second ünï 日本", "test");

        Assert.Equal("second ünï 日本", store.Read(_name));
    }

    [Fact]
    public void A_secret_kept_for_one_data_directory_is_not_the_secret_of_another()
    {
        var one = Store(_scope);
        var other = Store(_scope + "x");
        var unscoped = Store(null);

        one.Write(_name, "secret of one", "test");

        Assert.Null(other.Read(_name));
        Assert.Null(unscoped.Read(_name));
        Assert.False(other.Delete(_name));
        Assert.Equal("secret of one", one.Read(_name));
    }

    [Fact]
    public void The_default_data_directory_has_no_scope_and_every_other_one_has_its_own()
    {
        var first = new YavPaths(@"C:\Portable\YAV one");
        var second = new YavPaths(@"C:\Portable\YAV two");

        Assert.Null(YavPaths.Default.CredentialScope);
        Assert.NotNull(first.CredentialScope);
        Assert.NotEqual(first.CredentialScope, second.CredentialScope);
        Assert.Equal(first.CredentialScope, new YavPaths(@"c:\portable\yav ONE\").CredentialScope);
        Assert.Matches("^[0-9a-f]{12}$", first.CredentialScope);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("..")]
    [InlineData("name with blank")]
    [InlineData("name*")]
    public void A_name_or_scope_that_could_address_something_else_is_refused(string name)
    {
        var store = new WindowsCredentialStore(_scope);

        Assert.ThrowsAny<ArgumentException>(() => store.Read(name));
        Assert.ThrowsAny<ArgumentException>(() => store.Write(name, "secret", "test"));
        Assert.ThrowsAny<ArgumentException>(() => store.Delete(name));
        if (name.Trim().Length > 0)
        {
            Assert.ThrowsAny<ArgumentException>(() => new WindowsCredentialStore(name));
        }
    }

    [Fact]
    public void A_secret_that_is_longer_than_the_store_accepts_is_refused_and_nothing_is_stored()
    {
        var store = Store(_scope);

        Assert.Throws<ArgumentException>(() => store.Write(_name, new string('k', 3000), "test"));

        Assert.False(store.Exists(_name));
    }
}
