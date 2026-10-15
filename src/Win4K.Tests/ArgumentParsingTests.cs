using Win4K.ScreenSaver;

namespace Win4K.Tests;

/// <summary>
/// Windows is inconsistent about how it invokes a .scr — "/c:12345" packed with a colon in
/// some paths, "/p 12345" as two arguments in others, and no guarantee about casing. Getting
/// this wrong means the settings dialog opens when a preview was wanted, or nothing happens
/// at all.
/// </summary>
public class ArgumentParsingTests
{
    // Verbs are compared by name: Program.Verb is internal, and an internal type cannot
    // appear in the signature of a public xunit theory.
    [Theory]
    [InlineData("/s", "FullScreen")]
    [InlineData("/S", "FullScreen")]
    [InlineData("-s", "FullScreen")]
    [InlineData("/c", "Configure")]
    [InlineData("/C", "Configure")]
    [InlineData("/settings", "Configure")]
    [InlineData("/w", "Window")]
    [InlineData("/now", "StartNow")]
    [InlineData("/NOW", "StartNow")]
    [InlineData("/hotkey", "Hotkey")]
    [InlineData("/HOTKEY", "Hotkey")]
    [InlineData("/x", "Unknown")]
    public void Simple_verbs_parse(string arg, string expected)
    {
        Assert.Equal(expected, Program.ParseArguments([arg]).Verb.ToString());
    }

    [Theory]
    [InlineData("--export-settings", "ExportSettings")]
    [InlineData("--IMPORT-SETTINGS", "ImportSettings")]
    [InlineData("/import-settings", "ImportSettings")]
    public void Settings_cli_verbs_parse(string arg, string expected)
    {
        Assert.Equal(expected, Program.ParseArguments([arg]).Verb.ToString());
    }

    [Theory]
    [InlineData(new[] { "--export-settings", @"C:\Users\me\my settings.win4kss.json" }, @"C:\Users\me\my settings.win4kss.json")]
    [InlineData(new[] { "--import-settings", @"D:\backup\a.win4kss.json", "--include-machine" }, @"D:\backup\a.win4kss.json")]
    [InlineData(new[] { @"--export-settings=C:\x\a.json" }, @"C:\x\a.json")]
    public void Settings_cli_verbs_take_the_file_that_follows(string[] args, string expectedPath)
    {
        var (verb, argument) = Program.ParseArguments(args);

        Assert.True(verb is Program.Verb.ExportSettings or Program.Verb.ImportSettings);
        Assert.Equal(expectedPath, argument);
    }

    [Fact]
    public void Settings_cli_verbs_without_a_file_report_no_argument()
    {
        Assert.Null(Program.ParseArguments(["--export-settings"]).Argument);
    }

    [Fact]
    public void Colon_packed_configure_yields_the_owner_handle()
    {
        var (verb, argument) = Program.ParseArguments(["/c:12345"]);

        Assert.Equal(Program.Verb.Configure, verb);
        Assert.Equal("12345", argument);
    }

    [Fact]
    public void Colon_packed_preview_yields_the_parent_handle()
    {
        var (verb, argument) = Program.ParseArguments(["/p:987654"]);

        Assert.Equal(Program.Verb.Preview, verb);
        Assert.Equal("987654", argument);
    }

    [Fact]
    public void Space_separated_preview_yields_the_parent_handle()
    {
        var (verb, argument) = Program.ParseArguments(["/p", "987654"]);

        Assert.Equal(Program.Verb.Preview, verb);
        Assert.Equal("987654", argument);
    }

    [Fact]
    public void Uppercase_colon_form_still_parses()
    {
        var (verb, argument) = Program.ParseArguments(["/P:4242"]);

        Assert.Equal(Program.Verb.Preview, verb);
        Assert.Equal("4242", argument);
    }

    [Fact]
    public void Equals_separator_is_accepted_too()
    {
        var (verb, argument) = Program.ParseArguments(["/p=4242"]);

        Assert.Equal(Program.Verb.Preview, verb);
        Assert.Equal("4242", argument);
    }

    [Fact]
    public void Surrounding_whitespace_is_tolerated()
    {
        var (verb, argument) = Program.ParseArguments([" /p ", " 555 "]);

        Assert.Equal(Program.Verb.Preview, verb);
        Assert.Equal("555", argument);
    }

    [Fact]
    public void Preview_without_a_handle_reports_no_argument()
    {
        var (verb, argument) = Program.ParseArguments(["/p"]);

        Assert.Equal(Program.Verb.Preview, verb);
        Assert.Null(argument);
    }

    [Fact]
    public void No_arguments_picks_a_verb_from_the_executable_extension()
    {
        // Under the test host this is .exe/.dll rather than .scr, so the only stable
        // assertion is that it resolves to one of the two no-argument behaviours.
        var (verb, _) = Program.ParseArguments([]);

        Assert.True(verb is Program.Verb.Window or Program.Verb.Configure);
    }
}
