using Rafferty.Core;
using Rafferty.Shared;
using Xunit;

namespace Rafferty.Core.Tests;

public sealed class StrategyTests
{
    [Fact]
    public void BatImporter_ConvertsWinwsCommandIntoStructuredStrategy()
    {
        const string bat = "\"%BIN%winws.exe\" --wf-tcp=80,443 ^\r\n--filter-tcp=443 --hostlist=\"%LISTS%list-google.txt\" --dpi-desync=multisplit --dpi-desync-split-pos=1";
        var strategy = BatStrategyImporter.Import("test-1", "Test", bat);
        Assert.Contains("tcp", strategy.Protocols);
        Assert.Contains("youtube", strategy.Targets);
        Assert.Contains(443, strategy.Ports);
        Assert.Contains(strategy.Arguments, value => value == "--dpi-desync=multisplit");
        Assert.Contains(strategy.Arguments, value => value.Contains("{lists}"));
    }

    [Fact]
    public void BatImporter_PreservesEveryNewRuleSectionInOrder()
    {
        const string bat = "\"%BIN%winws.exe\" --filter-udp=443 --dpi-desync=fake --new ^\r\n--filter-tcp=443 --dpi-desync=multisplit --new ^\r\n--filter-tcp=80 --dpi-desync=syndata";
        var strategy = BatStrategyImporter.Import("full-chain", "Full chain", bat);
        Assert.Equal(2, strategy.Arguments.Count(argument => argument == "--new"));
        Assert.Equal("--filter-udp=443", strategy.Arguments[0]);
        Assert.Equal("--dpi-desync=syndata", strategy.Arguments[^1]);
    }

    [Fact]
    public void BatImporter_UsesUpstreamDisabledGameFilterDefault()
    {
        const string bat = "%BIN%winws.exe --wf-tcp=443,%GameFilterTCP% --filter-udp=%GameFilterUDP% --dpi-desync=fake";
        var strategy = BatStrategyImporter.Import("game-default", "Game default", bat);
        Assert.Contains("--wf-tcp=443,12", strategy.Arguments);
        Assert.Contains("--filter-udp=12", strategy.Arguments);
    }

    [Fact]
    public void BatImporter_UnescapesCmdLiteralExclamationPayload()
    {
        const string bat = "%BIN%winws.exe --filter-tcp=443 --dpi-desync=fake --dpi-desync-fake-tls=^!";
        var strategy = BatStrategyImporter.Import("cmd-escape", "CMD escape", bat);
        Assert.Contains("--dpi-desync-fake-tls=!", strategy.Arguments);
        Assert.DoesNotContain(strategy.Arguments, argument => argument.Contains('^'));
    }

    [Fact]
    public void Score_PrefersSuccessfulLowLatencyResults()
    {
        DiagnosticResult[] healthy =
        [
            new("one", "One", DiagnosticState.Success, "ok", 20),
            new("two", "Two", DiagnosticState.Success, "ok", 40)
        ];
        DiagnosticResult[] failing =
        [
            new("one", "One", DiagnosticState.Success, "ok", 200),
            new("two", "Two", DiagnosticState.Error, "failed", 900)
        ];
        var good = StrategyScorer.Calculate("good", healthy, TimeSpan.FromMilliseconds(200));
        var bad = StrategyScorer.Calculate("bad", failing, TimeSpan.FromSeconds(2));
        Assert.True(good.Score > bad.Score);
        Assert.Equal(1, good.SuccessRate);
    }

    [Fact]
    public async Task StrategyStore_RejectsDuplicateIds()
    {
        var directory = Path.Combine(Path.GetTempPath(), "RaffertyTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "strategies.json");
            await File.WriteAllTextAsync(path, """
                {"schemaVersion":1,"source":"test","updatedAt":"2026-01-01T00:00:00Z","strategies":[
                  {"id":"same","name":"A","description":"","targets":[],"protocols":["tcp"],"ports":[443],"arguments":["--filter-tcp=443"]},
                  {"id":"same","name":"B","description":"","targets":[],"protocols":["tcp"],"ports":[443],"arguments":["--filter-tcp=443"]}
                ]}
                """);
            await Assert.ThrowsAsync<InvalidDataException>(() => new StrategyStore(path).LoadAsync());
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
