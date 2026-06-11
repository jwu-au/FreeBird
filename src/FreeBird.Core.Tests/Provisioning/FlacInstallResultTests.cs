using FluentAssertions;
using FreeBird.Core.Provisioning;

namespace FreeBird.Core.Tests.Provisioning;

public class FlacInstallResultTests
{
    [Fact]
    public void Installed_HoldsPaths()
    {
        var r = new FlacInstallResult.Installed("/x/flac", "/x/metaflac");
        r.FlacPath.Should().Be("/x/flac");
        r.MetaflacPath.Should().Be("/x/metaflac");
    }

    [Fact]
    public void NotSupported_IsSingleton()
    {
        FlacInstallResult.NotSupported.Instance.Should().BeSameAs(FlacInstallResult.NotSupported.Instance);
    }

    [Fact]
    public void Failed_HoldsReason()
    {
        new FlacInstallResult.Failed("net down").Reason.Should().Be("net down");
    }

    [Fact]
    public void Disabled_IsSingleton()
    {
        FlacInstallResult.Disabled.Instance.Should().BeSameAs(FlacInstallResult.Disabled.Instance);
    }

    [Fact]
    public void PatternMatch_AllCases_DispatchesCorrectly()
    {
        static string Describe(FlacInstallResult r) => r switch
        {
            FlacInstallResult.Installed i => $"ok:{i.FlacPath}",
            FlacInstallResult.NotSupported => "not-supported",
            FlacInstallResult.Failed f => $"fail:{f.Reason}",
            FlacInstallResult.Disabled => "disabled",
            _ => throw new InvalidOperationException("Unhandled")
        };
        Describe(new FlacInstallResult.Installed("/f", "/m")).Should().Be("ok:/f");
        Describe(FlacInstallResult.NotSupported.Instance).Should().Be("not-supported");
        Describe(new FlacInstallResult.Failed("x")).Should().Be("fail:x");
        Describe(FlacInstallResult.Disabled.Instance).Should().Be("disabled");
    }
}
