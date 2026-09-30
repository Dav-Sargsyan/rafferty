using Rafferty.Core;
using Rafferty.Shared;
using System.Net;
using System.Security.Cryptography;
using System.Text;
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
            new("youtube", "YouTube", DiagnosticState.Success, "ok", 20),
            new("googlevideo", "Media", DiagnosticState.Success, "ok", 40),
            new("discord-api", "Discord", DiagnosticState.Success, "ok", 30),
            new("discord-cdn", "CDN", DiagnosticState.Success, "ok", 35),
            new("discord-gateway", "Gateway", DiagnosticState.Success, "ok", 45),
            new("discord-stun", "Voice", DiagnosticState.Success, "ok", 50)
        ];
        DiagnosticResult[] failing =
        [
            new("youtube", "YouTube", DiagnosticState.Error, "failed", 900),
            new("googlevideo", "Media", DiagnosticState.Error, "failed", 900),
            new("discord-api", "Discord", DiagnosticState.Success, "ok", 200),
            new("discord-cdn", "CDN", DiagnosticState.Error, "failed", 900),
            new("discord-gateway", "Gateway", DiagnosticState.Error, "failed", 900),
            new("discord-stun", "Voice", DiagnosticState.Error, "failed", 900)
        ];
        var good = StrategyScorer.Calculate("good", healthy, TimeSpan.FromMilliseconds(200));
        var bad = StrategyScorer.Calculate("bad", failing, TimeSpan.FromSeconds(2));
        Assert.True(good.Score > bad.Score);
        Assert.Equal(1, good.SuccessRate);
    }

    [Fact]
    public void Score_OnlyWeightsServicesSelectedForOptimization()
    {
        DiagnosticResult[] results =
        [
            new("youtube", "YouTube", DiagnosticState.Success, "ok"),
            new("googlevideo", "Media", DiagnosticState.Success, "ok"),
            new("discord-api", "Discord", DiagnosticState.Error, "failed"),
            new("discord-cdn", "CDN", DiagnosticState.Error, "failed"),
            new("discord-gateway", "Gateway", DiagnosticState.Error, "failed"),
            new("discord-stun", "Voice", DiagnosticState.Error, "failed")
        ];

        var youtubeOnly = StrategyScorer.Calculate("youtube", results, TimeSpan.Zero, checkDiscord: false, checkVoice: false);
        var allServices = StrategyScorer.Calculate("all", results, TimeSpan.Zero);

        Assert.Equal(100, youtubeOnly.Score);
        Assert.True(youtubeOnly.Score > allServices.Score);
    }

    [Theory]
    [InlineData("general", "general", "general")]
    [InlineData("general--alt7", "general (ALT7)", "ALT7")]
    [InlineData("general--fake-tls-auto-alt", "general (FAKE TLS AUTO ALT)", "FAKE TLS AUTO ALT")]
    public void StrategyNames_UseReferenceLabels(string id, string name, string expected) =>
        Assert.Equal(expected, StrategyNames.DisplayName(id, name));

    [Fact]
    public void ConnectivitySummary_DoesNotDowngradeYouTubeForOptionalQuic()
    {
        DiagnosticResult[] results =
        [
            new("youtube", "YouTube", DiagnosticState.Success, "ok"),
            new("googlevideo", "Media", DiagnosticState.Success, "ok"),
            new("youtube-quic", "QUIC", DiagnosticState.Warning, "unsupported")
        ];

        Assert.Equal(ServiceReachability.Working, ConnectivityTester.Summarize(results).YouTube);
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

    [Fact]
    public async Task StrategyStore_PersistsUserImportedStrategySeparately()
    {
        var directory = Path.Combine(Path.GetTempPath(), "RaffertyTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var bundledPath = Path.Combine(directory, "strategies.json");
            var customPath = Path.Combine(directory, "custom-strategies.json");
            await File.WriteAllTextAsync(bundledPath, """
                {"schemaVersion":1,"source":"bundled","updatedAt":"2026-01-01T00:00:00Z","strategies":[
                  {"id":"general","name":"general","description":"","targets":[],"protocols":["tcp"],"ports":[443],"arguments":["--filter-tcp=443"]}
                ]}
                """);
            var imported = new Strategy("custom--test", "Imported Test", "", [], ["tcp"], [443], ["--filter-tcp=443"]);
            var store = new StrategyStore(bundledPath, customPath);

            await store.AddOrReplaceAsync(imported);
            var reopened = new StrategyStore(bundledPath, customPath);
            var database = await reopened.LoadAsync();

            Assert.Contains(database.Strategies, strategy => strategy.Id == "general");
            Assert.Contains(database.Strategies, strategy => strategy.Id == "custom--test");
            Assert.True(File.Exists(customPath));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void RuntimeTransformer_PreservesReferenceArgumentsByDefault()
    {
        string[] arguments = ["--wf-tcp=80,443,12", "--new", "--filter-tcp=12", "--ipset={lists}\\ipset-all.txt", "--dpi-desync=fake"];
        Assert.Equal(arguments, StrategyRuntimeTransformer.Transform(arguments, new EngineRuntimeOptions()));
    }

    [Fact]
    public void RuntimeTransformer_AppliesGameRangesAndIpSetModes()
    {
        string[] arguments =
        [
            "--wf-tcp=80,443,12", "--wf-udp=443,12",
            "--new", "--filter-tcp=443", "--ipset={lists}\\ipset-all.txt", "--dpi-desync=fake",
            "--new", "--filter-tcp=12", "--ipset={lists}\\ipset-all.txt", "--dpi-desync=syndata"
        ];
        var none = StrategyRuntimeTransformer.Transform(arguments, new(IpSetMode.None, true, "1024-65535", "2000-3000"));
        Assert.Contains("--wf-tcp=80,443,1024-65535", none);
        Assert.Contains("--wf-udp=443,2000-3000", none);
        Assert.Contains("--filter-tcp=1024-65535", none);
        Assert.DoesNotContain(none, argument => argument.StartsWith("--ipset="));
        Assert.DoesNotContain("--filter-tcp=443", none);

        var any = StrategyRuntimeTransformer.Transform(arguments, new(IpSetMode.Any, false));
        Assert.Contains("--filter-tcp=443", any);
        Assert.DoesNotContain(any, argument => argument.StartsWith("--ipset="));
    }

    [Fact]
    public async Task UpdateService_ChecksVersionAndVerifiesDownloadedSha256()
    {
        var executable = Encoding.UTF8.GetBytes("verified executable");
        var hash = Convert.ToHexString(SHA256.HashData(executable));
        var manifest = $$"""
            {"version":"1.3.0","downloadUrl":"https://updates.example/Rafferty.exe","sha256":"{{hash}}","releaseNotes":"Test","mandatory":false}
            """;
        using var client = new HttpClient(new StubHandler(request => request.RequestUri!.AbsolutePath.EndsWith("update.json")
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(manifest) }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(executable) }));
        using var service = new UpdateService(client);
        var check = await service.CheckAsync("https://updates.example/update.json", new Version(1, 2, 0));
        Assert.True(check.UpdateAvailable);

        var directory = Path.Combine(Path.GetTempPath(), "RaffertyTests", Guid.NewGuid().ToString("N"));
        var destination = Path.Combine(directory, "Rafferty.new.exe");
        try
        {
            await service.DownloadAsync(check.Manifest, destination);
            Assert.Equal(executable, await File.ReadAllBytesAsync(destination));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task UpdateService_RejectsWrongHashAndKeepsDestinationAbsent()
    {
        var manifest = new UpdateManifest("9.0.0", "https://updates.example/Rafferty.exe", new string('0', 64), "Test");
        using var client = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes("tampered"))
        }));
        using var service = new UpdateService(client);
        var directory = Path.Combine(Path.GetTempPath(), "RaffertyTests", Guid.NewGuid().ToString("N"));
        var destination = Path.Combine(directory, "Rafferty.new.exe");
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(manifest, destination));
            Assert.False(File.Exists(destination));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task UpdateInstaller_ReplacesExecutableAndKeepsRollbackCopyUntilCommit()
    {
        var directory = Path.Combine(Path.GetTempPath(), "RaffertyTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var downloaded = Path.Combine(directory, "Rafferty.new.exe");
        var target = Path.Combine(directory, "Rafferty.exe");
        await File.WriteAllTextAsync(downloaded, "new version");
        await File.WriteAllTextAsync(target, "old version");
        try
        {
            var backup = await UpdateInstaller.ReplaceExecutableAsync(downloaded, target);
            Assert.Equal("new version", await File.ReadAllTextAsync(target));
            Assert.Equal("old version", await File.ReadAllTextAsync(backup));
            UpdateInstaller.Commit(backup);
            Assert.False(File.Exists(backup));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task UpdateInstaller_RollbackRestoresPreviousExecutable()
    {
        var directory = Path.Combine(Path.GetTempPath(), "RaffertyTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var downloaded = Path.Combine(directory, "Rafferty.new.exe");
        var target = Path.Combine(directory, "Rafferty.exe");
        await File.WriteAllTextAsync(downloaded, "new version");
        await File.WriteAllTextAsync(target, "old version");
        try
        {
            var backup = await UpdateInstaller.ReplaceExecutableAsync(downloaded, target);
            UpdateInstaller.Rollback(target, backup);
            Assert.Equal("old version", await File.ReadAllTextAsync(target));
            Assert.False(File.Exists(backup));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response(request));
    }
}
