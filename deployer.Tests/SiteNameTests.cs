using Deployer;

public class SiteNameTests
{
    [Theory]
    [InlineData("Jiří", "jiri")]
    [InlineData("  Žluťoučký Kůň ", "zlutoucky-kun")]
    [InlineData("Anna_Nová!!", "anna-nova")]
    [InlineData("--abc--", "abc")]
    public void Normalizes(string input, string expected) =>
        Assert.Equal(expected, SiteName.Parse(input).Name);

    [Theory]
    [InlineData("ab")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("a-very-long-name-that-goes-past-thirty")]
    [InlineData("deploy")]
    [InlineData("Admin")]
    [InlineData("API")]
    [InlineData("!!!")]
    public void Rejects(string? input) => Assert.NotNull(SiteName.Parse(input).Error);

    [Theory]
    [InlineData("jiri", true)]
    [InlineData("Jiri", false)]
    [InlineData("../etc", false)]
    [InlineData("www", false)]
    [InlineData(".tmp-x", false)]
    public void IsValidIsStrict(string name, bool ok) => Assert.Equal(ok, SiteName.IsValid(name));
}
