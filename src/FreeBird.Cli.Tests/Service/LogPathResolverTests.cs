using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using System.Linq;
using FluentAssertions;
using FreeBird.Cli.Service;
using FreeBird.Core.Abstractions;
using FreeBird.Core.DependencyInjection;
using Moq;
using Xunit;

namespace FreeBird.Cli.Tests.Service;

/// <summary>
/// T13b — Contract + behaviour tests for <see cref="ILogPathResolver"/> and the
/// default <see cref="LogPathResolver"/> implementation.
///
/// The resolver implements the design §2.4 log-file fallback rule in a pure,
/// OS-agnostic way: it probes the requested path's writability via the injected
/// <see cref="IFileSystem"/> and, when unwritable, returns the
/// <c>%ProgramData%\FreeBird\logs\freebird-service-.log</c> default with
/// <see cref="LogPathResolution.FellBack"/> set. Emitting the Event-Log WARNING
/// is the caller's job (T22 RunCommand / T24) — the resolver never touches the
/// Event Log so it stays unit-testable on every OS via a mock filesystem.
///
/// These tests pin down:
///   (1) writable requested path → returned verbatim, no fallback;
///   (2) null/empty/whitespace requested path → ProgramData default, no fallback;
///   (3) missing parent directory → fallback, Reason names the missing dir;
///   (4) permission-denied probe (Moq IFileSystem) → fallback, Reason names the error;
///   (5) <see cref="ILogPathResolver"/> extends <see cref="IDependency"/> for DI;
///   (6) the resolver type is sealed and <see cref="LogPathResolution"/> is a
///       value-equality record;
///   (7) no concrete <see cref="ILogPathResolver"/> implementation lives in Core.
/// </summary>
public class LogPathResolverTests
{
    private const string ProgramDataLogSuffix = "freebird-service-.log";

    [Fact]
    public void Resolve_WritablePath_ReturnsRequested_NoFallback()
    {
        const string requested = "/var/log/freebird/freebird-service-.log";
        IFileSystem fs = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            ["/var/log/freebird/existing.log"] = new MockFileData(""),
        });
        var sut = new LogPathResolver(fs);

        var result = sut.Resolve(requested);

        result.FinalPath.Should().Be(requested);
        result.FellBack.Should().BeFalse();
        result.Reason.Should().BeNull();
    }

    [Fact]
    public void Resolve_NullPath_ReturnsProgramDataDefault_NoFallback()
    {
        IFileSystem fs = new MockFileSystem();
        var sut = new LogPathResolver(fs);

        var result = sut.Resolve(null);

        result.FinalPath.Should().EndWith(ProgramDataLogSuffix);
        result.FinalPath.Should().Contain("FreeBird");
        result.FinalPath.Should().Contain("logs");
        result.FellBack.Should().BeFalse("a null requested path uses the intended default, not a fallback");
        result.Reason.Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_EmptyOrWhitespacePath_ReturnsProgramDataDefault_NoFallback(string requested)
    {
        IFileSystem fs = new MockFileSystem();
        var sut = new LogPathResolver(fs);

        var result = sut.Resolve(requested);

        result.FinalPath.Should().EndWith(ProgramDataLogSuffix);
        result.FellBack.Should().BeFalse();
        result.Reason.Should().BeNull();
    }

    [Fact]
    public void Resolve_MissingDirectory_FallsBack_ReasonNamesDir()
    {
        const string requested = "/does/not/exist/freebird-service-.log";
        IFileSystem fs = new MockFileSystem(); // empty: parent dir absent
        var sut = new LogPathResolver(fs);

        var result = sut.Resolve(requested);

        result.FellBack.Should().BeTrue();
        result.FinalPath.Should().EndWith(ProgramDataLogSuffix);
        result.FinalPath.Should().NotBe(requested);
        result.Reason.Should().NotBeNull();
        result.Reason.Should().Contain("/does/not/exist",
            "the fallback reason must name the missing directory for operator diagnosis");
    }

    [Fact]
    public void Resolve_PermissionDenied_FallsBack_ReasonNamesError()
    {
        const string requested = "/protected/freebird-service-.log";

        // MockFileSystem cannot simulate permission denial, so use Moq to make
        // the write probe throw UnauthorizedAccessException while the parent
        // directory reports as existing.
        var fileMock = new Mock<IFile>();
        fileMock
            .Setup(f => f.Open(requested, FileMode.Append, FileAccess.Write))
            .Throws(new UnauthorizedAccessException("Access to the path '/protected' is denied."));

        var dirMock = new Mock<IDirectory>();
        dirMock.Setup(d => d.Exists("/protected")).Returns(true);

        var pathMock = new Mock<IPath>();
        pathMock.Setup(p => p.GetDirectoryName(requested)).Returns("/protected");
        pathMock
            .Setup(p => p.Combine(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns<string, string, string, string>((a, b, c, d) => string.Join("/", a, b, c, d));

        var fsMock = new Mock<IFileSystem>();
        fsMock.SetupGet(fs => fs.File).Returns(fileMock.Object);
        fsMock.SetupGet(fs => fs.Directory).Returns(dirMock.Object);
        fsMock.SetupGet(fs => fs.Path).Returns(pathMock.Object);

        var sut = new LogPathResolver(fsMock.Object);

        var result = sut.Resolve(requested);

        result.FellBack.Should().BeTrue();
        result.FinalPath.Should().EndWith(ProgramDataLogSuffix);
        result.Reason.Should().NotBeNull();
        result.Reason.Should().Contain("denied",
            "the fallback reason must surface the access error for operator diagnosis");
    }

    [Fact]
    public void Resolve_NullRequested_ThrowsNothing_AndConstructorRejectsNullFileSystem()
    {
        var act = () => new LogPathResolver(null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("fs");
    }

    [Fact]
    public void ILogPathResolver_ExtendsIDependency()
    {
        typeof(IDependency).IsAssignableFrom(typeof(ILogPathResolver))
            .Should().BeTrue("ILogPathResolver must be DI-discoverable via the IDependency marker (registered by T14)");
    }

    [Fact]
    public void LogPathResolver_IsSealed()
    {
        typeof(LogPathResolver).IsSealed
            .Should().BeTrue("the resolver has no extension points");
    }

    [Fact]
    public void LogPathResolution_IsRecord_WithValueEquality()
    {
        var a = new LogPathResolution("/x/y.log", FellBack: true, Reason: "boom");
        var b = new LogPathResolution("/x/y.log", FellBack: true, Reason: "boom");

        a.Should().Be(b, "LogPathResolution is a record and must use value equality");
        a.GetHashCode().Should().Be(b.GetHashCode());
    }

    [Fact]
    public void ILogPathResolver_HasNoConcreteImplementation_InCoreAssembly()
    {
        var coreAssembly = typeof(CoreModule).Assembly;
        var offenders = coreAssembly.GetTypes()
            .Where(t => typeof(ILogPathResolver).IsAssignableFrom(t)
                        && !t.IsInterface
                        && !t.IsAbstract)
            .ToArray();

        offenders.Should().BeEmpty(
            "T13b forbids any ILogPathResolver implementation in FreeBird.Core (B-5/B-6 invariant)");
    }
}
