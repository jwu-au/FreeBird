using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FreeBird.Core.Abstractions;
using FreeBird.Core.Integrity;
using FreeBird.Core.Models;
using FreeBird.Core.Provisioning;
using Moq;

namespace FreeBird.Core.Tests.IntegrityChecks;

public class CompositeIntegrityCheckerTests
{
    private readonly Mock<IL1IntegrityChecker> _l1 = new();
    private readonly Mock<IL3IntegrityChecker> _l3 = new();
    private readonly Mock<IFlacProbe> _probe = new();
    private readonly CompositeIntegrityChecker _sut;

    private static readonly IntegrityResult L1Pass = IntegrityResult.Passed(IntegrityLevel.L1);
    private static readonly IntegrityResult L3Pass = IntegrityResult.Passed(IntegrityLevel.L3);

    public CompositeIntegrityCheckerTests()
    {
        _l1.Setup(c => c.CheckAsync(It.IsAny<string>(), It.IsAny<AudioFormat>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(L1Pass);
        _l3.Setup(c => c.CheckAsync(It.IsAny<string>(), It.IsAny<AudioFormat>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(L3Pass);
        _sut = new CompositeIntegrityChecker(_l1.Object, _l3.Object, _probe.Object);
    }

    // --- Off level: always Skipped ---

    [Theory]
    [InlineData(AudioFormat.Flac)]
    [InlineData(AudioFormat.Mp3)]
    [InlineData(AudioFormat.M4a)]
    [InlineData(AudioFormat.Unknown)]
    public async Task CheckAsync_OffLevel_ReturnsSkipped_NoUnderlyingCalled(AudioFormat fmt)
    {
        var r = await _sut.CheckAsync("file.x", fmt, IntegrityLevel.Off);
        r.Ok.Should().BeTrue();
        r.LevelApplied.Should().Be(IntegrityLevel.Off);
        _l1.Verify(c => c.CheckAsync(It.IsAny<string>(), It.IsAny<AudioFormat>(), It.IsAny<CancellationToken>()), Times.Never);
        _l3.Verify(c => c.CheckAsync(It.IsAny<string>(), It.IsAny<AudioFormat>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // --- L1 level: always L1 ---

    [Theory]
    [InlineData(AudioFormat.Flac)]
    [InlineData(AudioFormat.Mp3)]
    [InlineData(AudioFormat.M4a)]
    [InlineData(AudioFormat.Unknown)]
    public async Task CheckAsync_L1Level_AlwaysUsesL1(AudioFormat fmt)
    {
        var r = await _sut.CheckAsync("file.x", fmt, IntegrityLevel.L1);
        r.LevelApplied.Should().Be(IntegrityLevel.L1);
        _l1.Verify(c => c.CheckAsync("file.x", fmt, It.IsAny<CancellationToken>()), Times.Once);
        _l3.Verify(c => c.CheckAsync(It.IsAny<string>(), It.IsAny<AudioFormat>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // --- L3 level: FLAC -> L3, else L1 ---

    [Fact]
    public async Task CheckAsync_L3Level_FlacFormat_UsesL3()
    {
        var r = await _sut.CheckAsync("x.flac", AudioFormat.Flac, IntegrityLevel.L3);
        r.LevelApplied.Should().Be(IntegrityLevel.L3);
        _l3.Verify(c => c.CheckAsync("x.flac", AudioFormat.Flac, It.IsAny<CancellationToken>()), Times.Once);
        _l1.Verify(c => c.CheckAsync(It.IsAny<string>(), It.IsAny<AudioFormat>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(AudioFormat.Mp3)]
    [InlineData(AudioFormat.M4a)]
    [InlineData(AudioFormat.Unknown)]
    public async Task CheckAsync_L3Level_NonFlacFormat_FallsBackToL1(AudioFormat fmt)
    {
        var r = await _sut.CheckAsync("x.mp3", fmt, IntegrityLevel.L3);
        r.LevelApplied.Should().Be(IntegrityLevel.L1);
        _l1.Verify(c => c.CheckAsync("x.mp3", fmt, It.IsAny<CancellationToken>()), Times.Once);
        _l3.Verify(c => c.CheckAsync(It.IsAny<string>(), It.IsAny<AudioFormat>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // --- Auto level: FLAC + probe true -> L3; FLAC + probe false -> L1; non-FLAC -> L1 ---

    [Fact]
    public async Task CheckAsync_Auto_Flac_FlacAvailable_UsesL3()
    {
        _probe.Setup(p => p.IsAvailableAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var r = await _sut.CheckAsync("x.flac", AudioFormat.Flac, IntegrityLevel.Auto);
        r.LevelApplied.Should().Be(IntegrityLevel.L3);
        _l3.Verify(c => c.CheckAsync("x.flac", AudioFormat.Flac, It.IsAny<CancellationToken>()), Times.Once);
        _l1.Verify(c => c.CheckAsync(It.IsAny<string>(), It.IsAny<AudioFormat>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CheckAsync_Auto_Flac_FlacUnavailable_FallsBackToL1()
    {
        _probe.Setup(p => p.IsAvailableAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var r = await _sut.CheckAsync("x.flac", AudioFormat.Flac, IntegrityLevel.Auto);
        r.LevelApplied.Should().Be(IntegrityLevel.L1);
        _l1.Verify(c => c.CheckAsync("x.flac", AudioFormat.Flac, It.IsAny<CancellationToken>()), Times.Once);
        _l3.Verify(c => c.CheckAsync(It.IsAny<string>(), It.IsAny<AudioFormat>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(AudioFormat.Mp3)]
    [InlineData(AudioFormat.M4a)]
    [InlineData(AudioFormat.Unknown)]
    public async Task CheckAsync_Auto_NonFlac_AlwaysL1_ProbeNotCalled(AudioFormat fmt)
    {
        var r = await _sut.CheckAsync("x.mp3", fmt, IntegrityLevel.Auto);
        r.LevelApplied.Should().Be(IntegrityLevel.L1);
        _l1.Verify(c => c.CheckAsync("x.mp3", fmt, It.IsAny<CancellationToken>()), Times.Once);
        _probe.Verify(p => p.IsAvailableAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // --- Base interface CheckAsync (no level) defaults to Auto ---

    [Fact]
    public async Task CheckAsync_NoLevelArg_DefaultsToAuto_Mp3_UsesL1()
    {
        var r = await ((IIntegrityChecker)_sut).CheckAsync("x.mp3", AudioFormat.Mp3);
        r.LevelApplied.Should().Be(IntegrityLevel.L1);
        _l1.Verify(c => c.CheckAsync("x.mp3", AudioFormat.Mp3, It.IsAny<CancellationToken>()), Times.Once);
    }

    // --- Validation ---

    [Fact]
    public async Task CheckAsync_NullPath_Throws()
    {
        Func<Task> act = () => _sut.CheckAsync(null!, AudioFormat.Flac, IntegrityLevel.L1);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public void Constructor_NullL1_Throws()
    {
        Action act = () => _ = new CompositeIntegrityChecker(null!, _l3.Object, _probe.Object);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullL3_Throws()
    {
        Action act = () => _ = new CompositeIntegrityChecker(_l1.Object, null!, _probe.Object);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullProbe_Throws()
    {
        Action act = () => _ = new CompositeIntegrityChecker(_l1.Object, _l3.Object, null!);
        act.Should().Throw<ArgumentNullException>();
    }

    // --- T13: FlacNotAvailableException degradation policy ---

    [Fact]
    public async Task CheckAsync_L3_FlacNotAvailable_ReturnsFailed()
    {
        // User explicitly asked for L3. If the binary disappears between startup probe
        // and the L3 call (race), degrade to a Failed result so the file is quarantined
        // — never silently downgrade behind the user's back.
        _l3.Setup(c => c.CheckAsync(It.IsAny<string>(), It.IsAny<AudioFormat>(), It.IsAny<CancellationToken>()))
           .ThrowsAsync(new FlacNotAvailableException("flac binary not found"));

        var r = await _sut.CheckAsync("x.flac", AudioFormat.Flac, IntegrityLevel.L3);

        r.Ok.Should().BeFalse();
        r.LevelApplied.Should().Be(IntegrityLevel.L3);
        r.Reason.Should().Contain("flac binary not available");
        // L1 must NOT be silently invoked in --integrity l3 mode.
        _l1.Verify(c => c.CheckAsync(It.IsAny<string>(), It.IsAny<AudioFormat>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CheckAsync_Auto_FlacNotAvailable_DegradesToL1()
    {
        // Auto mode: probe said available at startup, but resolver lost it mid-run.
        // Degrade silently to L1 (the startup probe should have warned already).
        _probe.Setup(p => p.IsAvailableAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _l3.Setup(c => c.CheckAsync(It.IsAny<string>(), It.IsAny<AudioFormat>(), It.IsAny<CancellationToken>()))
           .ThrowsAsync(new FlacNotAvailableException("flac binary not found"));

        var r = await _sut.CheckAsync("x.flac", AudioFormat.Flac, IntegrityLevel.Auto);

        r.Ok.Should().BeTrue();
        r.LevelApplied.Should().Be(IntegrityLevel.L1);
        _l1.Verify(c => c.CheckAsync("x.flac", AudioFormat.Flac, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CheckAsync_L1_NotAffectedByL3Resolver()
    {
        // Sanity: an L1 request must never touch L3 (and therefore never trigger the
        // FlacNotAvailableException path). Even if we sabotage L3, L1 should succeed.
        _l3.Setup(c => c.CheckAsync(It.IsAny<string>(), It.IsAny<AudioFormat>(), It.IsAny<CancellationToken>()))
           .ThrowsAsync(new FlacNotAvailableException("flac binary not found"));

        var r = await _sut.CheckAsync("x.flac", AudioFormat.Flac, IntegrityLevel.L1);

        r.LevelApplied.Should().Be(IntegrityLevel.L1);
        _l1.Verify(c => c.CheckAsync("x.flac", AudioFormat.Flac, It.IsAny<CancellationToken>()), Times.Once);
        _l3.Verify(c => c.CheckAsync(It.IsAny<string>(), It.IsAny<AudioFormat>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
