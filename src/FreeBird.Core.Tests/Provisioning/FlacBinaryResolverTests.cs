using System;
using System.Collections.Generic;
using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FreeBird.Core.Provisioning;
using Moq;
using Serilog;

namespace FreeBird.Core.Tests.Provisioning;

public class FlacBinaryResolverTests
{
    private sealed class MockSetup
    {
        public Dictionary<string, MockFileData> Files = new();
        public string BaseDir = "/app";
        public string? FlacOverride;
        public string? MetaflacOverride;
        public Dictionary<string, string> PathHits = new();
        public string? AutoInstallUrl;
        public bool DisableAutoInstall;
        public Action<Mock<IFlacAutoInstaller>>? ConfigureInstaller;
    }

    private static (FlacBinaryResolver resolver, Mock<IFlacAutoInstaller> installer) BuildWithInstaller(Action<MockSetup>? configure = null)
    {
        var setup = new MockSetup();
        configure?.Invoke(setup);

        IFileSystem fs = new MockFileSystem(setup.Files);

        var pathEnv = new Mock<IPathEnvironment>();
        pathEnv
            .Setup(p => p.FindOnPath(It.IsAny<string>()))
            .Returns((string exeName) => setup.PathHits.TryGetValue(exeName, out var hit) ? hit : null);

        var installer = new Mock<IFlacAutoInstaller>();
        // Default: behave like NoOp (NotSupported) so existing probe-order tests don't accidentally trigger install.
        installer
            .Setup(i => i.InstallAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(FlacInstallResult.NotSupported.Instance);
        setup.ConfigureInstaller?.Invoke(installer);

        var options = new FlacResolverOptions
        {
            AppBaseDirectory = setup.BaseDir,
            FlacBinOverride = setup.FlacOverride,
            MetaflacBinOverride = setup.MetaflacOverride,
            AutoInstallUrl = setup.AutoInstallUrl,
            DisableAutoInstall = setup.DisableAutoInstall,
        };

        var log = new Mock<ILogger>().Object;
        return (new FlacBinaryResolver(fs, pathEnv.Object, installer.Object, options, log), installer);
    }

    private static FlacBinaryResolver Build(Action<MockSetup>? configure = null)
        => BuildWithInstaller(configure).resolver;

    // ---------------- Probe order: flac ----------------

    [Fact]
    public async Task ResolveFlac_OverrideFound_WinsOverBesideAndPath()
    {
        var r = Build(s =>
        {
            s.FlacOverride = "/custom/flac";
            s.Files["/custom/flac"] = new MockFileData("");
            s.Files["/app/flac"] = new MockFileData("");
            s.PathHits["flac"] = "/usr/bin/flac";
        });

        var result = await r.ResolveFlacAsync(CancellationToken.None);

        result.Path.Should().Be("/custom/flac");
        result.Provenance.Should().Be(FlacBinaryProvenance.CliOverride);
    }

    [Fact]
    public async Task ResolveFlac_OverrideMissing_FallsBackToBeside()
    {
        var r = Build(s =>
        {
            s.FlacOverride = "/nonexistent/flac";
            s.Files["/app/flac"] = new MockFileData("");
        });

        var result = await r.ResolveFlacAsync(CancellationToken.None);

        result.Path.Should().Be("/app/flac");
        result.Provenance.Should().Be(FlacBinaryProvenance.NextToExecutable);
    }

    [Fact]
    public async Task ResolveFlac_OverrideMissing_FallsBackToPathWhenNoBeside()
    {
        var r = Build(s =>
        {
            s.FlacOverride = "/nonexistent/flac";
            s.PathHits["flac"] = "/usr/bin/flac";
        });

        var result = await r.ResolveFlacAsync(CancellationToken.None);

        result.Path.Should().Be("/usr/bin/flac");
        result.Provenance.Should().Be(FlacBinaryProvenance.Path);
    }

    [Fact]
    public async Task ResolveFlac_BesideWinsOverPath()
    {
        var r = Build(s =>
        {
            s.Files["/app/flac"] = new MockFileData("");
            s.PathHits["flac"] = "/usr/bin/flac";
        });

        var result = await r.ResolveFlacAsync(CancellationToken.None);

        result.Path.Should().Be("/app/flac");
        result.Provenance.Should().Be(FlacBinaryProvenance.NextToExecutable);
    }

    [Fact]
    public async Task ResolveFlac_OnlyPathHas_ReturnsPathProvenance()
    {
        var r = Build(s => { s.PathHits["flac"] = "/usr/bin/flac"; });

        var result = await r.ResolveFlacAsync(CancellationToken.None);

        result.Path.Should().Be("/usr/bin/flac");
        result.Provenance.Should().Be(FlacBinaryProvenance.Path);
    }

    [Fact]
    public async Task ResolveFlac_NothingFound_ReturnsNotFound()
    {
        var r = Build();

        var result = await r.ResolveFlacAsync(CancellationToken.None);

        result.IsAvailable.Should().BeFalse();
        result.Provenance.Should().Be(FlacBinaryProvenance.NotFound);
        result.Path.Should().BeNull();
    }

    // ---------------- Probe order: metaflac (independence) ----------------

    [Fact]
    public async Task ResolveMetaflac_HasOwnOverride_Independent()
    {
        var r = Build(s =>
        {
            s.MetaflacOverride = "/custom/metaflac";
            s.Files["/custom/metaflac"] = new MockFileData("");
        });

        var result = await r.ResolveMetaflacAsync(CancellationToken.None);

        result.Path.Should().Be("/custom/metaflac");
        result.Provenance.Should().Be(FlacBinaryProvenance.CliOverride);
    }

    [Fact]
    public async Task ResolveMetaflac_NoOverride_UsesBesideAndPath()
    {
        var r = Build(s =>
        {
            s.PathHits["metaflac"] = "/usr/bin/metaflac";
        });

        var result = await r.ResolveMetaflacAsync(CancellationToken.None);

        result.Path.Should().Be("/usr/bin/metaflac");
        result.Provenance.Should().Be(FlacBinaryProvenance.Path);
    }

    [Fact]
    public async Task ResolveMetaflac_FlacOverrideDoesNotLeakToMetaflac()
    {
        var r = Build(s =>
        {
            s.FlacOverride = "/custom/flac";
            s.Files["/custom/flac"] = new MockFileData("");
            // metaflac has no override and no other location
        });

        var result = await r.ResolveMetaflacAsync(CancellationToken.None);

        result.IsAvailable.Should().BeFalse();
        result.Provenance.Should().Be(FlacBinaryProvenance.NotFound);
    }

    [Fact]
    public async Task ResolveMetaflac_BesideFb_Wins()
    {
        var r = Build(s =>
        {
            s.Files["/app/metaflac"] = new MockFileData("");
            s.PathHits["metaflac"] = "/usr/bin/metaflac";
        });

        var result = await r.ResolveMetaflacAsync(CancellationToken.None);

        result.Path.Should().Be("/app/metaflac");
        result.Provenance.Should().Be(FlacBinaryProvenance.NextToExecutable);
    }

    // ---------------- Windows .exe suffix ----------------

    [SkippableFact]
    public async Task ResolveFlac_OnWindows_UsesDotExeSuffix()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows-only check");

        var r = Build(s => { s.Files["/app/flac.exe"] = new MockFileData(""); });

        var result = await r.ResolveFlacAsync(CancellationToken.None);

        result.Path.Should().EndWith("flac.exe");
        result.Provenance.Should().Be(FlacBinaryProvenance.NextToExecutable);
    }

