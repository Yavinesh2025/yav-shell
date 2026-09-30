using Yav.Tests.Support;
using Yav.Workspace;

namespace Yav.Tests.Workspace;

public class GlobTests
{
    [Theory]
    [InlineData("**/*.test.ts", "src/login.test.ts", true)]
    [InlineData("**/*.test.ts", "login.test.ts", true)]
    [InlineData("**/*.test.ts", "src/login.ts", false)]
    [InlineData("tests/**", "tests/unit/a.cs", true)]
    [InlineData("tests/**", "tests/a.cs", true)]
    [InlineData("tests/**", "src/tests/a.cs", false)]
    [InlineData("**/tests/**", "src/tests/a.cs", true)]
    [InlineData("**/tests/**", "tests/a.cs", true)]
    [InlineData("**/tests/**", "src/testsuite/a.cs", false)]
    [InlineData("*.json", "yav.project.json", true)]
    [InlineData("*.json", "config/app.json", false)]
    [InlineData("yav.project.json", "YAV.Project.JSON", true)]
    [InlineData("src/?.cs", "src/a.cs", true)]
    [InlineData("src/?.cs", "src/ab.cs", false)]
    [InlineData("**/*Tests.cs", "src/App.Tests/LoginTests.cs", true)]
    [InlineData("**/*.Tests/**", "src/App.Tests/Helpers/Util.cs", true)]
    [InlineData("docs/*.md", "docs/sub/a.md", false)]
    [InlineData("a/**/b.txt", "a/b.txt", true)]
    [InlineData("a/**/b.txt", "a/x/y/b.txt", true)]
    [InlineData("**", "anything/at/all.txt", true)]
    [InlineData("src\\*.cs", "src/a.cs", true)]
    public void A_pattern_matches_the_paths_it_should(string pattern, string path, bool expected)
    {
        Assert.Equal(expected, Glob.IsMatch(pattern, path));
    }

    [Fact]
    public void A_path_matches_when_any_pattern_matches()
    {
        Assert.True(Glob.MatchesAny(["docs/**", "**/*.test.ts"], "src/a.test.ts"));
        Assert.False(Glob.MatchesAny(["docs/**", "**/*.test.ts"], "src/a.ts"));
        Assert.False(Glob.MatchesAny([], "src/a.ts"));
    }
}

public class SecretPathTests
{
    [Theory]
    [InlineData(".env", true)]
    [InlineData(".env.local", true)]
    [InlineData("config/.env.production", true)]
    [InlineData("secrets.json", true)]
    [InlineData("config/credentials.json", true)]
    [InlineData("deploy/id_rsa", true)]
    [InlineData("certs/server.pem", true)]
    [InlineData("certs/server.key", true)]
    [InlineData("app.pfx", true)]
    [InlineData(".npmrc", true)]
    [InlineData("src/environment.ts", false)]
    [InlineData("README.md", false)]
    [InlineData("src/keyboard.cs", false)]
    [InlineData(".env.example", false)]
    public void Files_that_usually_hold_secrets_are_recognized(string path, bool expected)
    {
        Assert.Equal(expected, SecretPaths.LooksLikeSecret(path));
    }
}

public class ManifestTests
{
    private static ManifestEntry Entry(string path, string hash, long length = 10) => new(path, length, hash, EntryKind.File, 0);

    [Fact]
    public void The_fingerprint_does_not_depend_on_the_order_files_were_found_in()
    {
        var first = Manifest.From([Entry("b.txt", "h2"), Entry("a.txt", "h1"), Entry("c/d.txt", "h3")]);
        var second = Manifest.From([Entry("c/d.txt", "h3"), Entry("a.txt", "h1"), Entry("b.txt", "h2")]);

        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.Equal(64, first.Fingerprint.Length);
    }

    [Fact]
    public void The_fingerprint_does_not_depend_on_modification_times()
    {
        var first = Manifest.From([new ManifestEntry("a.txt", 10, "h1", EntryKind.File, 1000)]);
        var second = Manifest.From([new ManifestEntry("a.txt", 10, "h1", EntryKind.File, 99999)]);

        Assert.Equal(first.Fingerprint, second.Fingerprint);
    }

    [Theory]
    [InlineData("a.txt", "h1", "a.txt", "h2")]
    [InlineData("a.txt", "h1", "A.txt", "h1")]
    [InlineData("a.txt", "h1", "b.txt", "h1")]
    public void The_fingerprint_changes_with_content_or_path(string path1, string hash1, string path2, string hash2)
    {
        var first = Manifest.From([Entry(path1, hash1)]);
        var second = Manifest.From([Entry(path2, hash2)]);

        Assert.NotEqual(first.Fingerprint, second.Fingerprint);
    }

