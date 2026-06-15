using System.Reflection;
using FluentAssertions;
using FreeBird.Core.Service;
using Xunit;

namespace FreeBird.Core.Tests.Service;

public class ServiceConfigDefaultsTests
{
    // --- ConfigDefaults: byte-for-byte default values per design §2.7 ---

    [Fact]
    public void DefaultServiceName_IsFreeBird()
    {
        ConfigDefaults.DefaultServiceName.Should().Be("FreeBird");
    }

    [Fact]
    public void DefaultDisplayName_IsExact()
    {
        ConfigDefaults.DefaultDisplayName.Should().Be("FreeBird Music Cache Decoder");
    }

    [Fact]
    public void DefaultDescription_IsExact()
    {
        ConfigDefaults.DefaultDescription.Should().Be(
            "Continuously decrypts NetEase Cloud Music cache files into playable audio files. https://github.com/jwu-au/FreeBird");
    }

    [Fact]
    public void DefaultRestartPolicy_IsBackoff()
    {
        ConfigDefaults.DefaultRestartPolicy.Should().Be(RestartPolicy.Backoff);
    }

    [Fact]
    public void DefaultLogFileTemplate_IsExact()
    {
        ConfigDefaults.DefaultLogFileTemplate.Should().Be(@"%ProgramData%\FreeBird\logs\watch-.log");
    }

    [Fact]
    public void DefaultConfigPath_IsExact()
    {
        ConfigDefaults.DefaultConfigPath.Should().Be(@"%ProgramData%\FreeBird\config.json");
    }

    // --- RestartPolicy enum: required members ---

    [Fact]
    public void RestartPolicy_HasManualRestartBackoffMembers()
    {
        Enum.IsDefined(typeof(RestartPolicy), RestartPolicy.Manual).Should().BeTrue();
        Enum.IsDefined(typeof(RestartPolicy), RestartPolicy.Restart).Should().BeTrue();
        Enum.IsDefined(typeof(RestartPolicy), RestartPolicy.Backoff).Should().BeTrue();
    }

    // --- IntegrityChoice enum: required members ---

    [Fact]
    public void IntegrityChoice_HasOffL1L3AutoMembers()
    {
        Enum.IsDefined(typeof(IntegrityChoice), IntegrityChoice.Off).Should().BeTrue();
        Enum.IsDefined(typeof(IntegrityChoice), IntegrityChoice.L1).Should().BeTrue();
        Enum.IsDefined(typeof(IntegrityChoice), IntegrityChoice.L3).Should().BeTrue();
        Enum.IsDefined(typeof(IntegrityChoice), IntegrityChoice.Auto).Should().BeTrue();
    }

    // --- RootConfig defaults ---

    [Fact]
    public void RootConfig_DefaultConstructed_HasNonNullWatchAndService()
    {
        var root = new RootConfig();
        root.Watch.Should().NotBeNull();
        root.Service.Should().NotBeNull();
        root.Schema.Should().BeNull();
    }

    [Fact]
    public void RootConfig_HasNullableSchemaProperty()
    {
        var prop = typeof(RootConfig).GetProperty("Schema");
        prop.Should().NotBeNull("RootConfig must expose a Schema property to tolerate $schema");
        prop!.PropertyType.Should().Be(typeof(string));
    }

    // --- WatchConfig defaults (schema-shape + values) ---

    [Fact]
    public void WatchConfig_DefaultConstructed_HasDocumentedDefaults()
    {
        var watch = new WatchConfig();
        watch.Inputs.Should().BeEmpty();
        watch.Output.Should().Be("");
        watch.Integrity.Should().Be(IntegrityChoice.Auto);
        watch.Concurrency.Should().Be(4);
        watch.ApiConcurrency.Should().Be(4);
        watch.PollInterval.Should().Be("10s");
        watch.StabilityChecks.Should().Be(2);
        watch.MinFileSize.Should().Be(1024);
        watch.WriteTags.Should().BeTrue();
        watch.LogFile.Should().BeNull();
    }

    // --- Round 5 M-4: WatchConfig.Flac + WatchConfig.ApiRateLimit ---

    [Fact]
    public void WatchConfig_HasNullableFlacProperty_OfFlacConfig_Round5M4()
    {
        var prop = typeof(WatchConfig).GetProperty("Flac");
        prop.Should().NotBeNull("Round 5 M-4 requires WatchConfig.Flac");
        prop!.PropertyType.Should().Be(typeof(FlacConfig));
    }

