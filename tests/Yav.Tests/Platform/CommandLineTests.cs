using Yav.Platform.Processes;

namespace Yav.Tests.Platform;

public class CommandLineTests
{
    [Theory]
    [InlineData("simple", "simple")]
    [InlineData("a b", "\"a b\"")]
    [InlineData("", "\"\"")]
    [InlineData("a\"b", "\"a\\\"b\"")]
    [InlineData(@"a\b", @"a\b")]
    [InlineData(@"a b\", "\"a b\\\\\"")]
    [InlineData("a\\\"b", "\"a\\\\\\\"b\"")]
    [InlineData("tab\there", "\"tab\there\"")]
    public void Arguments_are_quoted_by_the_rules_native_programs_parse_with(string argument, string expected)
    {
        var line = CommandLine.Build("tool.exe", [argument]);

        Assert.Equal("tool.exe " + expected, line);
    }

    [Fact]
    public void An_executable_path_with_a_space_is_quoted()
    {
        var line = CommandLine.Build(@"C:\Program Files\x\tool.exe", ["run"]);

        Assert.Equal("\"C:\\Program Files\\x\\tool.exe\" run", line);
    }

    [Fact]
    public void A_batch_command_line_quotes_operators_so_cmd_does_not_interpret_them()
    {
        var line = CommandLine.BuildForBatch(@"C:\x y\t.cmd", ["a b", "c&d", "plain"]);

        Assert.Equal("/d /s /c \"\"C:\\x y\\t.cmd\" \"a b\" \"c&d\" plain\"", line);
    }

    [Fact]
    public void A_batch_argument_ending_in_backslashes_has_them_doubled()
    {
        var line = CommandLine.BuildForBatch(@"C:\t.cmd", [@"C:\dir with space\\"]);

        Assert.Equal("/d /s /c \"\"C:\\t.cmd\" \"C:\\dir with space\\\\\\\\\"\"", line);
    }

    [Theory]
    [InlineData("%PATH%")]
    [InlineData("say \"hi\"")]
    [InlineData("line\nbreak")]
    [InlineData("bang!")]
    public void A_batch_argument_that_cmd_could_reinterpret_is_refused(string argument)
    {
        Assert.Throws<UnsafeArgumentException>(() => CommandLine.BuildForBatch(@"C:\t.cmd", [argument]));
    }

    [Fact]
    public void The_environment_block_applies_overrides_and_removals()
    {
        Environment.SetEnvironmentVariable("YAV_TEST_REMOVE_ME", "present");
        try
        {
            var block = new string(CommandLine.BuildEnvironmentBlock(new Dictionary<string, string?>
            {
                ["YAV_TEST_ADDED"] = "value one",
                ["YAV_TEST_REMOVE_ME"] = null,
            }));

            var entries = block.Split('\0', StringSplitOptions.RemoveEmptyEntries);
            Assert.Contains("YAV_TEST_ADDED=value one", entries);
            Assert.DoesNotContain(entries, e => e.StartsWith("YAV_TEST_REMOVE_ME=", StringComparison.Ordinal));
            Assert.EndsWith("\0\0", block);
        }
        finally
        {
            Environment.SetEnvironmentVariable("YAV_TEST_REMOVE_ME", null);
        }
    }

    [Theory]
    [InlineData("A=B")]
    [InlineData("")]
    public void An_invalid_environment_variable_name_is_refused(string name)
    {
        Assert.Throws<ArgumentException>(() => CommandLine.BuildEnvironmentBlock(new Dictionary<string, string?> { [name] = "x" }));
    }
}