    [Fact]
    public void A_path_cannot_be_forged_from_two_shorter_entries()
    {
        // Without separators "ab" + "c" and "a" + "bc" would produce the same text to hash.
        var first = Manifest.From([Entry("ab", "c")]);
        var second = Manifest.From([Entry("a", "bc")]);

        Assert.NotEqual(first.Fingerprint, second.Fingerprint);
    }

    [Fact]
    public void A_manifest_survives_being_written_and_read()
    {
        using var directory = new TempDirectory("manifest");
        var manifest = Manifest.From(
        [
            new ManifestEntry("src/ünï 日本/a b.cs", 1234, "aa11", EntryKind.File, 638_000_000_000_000_000),
            new ManifestEntry("link", 0, "bb22", EntryKind.Symlink, 5),
        ]);
        var path = directory.File("m.manifest");

        manifest.Save(path);
        var loaded = Manifest.Load(path);

        Assert.Equal(manifest.Fingerprint, loaded.Fingerprint);
        Assert.Equal(manifest.Entries.Values.OrderBy(e => e.Path), loaded.Entries.Values.OrderBy(e => e.Path));
    }

    [Fact]
    public void A_manifest_that_was_altered_on_disk_is_rejected()
    {
        using var directory = new TempDirectory("manifest");
        var path = directory.File("m.manifest");
        Manifest.From([Entry("a.txt", "h1")]).Save(path);
        File.WriteAllText(path, File.ReadAllText(path).Replace("h1", "h9"));

        Assert.Throws<InvalidDataException>(() => Manifest.Load(path));
    }

    [Fact]
    public void Changes_are_the_difference_between_two_manifests()
    {
        var baseline = Manifest.From([Entry("keep.txt", "k"), Entry("edit.txt", "old", 5), Entry("gone.txt", "g"), Entry("moved/old.txt", "same")]);
        var candidate = Manifest.From([Entry("keep.txt", "k"), Entry("edit.txt", "new", 7), Entry("added.txt", "a"), Entry("moved/new.txt", "same")]);

        var changes = Manifest.Compare(baseline, candidate, _ => false);

        Assert.Equal(["added.txt", "edit.txt", "gone.txt", "moved/new.txt", "moved/old.txt"], changes.Files.Select(f => f.Path));
        var edit = changes.Files.Single(f => f.Path == "edit.txt");
        Assert.Equal(Yav.Core.Runs.ChangeKind.Modified, edit.Kind);
        Assert.Equal("old", edit.BaselineHash);
        Assert.Equal("new", edit.CandidateHash);
        Assert.Equal(5, edit.BaselineLength);
        Assert.Equal(7, edit.CandidateLength);
        Assert.Equal(Yav.Core.Runs.ChangeKind.Deleted, changes.Files.Single(f => f.Path == "gone.txt").Kind);
        Assert.Equal("moved/old.txt", changes.Files.Single(f => f.Path == "moved/new.txt").RenamedFrom);
        Assert.Null(changes.Files.Single(f => f.Path == "added.txt").RenamedFrom);
    }

    [Fact]
    public void Identical_manifests_have_no_changes()
    {
        var manifest = Manifest.From([Entry("a.txt", "h1")]);

        Assert.True(Manifest.Compare(manifest, manifest, _ => false).IsEmpty);
    }
}

public class BlobStoreTests
{
    [Fact]
    public void Content_is_stored_once_and_found_by_its_hash()
    {
        using var directory = new TempDirectory("blobs");
        var store = new BlobStore(directory.Path);
        var source = directory.Write("in/a.txt", "hello");

        var hash = store.StoreFile(source);
        var again = store.StoreFile(directory.Write("in/b.txt", "hello"));

        Assert.Equal("2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824", hash);
        Assert.Equal(hash, again);
        Assert.True(store.Contains(hash));
        Assert.Equal("hello", File.ReadAllText(store.PathOf(hash)));
        Assert.Single(Directory.EnumerateFiles(Path.Combine(directory.Path, "2c")));
    }

    [Fact]
    public void Restoring_writes_the_exact_bytes()
    {
        using var directory = new TempDirectory("blobs");
        var store = new BlobStore(directory.File("store"));
        byte[] bytes = [0, 1, 2, 255, 13, 10, 0, 200];
        var hash = store.StoreFile(directory.WriteBytes("in.bin", bytes));
        var target = directory.File("out/nested/ünï/out.bin");

        store.RestoreTo(hash, target);

        Assert.Equal(bytes, File.ReadAllBytes(target));
    }

