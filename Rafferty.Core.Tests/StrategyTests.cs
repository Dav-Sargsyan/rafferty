using Rafferty.Core;
using Rafferty.Shared;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Rafferty.Core.Tests;

public sealed class StrategyTests
{
    private static string TestData(string name) => Path.Combine(AppContext.BaseDirectory, "TestData", name);

    [Fact]
    public async Task EveryBundledClassicStrategyMatchesPinnedGoldenSnapshot()
    {
        var current = await JsonSerializer.DeserializeAsync<StrategyDatabase>(File.OpenRead(TestData("strategies.json")), JsonDefaults.Options);
        Assert.NotNull(current);
        Assert.Equal(22, current.Strategies.Count);
        foreach (var strategy in current.Strategies)
        {
            var comparison = await GoldenStrategyComparer.CompareAsync(strategy, TestData("golden-classic.json"));
            Assert.True(comparison.Match, $"{strategy.Id}: {string.Join(Environment.NewLine, comparison.Differences)}");
        }
    }

    [Fact]
    public async Task BundledStrategiesReferenceOnlyExistingRuntimeFiles()
    {
        var classic = await JsonSerializer.DeserializeAsync<StrategyDatabase>(File.OpenRead(TestData("strategies.json")), JsonDefaults.Options);
        var nextGen = await JsonSerializer.DeserializeAsync<StrategyDatabase>(File.OpenRead(TestData("nextgen-strategies.json")), JsonDefaults.Options);
        Assert.NotNull(classic);
        Assert.NotNull(nextGen);
        Assert.NotEmpty(nextGen.Strategies);

        foreach (var strategy in classic.Strategies.Concat(nextGen.Strategies))
        {
            var engineRoot = TestData(strategy.EngineType == EngineType.NextGen ? "engine-nextgen" : "engine");
            foreach (var file in (strategy.LuaFiles ?? []).Concat(strategy.RequiredFiles ?? []))
                Assert.True(File.Exists(Path.Combine(engineRoot, file)), $"{strategy.Id} requires missing {file}");
            foreach (var argument in strategy.Arguments)
            {
                var value = argument[(argument.IndexOf('=') + 1)..];
                if (value.StartsWith("{engine}\\", StringComparison.OrdinalIgnoreCase))
                    Assert.True(File.Exists(Path.Combine(engineRoot, value[9..])), $"{strategy.Id} requires missing {value}");
                if (value.StartsWith("{lists}\\", StringComparison.OrdinalIgnoreCase))
                    Assert.True(File.Exists(Path.Combine(TestData("lists"), value[8..])), $"{strategy.Id} requires missing {value}");
            }
        }
    }
    [Fact]
    public void Strategy_DefaultsToClassicAndRejectsAutoEngine()
    {
        var classic = new Strategy("classic", "Classic", "", [], ["tcp"], [443], ["--filter-tcp=443"]);
        Assert.Equal(EngineType.Classic, classic.EngineType);

        var automatic = classic with { Id = "automatic", EngineType = EngineType.Auto };
        Assert.Throws<InvalidDataException>(automatic.Validate);
    }

    [Fact]
    public void AutoOptimization_UsesOnlyClassicUntilNextGenIsValidated()
    {
        var strategies = Enumerable.Range(0, 8)
            .Select(index => new Strategy($"classic-{index}", "Classic", "", [], ["tcp"], [443], ["--filter-tcp=443"]))
            .Concat(Enumerable.Range(0, 8).Select(index => new Strategy($"next-{index}", "Next", "", [], ["tcp"], [443], ["--filter-tcp=443"], EngineType: EngineType.NextGen)))
            .ToArray();

        var candidates = OptimizationEngine.OrderCandidates(strategies, EngineType.Auto, deepSearch: false);

        Assert.Equal(3, candidates.Count);
        Assert.Equal(3, candidates.Count(strategy => strategy.EngineType == EngineType.Classic));
        Assert.DoesNotContain(candidates, strategy => strategy.EngineType == EngineType.NextGen);
        Assert.Equal(EngineType.Classic, OptimizationEngine.OrderCandidates(strategies, EngineType.Auto, false, EngineType.NextGen)[0].EngineType);
        Assert.Equal("classic-5", OptimizationEngine.OrderCandidates(strategies, EngineType.Auto, false, EngineType.NextGen, ["classic-5"])[0].Id);
        Assert.NotEqual("classic-5", OptimizationEngine.OrderCandidates(strategies, EngineType.Auto, false, EngineType.NextGen, ["classic-5"], ["classic-5"])[0].Id);
        Assert.Equal(6, OptimizationEngine.OrderCandidates(strategies, EngineType.NextGen, deepSearch: false).Count);
        Assert.Equal(8, OptimizationEngine.OrderCandidates(strategies, EngineType.Auto, deepSearch: true).Count);
    }

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