    [SkippableFact]
    public async Task ResolveFlac_OnNonWindows_NoDotExeSuffix()
    {
        Skip.If(OperatingSystem.IsWindows(), "Non-Windows check");

        var r = Build(s =>
        {
            s.Files["/app/flac.exe"] = new MockFileData("");
            // No /app/flac present — should NOT match flac.exe on non-Windows
        });

        var result = await r.ResolveFlacAsync(CancellationToken.None);

        result.IsAvailable.Should().BeFalse();
    }

    // ---------------- Auto-install delegation (T03) ----------------

    [Fact]
    public async Task ResolveFlac_MissAndNoUrl_ReturnsNotFound_NoInstallAttempt()
    {
        var (r, installer) = BuildWithInstaller();  // no URL

        var result = await r.ResolveFlacAsync(CancellationToken.None);

        result.IsAvailable.Should().BeFalse();
        installer.Verify(
            i => i.InstallAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ResolveFlac_MissWithDisableFlag_ReturnsNotFound_NoInstallAttempt()
    {
        var (r, installer) = BuildWithInstaller(s =>
        {
            s.AutoInstallUrl = "https://x/flac.zip";
            s.DisableAutoInstall = true;
        });

        var result = await r.ResolveFlacAsync(CancellationToken.None);

        result.IsAvailable.Should().BeFalse();
        installer.Verify(
            i => i.InstallAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ResolveFlac_MissWithUrl_CallsInstaller_AndReturnsInstalledPath()
    {
        var (r, installer) = BuildWithInstaller(s =>
        {
            s.BaseDir = "/app";
            s.AutoInstallUrl = "https://x/flac.zip";
            s.ConfigureInstaller = mock => mock
                .Setup(i => i.InstallAsync("/app", "https://x/flac.zip", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new FlacInstallResult.Installed("/app/flac", "/app/metaflac"));
        });

        var result = await r.ResolveFlacAsync(CancellationToken.None);

        result.Path.Should().Be("/app/flac");
        result.Provenance.Should().Be(FlacBinaryProvenance.NextToExecutable);
        installer.Verify(
            i => i.InstallAsync("/app", "https://x/flac.zip", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ResolveMetaflac_MissWithUrl_InstallReturnsBothPaths_ReturnsMetaflacPath()
    {
        var (r, _) = BuildWithInstaller(s =>
        {
            s.AutoInstallUrl = "https://x/flac.zip";
            s.ConfigureInstaller = mock => mock
                .Setup(i => i.InstallAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new FlacInstallResult.Installed("/app/flac", "/app/metaflac"));
        });

        var result = await r.ResolveMetaflacAsync(CancellationToken.None);

        result.Path.Should().Be("/app/metaflac");
        result.Provenance.Should().Be(FlacBinaryProvenance.NextToExecutable);
    }

    [Fact]
    public async Task ResolveFlac_InstallNotSupported_ReturnsNotFound()
    {
        var (r, _) = BuildWithInstaller(s =>
        {
            s.AutoInstallUrl = "https://x/flac.zip";
            s.ConfigureInstaller = mock => mock
                .Setup(i => i.InstallAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(FlacInstallResult.NotSupported.Instance);
        });

        var result = await r.ResolveFlacAsync(CancellationToken.None);

        result.IsAvailable.Should().BeFalse();
    }

    [Fact]
    public async Task ResolveFlac_InstallFailed_ReturnsNotFound()
    {
        var (r, _) = BuildWithInstaller(s =>
        {
            s.AutoInstallUrl = "https://x/flac.zip";
            s.ConfigureInstaller = mock => mock
                .Setup(i => i.InstallAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new FlacInstallResult.Failed("network down"));
        });

        var result = await r.ResolveFlacAsync(CancellationToken.None);

        result.IsAvailable.Should().BeFalse();
    }

    [Fact]
    public async Task ResolveFlac_ProbeHits_NeverCallsInstaller()
    {
        var (r, installer) = BuildWithInstaller(s =>
        {
            s.Files["/app/flac"] = new MockFileData("");
            s.AutoInstallUrl = "https://x/flac.zip";  // even with URL, probe hit short-circuits
        });

        var result = await r.ResolveFlacAsync(CancellationToken.None);

        result.IsAvailable.Should().BeTrue();
        installer.Verify(
            i => i.InstallAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ResolveFlac_HonorsCancellation_PropagatesToInstaller()
    {
        using var cts = new CancellationTokenSource();
        var (r, installer) = BuildWithInstaller(s =>
        {
            s.AutoInstallUrl = "https://x/flac.zip";
            s.ConfigureInstaller = mock => mock
                .Setup(i => i.InstallAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(FlacInstallResult.Disabled.Instance);
        });
        cts.Cancel();

        await r.ResolveFlacAsync(cts.Token);

        installer.Verify(
            i => i.InstallAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.Is<CancellationToken>(c => c.IsCancellationRequested)),
            Times.Once);
    }
}

public class PathEnvironmentTests
{
    [Fact]
    public void FindOnPath_HitsFirstMatch()
    {
        var fs = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            ["/usr/bin/flac"] = new MockFileData(""),
            ["/usr/local/bin/flac"] = new MockFileData(""),
        });
        var prev = Environment.GetEnvironmentVariable("PATH");
        try
        {
            var sep = OperatingSystem.IsWindows() ? ';' : ':';
            Environment.SetEnvironmentVariable("PATH", $"/usr/bin{sep}/usr/local/bin");
            var env = new PathEnvironment(fs);
            env.FindOnPath("flac").Should().Be("/usr/bin/flac");
        }
        finally { Environment.SetEnvironmentVariable("PATH", prev); }
    }

    [Fact]
    public void FindOnPath_NotFound_ReturnsNull()
    {
        var fs = new MockFileSystem();
        var prev = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", "/empty");
            new PathEnvironment(fs).FindOnPath("flac").Should().BeNull();
        }
        finally { Environment.SetEnvironmentVariable("PATH", prev); }
    }

    [Fact]
    public void FindOnPath_EmptyPathEnv_ReturnsNull()
    {
        var prev = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", "");
            new PathEnvironment(new MockFileSystem()).FindOnPath("flac").Should().BeNull();
        }
        finally { Environment.SetEnvironmentVariable("PATH", prev); }
    }

    [Fact]
    public void FindOnPath_SkipsEmptySegments()
    {
        var fs = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            ["/usr/bin/flac"] = new MockFileData(""),
        });
        var prev = Environment.GetEnvironmentVariable("PATH");
        try
        {
            var sep = OperatingSystem.IsWindows() ? ';' : ':';
            // Empty segment between separators
            Environment.SetEnvironmentVariable("PATH", $"{sep}{sep}/usr/bin{sep}");
            new PathEnvironment(fs).FindOnPath("flac").Should().Be("/usr/bin/flac");
        }
        finally { Environment.SetEnvironmentVariable("PATH", prev); }
    }
}
