using FluentAssertions;
using FreeBird.Core.Provisioning;
using Moq;
using Serilog;

namespace FreeBird.Core.Tests.Provisioning;

public class NoOpFlacAutoInstallerTests
{
    private static NoOpFlacAutoInstaller Build() => new(new Mock<ILogger>().Object);

    [Fact]
    public async Task InstallAsync_AlwaysReturnsNotSupported()
    {
        var installer = Build();
        var result = await installer.InstallAsync("/some/dir", "https://example/flac.zip", CancellationToken.None);
        result.Should().BeOfType<FlacInstallResult.NotSupported>();
        result.Should().BeSameAs(FlacInstallResult.NotSupported.Instance);
    }

    [Fact]
    public async Task InstallAsync_EmptyArgs_StillReturnsNotSupported()
    {
        var installer = Build();
        (await installer.InstallAsync("", "", CancellationToken.None)).Should().BeSameAs(FlacInstallResult.NotSupported.Instance);
    }

    [Fact]
    public async Task InstallAsync_NullArgs_StillReturnsNotSupported_NoNRE()
    {
        var installer = Build();
        (await installer.InstallAsync(null!, null!, CancellationToken.None)).Should().BeSameAs(FlacInstallResult.NotSupported.Instance);
    }

    [Fact]
    public async Task InstallAsync_AlreadyCancelled_Throws()
    {
        var installer = Build();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var act = async () => await installer.InstallAsync("/x", "https://x", cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void Ctor_NullLogger_Throws()
    {
        Action act = () => new NoOpFlacAutoInstaller(null!);
        act.Should().Throw<ArgumentNullException>().WithParameterName("log");
    }

    [Fact]
    public async Task InstallAsync_PerformsZeroNetworkIO()
    {
        // No way to spy on socket IO without elaborate mock infra; the assertion here is
        // that the impl class has NO HttpClient dependency (verified by reflection on ctor params).
        var ctors = typeof(NoOpFlacAutoInstaller).GetConstructors();
        ctors.Should().HaveCount(1);
        var paramTypes = ctors[0].GetParameters().Select(p => p.ParameterType).ToArray();
        paramTypes.Should().NotContain(t => t.Name.Contains("HttpClient", StringComparison.OrdinalIgnoreCase));
        paramTypes.Should().NotContain(t => t.Name.Contains("IHttpDownloader", StringComparison.OrdinalIgnoreCase));
        await Task.CompletedTask;  // satisfy async signature
    }

    [Fact]
    public async Task InstallAsync_PerformsZeroFileSystemIO()
    {
        // Similarly assert no IFileSystem / System.IO dependency in constructor
        var ctors = typeof(NoOpFlacAutoInstaller).GetConstructors();
        var paramTypes = ctors[0].GetParameters().Select(p => p.ParameterType).ToArray();
        paramTypes.Should().NotContain(t => t.Name.Contains("FileSystem", StringComparison.OrdinalIgnoreCase));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task InstallAsync_Idempotent_MultipleCalls()
    {
        var installer = Build();
        for (int i = 0; i < 5; i++)
        {
            var r = await installer.InstallAsync("/x", "https://x", CancellationToken.None);
            r.Should().BeSameAs(FlacInstallResult.NotSupported.Instance);
        }
    }
}
