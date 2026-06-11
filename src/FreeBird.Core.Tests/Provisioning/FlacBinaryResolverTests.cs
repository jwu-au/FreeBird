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
    }

    private static FlacBinaryResolver Build(Action<MockSetup>? configure = null)
    {
        var setup = new MockSetup();
        configure?.Invoke(setup);

        IFileSystem fs = new MockFileSystem(setup.Files);

        var pathEnv = new Mock<IPathEnvironment>();
        pathEnv
            .Setup(p => p.FindOnPath(It.IsAny<string>()))
            .Returns((string exeName) => setup.PathHits.TryGetValue(exeName, out var hit) ? hit : null);

        var options = new FlacResolverOptions
        {
            AppBaseDirectory = setup.BaseDir,
            FlacBinOverride = setup.FlacOverride,
            MetaflacBinOverride = setup.MetaflacOverride,
        };

        var log = new Mock<ILogger>().Object;
        return new FlacBinaryResolver(fs, pathEnv.Object, options, log);
    }

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
