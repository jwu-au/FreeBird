using System.Linq;
using FluentAssertions;
using FreeBird.Core.Abstractions;

namespace FreeBird.Core.Tests.Abstractions;

public class InterfaceContractTests
{
    public static IEnumerable<object[]> AllDependencyInterfaces =>
        typeof(IDependency).Assembly
            .GetTypes()
            .Where(t => t.IsInterface && typeof(IDependency).IsAssignableFrom(t) && t != typeof(IDependency))
            .Select(t => new object[] { t });

    [Theory]
    [MemberData(nameof(AllDependencyInterfaces))]
    public void AllDependencyDerivedInterfaces_ArePublic(Type interfaceType)
    {
        interfaceType.IsPublic.Should().BeTrue(
            $"{interfaceType.Name} should be public so Cli assembly-scan can see it");
    }

    [Fact]
    public void AllRequiredInterfaces_Exist()
    {
        var asm = typeof(IDependency).Assembly;
        var required = new[]
        {
            "IXorDecoder",
            "IFormatSniffer",
            "INamingStrategy",
            "IIntegrityChecker",
            "IL1IntegrityChecker",
            "IL3IntegrityChecker",
            "IProcessRunner",
            "IAtomicFileWriter",
            "IFlacProbe",
            "IFileProcessor",
            "IScanOrchestrator",
        };
        var found = asm.GetTypes().Where(t => t.IsInterface).Select(t => t.Name).ToHashSet();
        foreach (var name in required)
            found.Should().Contain(name, $"required interface {name} missing");
    }

    [Fact]
    public void IL1IntegrityChecker_ExtendsIIntegrityChecker()
    {
        typeof(IIntegrityChecker).IsAssignableFrom(typeof(IL1IntegrityChecker)).Should().BeTrue();
    }

    [Fact]
    public void IL3IntegrityChecker_ExtendsIIntegrityChecker()
    {
        typeof(IIntegrityChecker).IsAssignableFrom(typeof(IL3IntegrityChecker)).Should().BeTrue();
    }

    [Fact]
    public void ProcessResult_RecordEquality()
    {
        var a = new ProcessResult(0, "out", "err");
        var b = new ProcessResult(0, "out", "err");
        a.Should().Be(b);
    }
}
