using Microsoft.Extensions.Configuration;
using VuStudio.Agent;
using VuStudio.Tests.Sources;

namespace VuStudio.Tests.Agent;

public class AgentSecurityTests
{
    private static AgentOptions Options(params (string Key, string Value)[] values) =>
        AgentOptions.From(new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build());

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("[::1]", true)]
    [InlineData("localhost", true)]
    [InlineData("0.0.0.0", false)]
    [InlineData("::", false)]
    [InlineData("192.168.1.4", false)]
    [InlineData("*", false)]
    public void Loopback_binds_are_recognised(string bind, bool loopback) =>
        Assert.Equal(loopback, AgentSecurity.IsLoopbackBind(bind));

    [Fact]
    public void A_network_agent_without_a_token_generates_and_keeps_one()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "checks Unix permission bits");
        using var dir = new TempDirectory("agent");
        var options = Options(("data", dir.Path));
        var file = AgentSecurity.TokenFile(options);
        Assert.Equal(Path.Combine(dir.Path, "token"), file);

        var first = AgentSecurity.EnsureToken(options, file);
        Assert.Equal(TokenSource.Generated, first.Source);
        Assert.True(first.Options.Token!.Length >= 32);
        Assert.Equal(first.Options.Token, File.ReadAllText(file));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));

        var second = AgentSecurity.EnsureToken(options, file);
        Assert.Equal(TokenSource.Stored, second.Source);
        Assert.Equal(first.Options.Token, second.Options.Token);
    }

    [Fact]
    public void Configured_loopback_and_insecure_agents_do_not_generate_a_token()
    {
        using var dir = new TempDirectory("agent");
        var file = dir.Combine("token");

        var configured = AgentSecurity.EnsureToken(Options(("token", "my-own-token-123")), file);
        Assert.Equal((TokenSource.Configured, "my-own-token-123"), (configured.Source, configured.Options.Token));

        var loopback = AgentSecurity.EnsureToken(Options(("bind", "127.0.0.1")), file);
        Assert.Equal(TokenSource.NotNeeded, loopback.Source);
        Assert.Null(loopback.Options.Token);

        var insecure = AgentSecurity.EnsureToken(Options(("insecure", "1")), file);
        Assert.Equal(TokenSource.Insecure, insecure.Source);
        Assert.Null(insecure.Options.Token);

        Assert.False(File.Exists(file));
    }

    [Fact]
    public void An_unwritable_state_file_still_yields_a_token()
    {
        using var dir = new TempDirectory("agent");
        var blocker = dir.Write("not-a-dir", "x");
        var outcome = AgentSecurity.EnsureToken(Options(), Path.Combine(blocker, "token"));
        Assert.Equal(TokenSource.Ephemeral, outcome.Source);
        Assert.NotNull(outcome.Options.Token);
        Assert.NotNull(outcome.Error);
    }

    [Fact]
    public void Token_file_follows_the_documented_order()
    {
        var env = new Dictionary<string, string?> { ["STATE_DIRECTORY"] = "/var/lib/vustudio-agent", ["XDG_STATE_HOME"] = "/x" };
        Assert.Equal("/data/token", AgentSecurity.TokenFile(Options(("data", "/data")), k => env.GetValueOrDefault(k)).Replace('\\', '/'));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal("/var/lib/vustudio-agent/token", AgentSecurity.TokenFile(Options(), k => env.GetValueOrDefault(k)));
            env.Remove("STATE_DIRECTORY");
            Assert.Equal("/x/vustudio-agent/token", AgentSecurity.TokenFile(Options(), k => env.GetValueOrDefault(k)));
        }
    }

    [Theory]
    [InlineData("192.168.1.4", true)]
    [InlineData("[fe80::1]", true)]
    [InlineData("localhost", true)]
    [InlineData("tower", true)]
    [InlineData("TOWER.lan", true)]
    [InlineData("tower.local", true)]
    [InlineData("steamdeck.local", true)]
    [InlineData("stats.example.org", true)]
    [InlineData("attacker.example.com", false)]
    [InlineData("tower.attacker.com", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Host_header_check_blocks_rebound_names(string? host, bool allowed) =>
        Assert.Equal(allowed, AgentSecurity.IsAllowedHost(host, ["stats.example.org"], AgentSecurity.BuildMachineNames("Tower")));

    [Fact]
    public void Extra_hosts_and_insecure_are_read_from_the_settings()
    {
        var options = Options(("allowed-hosts", "a.example, b.example"), ("insecure", "true"), ("data", "/data"));
        Assert.Equal(["a.example", "b.example"], options.AllowedHosts);
        Assert.True(options.Insecure);
        Assert.Equal("/data", options.DataDirectory);
    }
}