    [Fact]
    public void WatchConfig_HasNullableApiRateLimitProperty_OfDouble_Round5M4()
    {
        var prop = typeof(WatchConfig).GetProperty("ApiRateLimit");
        prop.Should().NotBeNull("Round 5 M-4 requires WatchConfig.ApiRateLimit");
        prop!.PropertyType.Should().Be(typeof(double?));
    }

    [Fact]
    public void WatchConfig_DefaultConstructed_FlacIsNull_Round5M4()
    {
        var watch = new WatchConfig();
        watch.Flac.Should().BeNull("omitted in JSON means absent in DTO");
    }

    [Fact]
    public void WatchConfig_DefaultConstructed_ApiRateLimitIsNull_Round5M4()
    {
        var watch = new WatchConfig();
        watch.ApiRateLimit.Should().BeNull("omitted in JSON means absent in DTO");
    }

    // --- FlacConfig type exists with required init-only props (Round 5 M-4) ---

    [Fact]
    public void FlacConfig_TypeExists_AndIsSealedRecord_Round5M4()
    {
        var t = typeof(FlacConfig);
        t.IsSealed.Should().BeTrue("DTOs must be sealed records per AGENTS.md §5");
    }

    [Fact]
    public void FlacConfig_HasBinUrlDisableAutoInstallProps_Round5M4()
    {
        var t = typeof(FlacConfig);
        var bin = t.GetProperty("Bin");
        var url = t.GetProperty("Url");
        var disable = t.GetProperty("DisableAutoInstall");
        bin.Should().NotBeNull();
        url.Should().NotBeNull();
        disable.Should().NotBeNull();
        bin!.PropertyType.Should().Be(typeof(string));
        url!.PropertyType.Should().Be(typeof(string));
        disable!.PropertyType.Should().Be(typeof(bool));
    }

    [Fact]
    public void FlacConfig_PropertiesAreInitOnly_Round5M4()
    {
        var t = typeof(FlacConfig);
        foreach (var name in new[] { "Bin", "Url", "DisableAutoInstall" })
        {
            var prop = t.GetProperty(name);
            prop.Should().NotBeNull();
            var setter = prop!.SetMethod;
            setter.Should().NotBeNull($"{name} must have a setter");
            // init-only setters carry the `IsExternalInit` modreq on their return type
            var modreqs = setter!.ReturnParameter.GetRequiredCustomModifiers();
            modreqs.Should().Contain(typeof(System.Runtime.CompilerServices.IsExternalInit),
                $"{name} must be init-only");
        }
    }

    [Fact]
    public void FlacConfig_DefaultConstructed_HasExpectedDefaults_Round5M4()
    {
        var flac = new FlacConfig();
        flac.Bin.Should().BeNull();
        flac.Url.Should().BeNull();
        flac.DisableAutoInstall.Should().BeFalse();
    }

    // --- ServiceConfig defaults wire through ConfigDefaults ---

    [Fact]
    public void ServiceConfig_DefaultConstructed_UsesConfigDefaults()
    {
        var svc = new ServiceConfig();
        svc.Name.Should().Be(ConfigDefaults.DefaultServiceName);
        svc.DisplayName.Should().Be(ConfigDefaults.DefaultDisplayName);
        svc.Description.Should().Be(ConfigDefaults.DefaultDescription);
        svc.RestartPolicy.Should().Be(ConfigDefaults.DefaultRestartPolicy);
        svc.LogToEventLog.Should().BeTrue();
    }

    // --- All DTOs are sealed records (AGENTS.md §5) ---

    [Theory]
    [InlineData(typeof(RootConfig))]
    [InlineData(typeof(WatchConfig))]
    [InlineData(typeof(ServiceConfig))]
    [InlineData(typeof(FlacConfig))]
    public void Dto_IsSealedRecord(Type dtoType)
    {
        dtoType.IsSealed.Should().BeTrue($"{dtoType.Name} must be sealed");
        // Records have a compiler-generated `EqualityContract` property
        dtoType.GetProperty("EqualityContract", BindingFlags.NonPublic | BindingFlags.Instance)
            .Should().NotBeNull($"{dtoType.Name} must be a record");
    }
}