    [Fact]
    public void A_blob_that_was_damaged_on_disk_is_not_restored()
    {
        using var directory = new TempDirectory("blobs");
        var store = new BlobStore(directory.File("store"));
        var hash = store.StoreFile(directory.Write("in.txt", "original"));
        File.SetAttributes(store.PathOf(hash), FileAttributes.Normal);
        File.WriteAllText(store.PathOf(hash), "tampered");

        Assert.Throws<InvalidDataException>(() => store.RestoreTo(hash, directory.File("out.txt")));
        Assert.False(File.Exists(directory.File("out.txt")));
    }

    [Fact]
    public void Garbage_collection_removes_only_unreferenced_content()
    {
        using var directory = new TempDirectory("blobs");
        var store = new BlobStore(directory.File("store"));
        var kept = store.StoreFile(directory.Write("a.txt", "keep me"));
        var dropped = store.StoreFile(directory.Write("b.txt", "drop me"));

        var removed = store.CollectGarbage(new HashSet<string> { kept });

        Assert.Equal(1, removed.Files);
        Assert.True(store.Contains(kept));
        Assert.False(store.Contains(dropped));
    }

    [Theory]
    [InlineData("../../../etc/passwd")]
    [InlineData("zz")]
    [InlineData("")]
    [InlineData("2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b982G")]
    public void A_hash_that_is_not_a_hash_is_refused(string hash)
    {
        using var directory = new TempDirectory("blobs");
        var store = new BlobStore(directory.Path);

        Assert.Throws<ArgumentException>(() => store.PathOf(hash));
    }
}

public class UnifiedDiffTests
{
    [Fact]
    public void A_changed_line_is_shown_with_context()
    {
        var before = "one\ntwo\nthree\nfour\nfive\nsix\nseven\neight\n";
        var after = "one\ntwo\nthree\nfour\nFIVE\nsix\nseven\neight\n";

        var diff = UnifiedDiff.Create("a/f.txt", "b/f.txt", before, after);

        Assert.Equal(
            "--- a/f.txt\n+++ b/f.txt\n@@ -2,7 +2,7 @@\n two\n three\n four\n-five\n+FIVE\n six\n seven\n eight\n",
            diff);
    }

    [Fact]
    public void An_added_file_is_all_additions()
    {
        var diff = UnifiedDiff.Create("/dev/null", "b/new.txt", string.Empty, "first\nsecond\n");

        Assert.Equal("--- /dev/null\n+++ b/new.txt\n@@ -0,0 +1,2 @@\n+first\n+second\n", diff);
    }

    [Fact]
    public void A_deleted_file_is_all_removals()
    {
        var diff = UnifiedDiff.Create("a/old.txt", "/dev/null", "first\nsecond\n", string.Empty);

        Assert.Equal("--- a/old.txt\n+++ /dev/null\n@@ -1,2 +0,0 @@\n-first\n-second\n", diff);
    }

    [Fact]
    public void Changes_far_apart_get_separate_hunks()
    {
        var lines = Enumerable.Range(1, 30).Select(i => $"line {i}").ToArray();
        var changed = lines.ToArray();
        changed[1] = "CHANGED 2";
        changed[27] = "CHANGED 28";

        var diff = UnifiedDiff.Create("a/f", "b/f", string.Join('\n', lines) + "\n", string.Join('\n', changed) + "\n");

        Assert.Equal(2, diff.Split('\n').Count(l => l.StartsWith("@@", StringComparison.Ordinal)));
        Assert.Contains("-line 2\n+CHANGED 2\n", diff);
        Assert.Contains("-line 28\n+CHANGED 28\n", diff);
        Assert.DoesNotContain("line 15", diff);
    }

    [Fact]
    public void A_missing_final_line_break_is_marked()
    {
        var diff = UnifiedDiff.Create("a/f", "b/f", "one\ntwo", "one\ntwo\n");

        Assert.Contains("-two\n\\ No newline at end of file\n+two\n", diff);
    }

    [Fact]
    public void Identical_text_has_no_diff()
    {
        Assert.Equal(string.Empty, UnifiedDiff.Create("a/f", "b/f", "same\n", "same\n"));
    }

    [Fact]
    public void Windows_line_endings_are_compared_line_by_line()
    {
        var diff = UnifiedDiff.Create("a/f", "b/f", "one\r\ntwo\r\n", "one\r\nTWO\r\n");

        Assert.Contains("-two\r\n+TWO\r\n", diff);
        Assert.Contains(" one\r\n", diff);
    }

    [Theory]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00 }, true)]
    [InlineData(new byte[] { 0x68, 0x65, 0x6C, 0x6C, 0x6F, 0x0A }, false)]
    [InlineData(new byte[] { 0xEF, 0xBB, 0xBF, 0x68, 0x69 }, false)]
    [InlineData(new byte[] { }, false)]
    public void Binary_content_is_recognized(byte[] content, bool expected)
    {
        Assert.Equal(expected, UnifiedDiff.IsBinary(content));
    }
}
