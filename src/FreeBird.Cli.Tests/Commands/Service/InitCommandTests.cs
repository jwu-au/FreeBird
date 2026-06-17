using System;
using System.IO;
using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using FluentAssertions;
using FreeBird.Cli.Commands.Service;
using FreeBird.Core.Service;
using Moq;
using Serilog;
using Xunit;

namespace FreeBird.Cli.Tests.Commands.Service;

/// <summary>
/// T17 — Behaviour tests for <see cref="InitCommand.Handle"/>, the OS-agnostic core
/// of <c>fb service init</c>.
///
/// <para>The handler writes the §4 default JSON config template to a target path:
/// a null/empty <c>outputPath</c> expands to
/// <c>&lt;CommonApplicationData&gt;/FreeBird/config.json</c>; <c>--output</c> overrides it;
/// <c>--force</c> overwrites an existing file. Exit codes mirror design: 0 success,
/// 1 file exists without <c>--force</c>, 3 I/O error.</para>
///
/// <para>All file I/O goes through an injected <see cref="IFileSystem"/> so every branch
/// is unit-testable on any OS with a <see cref="MockFileSystem"/> — nothing real is
/// written, and the real <c>%ProgramData%</c> is never touched.</para>
///
/// <para>The default-path branch is exercised with a <see cref="MockFileSystem"/> too:
/// <see cref="Environment.GetFolderPath"/> for <see cref="Environment.SpecialFolder.CommonApplicationData"/>
/// is combined with <c>FreeBird/config.json</c> via the injected <c>fs.Path.Combine</c>,
/// so the test asserts only the platform-agnostic suffix. On some sandboxes
/// CommonApplicationData is the empty string; the assertion still holds because the
/// suffix <c>FreeBird/config.json</c> is unaffected.</para>
/// </summary>
public class InitCommandTests
{
    private const string SampleInput =
        @"C:\Users\<you>\AppData\Local\NetEase\CloudMusic\Cache\Cache";
    private const string SampleOutput = @"D:\Music\NetEase";

    private static ILogger SilentLogger() => new LoggerConfiguration().CreateLogger();

    private static string ExpectedTemplate() =>
        DefaultConfigTemplate.Render(SampleInput, SampleOutput);

    [Fact]
    public void Handle_NoOutputPath_WritesToCommonApplicationDataDefault_Exit0()
    {
        var fs = new MockFileSystem();
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = InitCommand.Handle(outputPath: null, force: false, fs: fs, stdout: stdout, stderr: stderr);

        exit.Should().Be(0);

        // Separator-agnostic suffix assertion (CommonApplicationData may be empty on sandboxes).
        var expectedSuffix = fs.Path.Combine("FreeBird", "config.json");
        var written = fs.AllFiles.Should().ContainSingle().Subject;
        written.Should().EndWith(expectedSuffix);
        fs.File.ReadAllText(written).Should().Be(ExpectedTemplate());
    }

    [Fact]
    public void Handle_WithOutputPath_WritesToThatPath_Exit0_StdoutContainsPath()
    {
        var fs = new MockFileSystem();
        var target = fs.Path.Combine("output", "my-config.json");
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = InitCommand.Handle(outputPath: target, force: false, fs: fs, stdout: stdout, stderr: stderr);

        exit.Should().Be(0);
        fs.File.Exists(target).Should().BeTrue();
        fs.File.ReadAllText(target).Should().Be(ExpectedTemplate());
        stdout.ToString().Should().Contain(target);
    }

    [Fact]
    public void Handle_TargetExists_NoForce_Exit1_StderrMentionsForce_ContentUnchanged()
    {
        const string sentinel = "DO NOT CLOBBER ME";
        var fs = new MockFileSystem();
        var target = fs.Path.Combine("output", "config.json");
        fs.AddFile(target, new MockFileData(sentinel));
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = InitCommand.Handle(outputPath: target, force: false, fs: fs, stdout: stdout, stderr: stderr);

        exit.Should().Be(1);
        stderr.ToString().Should().Contain("--force");
        fs.File.ReadAllText(target).Should().Be(sentinel, "the existing file must be left untouched");
    }

    [Fact]
    public void Handle_TargetExists_Force_Overwrites_Exit0()
    {
        const string sentinel = "old content";
        var fs = new MockFileSystem();
        var target = fs.Path.Combine("output", "config.json");
        fs.AddFile(target, new MockFileData(sentinel));
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = InitCommand.Handle(outputPath: target, force: true, fs: fs, stdout: stdout, stderr: stderr);

        exit.Should().Be(0);
        fs.File.ReadAllText(target).Should().Be(ExpectedTemplate());
    }

    [Fact]
    public void Handle_IoErrorDuringWrite_Exit3_StderrNamesPath()
    {
        const string target = "/protected/config.json";

        // Moq IFileSystem where directory creation throws IOException, so the
        // write cannot proceed. The handler must map this to exit 3 and name the path.
        var dirMock = new Mock<IDirectory>();
        dirMock
            .Setup(d => d.CreateDirectory(It.IsAny<string>()))
            .Throws(new IOException("disk is read-only"));

        var fileMock = new Mock<IFile>();
        fileMock.Setup(f => f.Exists(target)).Returns(false);

        var pathMock = new Mock<IPath>();
        pathMock.Setup(p => p.GetDirectoryName(target)).Returns("/protected");

        var fsMock = new Mock<IFileSystem>();
        fsMock.SetupGet(fs => fs.Directory).Returns(dirMock.Object);
        fsMock.SetupGet(fs => fs.File).Returns(fileMock.Object);
        fsMock.SetupGet(fs => fs.Path).Returns(pathMock.Object);

        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = InitCommand.Handle(outputPath: target, force: false, fs: fsMock.Object, stdout: stdout, stderr: stderr);

        exit.Should().Be(3);
        stderr.ToString().Should().Contain(target, "the I/O error message must name the target path");
    }

    [Fact]
    public void Handle_WrittenContent_RoundTripsThroughJsonConfigLoader()
    {
        var fs = new MockFileSystem();
        var target = fs.Path.Combine("output", "config.json");
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = InitCommand.Handle(outputPath: target, force: false, fs: fs, stdout: stdout, stderr: stderr);
        exit.Should().Be(0);

        var written = fs.File.ReadAllText(target);
        var loader = new JsonConfigLoader(SilentLogger());

        var config = loader.LoadFromJson(written);

        config.Watch.Flac.Should().BeNull("the flac block is //-commented in the template");
        config.Watch.ApiRateLimit.Should().BeNull("the api_rate_limit line is //-commented in the template");
    }
}