    [Fact]
    public void Score_PreservesPartialResultsForEverySelectedTarget()
    {
        DiagnosticResult[] results =
        [
            new("chatgpt-web", "ChatGPT Web", DiagnosticState.Success, "ok", 30),
            new("chatgpt-api", "OpenAI API", DiagnosticState.Error, "blocked", 200),
            new("chatgpt-static", "ChatGPT CDN", DiagnosticState.Success, "ok", 40),
            new("instagram-web", "Instagram Web", DiagnosticState.Error, "blocked", 200),
            new("instagram-api", "Instagram API", DiagnosticState.Error, "blocked", 200),
            new("instagram-media", "Instagram CDN", DiagnosticState.Error, "blocked", 200)
        ];

        var score = StrategyScorer.Calculate("partial", results, TimeSpan.Zero,
            [ServiceTargetCatalog.ChatGpt, ServiceTargetCatalog.Instagram]);

        Assert.NotNull(score.TargetResults);
        Assert.Equal(ServiceReachability.Degraded, score.TargetResults[ServiceTargetCatalog.ChatGpt]);
        Assert.Equal(ServiceReachability.Unavailable, score.TargetResults[ServiceTargetCatalog.Instagram]);
        Assert.InRange(score.Score, 20, 30);
    }

    [Fact]
    public void ServiceListManager_MergesSelectedPacksAndPreservesCustomEntries()
    {
        var directory = Path.Combine(Path.GetTempPath(), "RaffertyTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "services"));
        try
        {
            File.WriteAllLines(Path.Combine(directory, "list-general-user.txt"), ["custom.example"]);
            File.WriteAllLines(Path.Combine(directory, "list-general.txt"), ["base.example", "www.chatgpt.com"]);
            File.WriteAllLines(Path.Combine(directory, "list-exclude.txt"), ["blocked.example"]);
            File.WriteAllLines(Path.Combine(directory, "services", "chatgpt.txt"), ["chatgpt.com", "blocked.example"]);
            File.WriteAllLines(Path.Combine(directory, "services", "instagram.txt"), ["instagram.com"]);

            ServiceListManager.Apply(directory, [ServiceTargetCatalog.ChatGpt]);
            var first = File.ReadAllLines(Path.Combine(directory, "active-hostlist.txt"));
            Assert.Contains("custom.example", first);
            Assert.Contains("base.example", first);
            Assert.Contains("chatgpt.com", first);
            Assert.DoesNotContain("blocked.example", first);
            Assert.DoesNotContain("instagram.com", first);
            var general = File.ReadAllLines(Path.Combine(directory, "active-general-hostlist.txt"));
            Assert.Contains("base.example", general);
            Assert.DoesNotContain("chatgpt.com", general);
            Assert.DoesNotContain("instagram.com", general);

            ServiceListManager.Apply(directory, [ServiceTargetCatalog.Instagram]);
            var second = File.ReadAllLines(Path.Combine(directory, "active-hostlist.txt"));
            Assert.Contains("custom.example", second);
            Assert.Contains("instagram.com", second);
            Assert.DoesNotContain("chatgpt.com", second);
            Assert.DoesNotContain("www.chatgpt.com", second);
            Assert.Equal(["custom.example"], File.ReadAllLines(Path.Combine(directory, "list-general-user.txt")));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void UserSettings_RoundTripKeepsNextGenAndServiceSelection()
    {
        var expected = new UserSettings(PreferredEngine: EngineType.NextGen,
            NextGenStrategyId: "nextgen-discord", CheckChatGpt: false, CheckInstagram: true, CheckTikTok: true,
            CheckTelegram: true, Services: new(TikTok: true, Telegram: true));

        var actual = JsonSerializer.Deserialize<UserSettings>(JsonSerializer.Serialize(expected, JsonDefaults.Options), JsonDefaults.Options);

        Assert.NotNull(actual);
        Assert.Equal(EngineType.NextGen, actual.PreferredEngine);
        Assert.Equal("nextgen-discord", actual.NextGenStrategyId);
        Assert.False(actual.CheckChatGpt);
        Assert.True(actual.CheckInstagram);
        Assert.True(actual.CheckTikTok);
        Assert.True(actual.CheckTelegram);
        Assert.NotNull(actual.Services);
        Assert.True(actual.Services.Telegram);
    }

    [Fact]
    public void UserSettings_CleanInstallKeepsOnlyLegacyServicesEnabled()
    {
        var settings = new UserSettings();
        Assert.Equal(
            [ServiceTargetCatalog.YouTube, ServiceTargetCatalog.Discord, ServiceTargetCatalog.Voice],
            ServiceTargetCatalog.EnabledFromSettings(settings));
    }

    [Fact]
    public void ServiceStrategyComposer_PreservesDefaultClassicCommandExactly()
    {
        string[] arguments =
        [
            "--filter-tcp=80,443", "--hostlist={lists}\\list-general.txt",
            "--hostlist={lists}\\list-general-user.txt", "--dpi-desync=multisplit"
        ];

        var composed = ServiceStrategyComposer.Compose(arguments, EngineType.Classic,
            [ServiceTargetCatalog.YouTube, ServiceTargetCatalog.Discord, ServiceTargetCatalog.Voice]);

        Assert.Equal(arguments, composed);
    }

    [Fact]
    public void ServiceStrategyComposer_AddsPerServiceTlsAndQuicExtensions()
    {
        string[] arguments =
        [
            "--wf-tcp-out=80,443", "--wf-udp-out=443", "--filter-tcp=80,443", "--hostlist={lists}\\list-general.txt",
            "--hostlist={lists}\\list-general-user.txt", "--lua-desync=multisplit:pos=1,midsld",
            "--new", "--filter-udp=443", "--hostlist={lists}\\list-general.txt",
            "--lua-desync=fake:blob=fake_default_quic"
        ];

        var composed = ServiceStrategyComposer.Compose(arguments, EngineType.NextGen,
            [ServiceTargetCatalog.ChatGpt, ServiceTargetCatalog.TikTok]);

        Assert.Contains("--hostlist={lists}\\active-service-chatgpt.txt", composed);
        Assert.Contains("--hostlist={lists}\\active-service-tiktok.txt", composed);
        Assert.Contains("--hostlist={lists}\\active-general-hostlist.txt", composed);
        Assert.True(composed.Count(argument => argument == "--filter-udp=443") >= 2);
        Assert.Equal(1, composed.Count(argument => argument == "--wf-tcp-out=80,443"));
        Assert.Equal(1, composed.Count(argument => argument == "--wf-udp-out=443"));
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

    [Theory]
    [InlineData(200, DiagnosticState.Success, ConnectivityState.Working, true)]
    [InlineData(403, DiagnosticState.Warning, ConnectivityState.ServerRejected, false)]
    [InlineData(404, DiagnosticState.Warning, ConnectivityState.Inconclusive, false)]
    [InlineData(503, DiagnosticState.Error, ConnectivityState.Failed, false)]
    public void ConnectivityClassification_DoesNotTreatAnyHttpResponseAsWorking(
        int statusCode,
        DiagnosticState expectedState,
        ConnectivityState expectedConnectivity,
        bool expectedValidated)
    {
        var actual = ConnectivityTester.ClassifyHttpStatus(statusCode);

        Assert.Equal(expectedState, actual.State);
        Assert.Equal(expectedConnectivity, actual.Connectivity);
        Assert.Equal(expectedValidated, actual.ServiceValidated);
    }

    [Fact]
    public void ServiceSummary_TreatsTransportOnlyAsPartialNotWorking()
    {
        DiagnosticResult[] results =
        [
            new("telegram-desktop", "Telegram Desktop", DiagnosticState.Success, "TCP connected",
                Connectivity: ConnectivityState.TransportOnly, TransportReachable: true)
        ];

        var summary = ServiceTargetCatalog.SummarizeTargets(results, [ServiceTargetCatalog.Telegram]);

        Assert.Equal(ServiceReachability.Degraded, summary[ServiceTargetCatalog.Telegram]);
    }

    [Fact]
    public void ClassicReferenceAlt3_DeclaresEveryRequiredReferenceResource()
    {
        Assert.Equal("general--alt3", ClassicReferenceProfile.Alt3StrategyId);
        Assert.Contains("winws.exe", ClassicReferenceProfile.RequiredEngineFiles);
        Assert.Contains("WinDivert.dll", ClassicReferenceProfile.RequiredEngineFiles);
        Assert.Contains("WinDivert64.sys", ClassicReferenceProfile.RequiredEngineFiles);
        Assert.Contains("ACTIVE_DISCORD_UDP.bin", ClassicReferenceProfile.RequiredEngineFiles);
        Assert.Contains("list-google.txt", ClassicReferenceProfile.RequiredListFiles);
        Assert.Contains("ipset-exclude-user.txt", ClassicReferenceProfile.RequiredListFiles);
    }

    [Fact]
    public void ServiceStrategyCache_IsolatedByNetworkAndService()
    {
        var older = new ServiceStrategyResult("youtube", EngineType.Classic, "ALT3", ["--filter-tcp=443"], "tls",
            DateTimeOffset.Parse("2026-01-01T00:00:00Z"), "network-a", 80);
        var winner = older with { StrategyFamily = "ALT7", LastValidated = older.LastValidated.AddDays(1), SuccessScore = 95 };
        var anotherNetwork = older with { NetworkFingerprint = "network-b", StrategyFamily = "ALT2", SuccessScore = 100 };

        var cached = ServiceStrategyCache.ForNetwork([older, winner, anotherNetwork], "network-a");

        Assert.Equal("ALT7", cached["youtube"].StrategyFamily);
        Assert.DoesNotContain(ServiceStrategyCache.ForNetwork([older, winner, anotherNetwork], "network-a").Values,
            item => item.NetworkFingerprint == "network-b");
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
    public async Task StrategyStore_FallsBackToPreviousValidatedDatabase()
    {
        var directory = Path.Combine(Path.GetTempPath(), "RaffertyTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "strategies.json");
            await File.WriteAllTextAsync(path, "{broken-json");
            await File.WriteAllTextAsync(path + ".previous", """
                {"schemaVersion":1,"source":"rollback","updatedAt":"2026-01-01T00:00:00Z","strategies":[
                  {"id":"general","name":"general","description":"","targets":[],"protocols":["tcp"],"ports":[443],"arguments":["--filter-tcp=443"]}
                ]}
                """);

            var database = await new StrategyStore(path).LoadAsync();

            Assert.Equal("rollback", database.Source);
            Assert.Equal("general", Assert.Single(database.Strategies).Id);
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
    public async Task UpdateService_UsesGitHubReleaseAssetAndAcceptsVTag()
    {
        var hash = new string('a', 64);
        var release = $$"""
            {"tag_name":"v1.4.1","html_url":"https://github.com/example/releases/tag/v1.4.1","body":"Fixes","assets":[
              {"name":"Rafferty.exe","browser_download_url":"https://github.com/example/releases/download/v1.4.1/Rafferty.exe","digest":"sha256:{{hash}}"}
            ]}
            """;
        using var client = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(release) }));
        using var service = new UpdateService(client);
        var log = new List<string>();
        service.Diagnostic += log.Add;

        var result = await service.CheckAsync("https://api.github.com/repos/example/app/releases/latest", new Version(1, 4, 0));

        Assert.True(result.UpdateAvailable);
        Assert.Equal(new Version(1, 4, 1), result.LatestVersion);
        Assert.EndsWith("/v1.4.1/Rafferty.exe", result.Manifest.DownloadUrl);
        Assert.Contains(log, line => line.StartsWith("Update check URL:"));
        Assert.Contains(log, line => line.StartsWith("Release URL:"));
        Assert.Contains(log, line => line.StartsWith("Asset URL:"));
    }

    [Fact]
    public async Task UpdateService_ReportsMissingAssetClearly()
    {
        const string release = """
            {"tag_name":"v1.4.1","html_url":"https://github.com/example/releases/tag/v1.4.1","body":"Fixes","assets":[
              {"name":"checksums.txt","browser_download_url":"https://github.com/example/checksums.txt","digest":"sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}
            ]}
            """;
        using var client = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(release) }));
        using var service = new UpdateService(client);

        var error = await Assert.ThrowsAsync<UpdateAssetNotFoundException>(() =>
            service.CheckAsync("https://api.github.com/repos/example/app/releases/latest"));

        Assert.Equal("В релизе v1.4.1 не найден файл Rafferty.exe.", error.Message);
    }

    [Fact]
    public async Task UpdateService_DoesNotRetryHttp404()
    {
        var requests = 0;
        using var client = new HttpClient(new StubHandler(_ =>
        {
            requests++;
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));
        using var service = new UpdateService(client);

        await Assert.ThrowsAsync<UpdateRequestException>(() =>
            service.CheckAsync("https://api.github.com/repos/example/app/releases/latest"));

        Assert.Equal(1, requests);
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
