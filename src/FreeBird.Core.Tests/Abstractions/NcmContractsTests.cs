using System.Reflection;
using FluentAssertions;
using FreeBird.Core.Abstractions;
using FreeBird.Core.Metadata;
using FreeBird.Core.Models;

namespace FreeBird.Core.Tests.Abstractions;

/// <summary>
/// Shape/contract guards for the .ncm decoder contracts (Task 3). These types are
/// interfaces/records/exceptions only — the implementation (NcmDecoder) arrives in
/// later tasks. The asserts here are the meaningful guards used elsewhere in the
/// codebase (mirrors InterfaceContractTests / IDependencyTests).
/// </summary>
public class NcmContractsTests
{
    [Fact]
    public void INcmDecoder_Extends_IDependency()
    {
        typeof(IDependency).IsAssignableFrom(typeof(INcmDecoder)).Should().BeTrue();
    }

    [Fact]
    public void INcmDecoder_IsPublic()
    {
        typeof(INcmDecoder).IsPublic.Should().BeTrue(
            "INcmDecoder must be public so the Cli assembly-scan can see it");
    }

    [Fact]
    public void INcmDecoder_HasDecodeAsync_WithExpectedSignature()
    {
        var method = typeof(INcmDecoder).GetMethod("DecodeAsync");
        method.Should().NotBeNull("INcmDecoder must declare DecodeAsync");

        method!.ReturnType.Should().Be(typeof(Task<NcmDecodeResult>));

        var parameters = method.GetParameters();
        parameters.Should().HaveCount(3);
        parameters[0].ParameterType.Should().Be(typeof(string));
        parameters[1].ParameterType.Should().Be(typeof(Stream));
        parameters[2].ParameterType.Should().Be(typeof(CancellationToken));
    }

    [Fact]
    public void NcmDecodeResult_IsRecord_WithThreeTypedProperties()
    {
        var type = typeof(NcmDecodeResult);

        // Records emit a compiler-generated EqualityContract property.
        type.GetProperty("EqualityContract", BindingFlags.Instance | BindingFlags.NonPublic)
            .Should().NotBeNull("NcmDecodeResult should be a record");

        type.GetProperty("Metadata")!.PropertyType.Should().Be(typeof(NcmMetadata));
        type.GetProperty("Cover")!.PropertyType.Should().Be(typeof(byte[]));
        type.GetProperty("Format")!.PropertyType.Should().Be(typeof(AudioFormat));
    }

    [Fact]
    public void NcmDecodeResult_RecordEquality()
    {
        var a = new NcmDecodeResult(null, null, AudioFormat.Flac);
        var b = new NcmDecodeResult(null, null, AudioFormat.Flac);
        a.Should().Be(b);
    }

    [Fact]
    public void NcmDecodeException_DerivesFromException()
    {
        typeof(Exception).IsAssignableFrom(typeof(NcmDecodeException)).Should().BeTrue();
    }

    [Fact]
    public void NcmDecodeException_ExposesReason_AndSetsMessage()
    {
        var ex = new NcmDecodeException("bad-magic");
        ex.Reason.Should().Be("bad-magic");
        ex.Message.Should().Be("bad-magic");
    }

    [Fact]
    public void NcmDecodeException_PreservesInnerException()
    {
        var inner = new InvalidOperationException("boom");
        var ex = new NcmDecodeException("aes-failure", inner);
        ex.Reason.Should().Be("aes-failure");
        ex.InnerException.Should().BeSameAs(inner);
    }
}
