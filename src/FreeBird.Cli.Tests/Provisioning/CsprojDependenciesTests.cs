using System.IO;
using System.Linq;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace FreeBird.Cli.Tests.Provisioning;

/// <summary>
/// T01 — Verifies that FreeBird.Cli.csproj references the NuGet packages required
/// for Windows Service hosting (FreeBird v3.5 service feature) and does NOT
/// reference System.Management (M-3 fix: T12 uses Process.GetProcessById(...)
/// .StartTime instead of WMI to determine service start time).
///
/// This is project-file plumbing coverage — it asserts the csproj XML directly so
/// that an accidental removal of any of these PackageReferences during a refactor
/// is caught immediately rather than at downstream code-compile time.
/// </summary>
public class CsprojDependenciesTests
{
    private static XDocument LoadCliCsproj()
    {
        // Walk up from the test assembly's bin directory until we find the repo
        // root (the directory containing FreeBird.sln). This keeps the test
        // independent of how/where the test runner places the working directory.
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FreeBird.sln")))
        {
            dir = dir.Parent;
        }

        dir.Should().NotBeNull("repo root containing FreeBird.sln must be discoverable from the test working directory");

        var csprojPath = Path.Combine(dir!.FullName, "src", "FreeBird.Cli", "FreeBird.Cli.csproj");
        File.Exists(csprojPath).Should().BeTrue($"csproj should exist at {csprojPath}");

        return XDocument.Load(csprojPath);
    }

    private static bool HasPackageReference(XDocument doc, string packageId)
    {
        return doc.Descendants("PackageReference")
            .Any(pr => string.Equals((string?)pr.Attribute("Include"), packageId, System.StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Csproj_References_MicrosoftExtensionsHostingWindowsServices()
    {
        var doc = LoadCliCsproj();
        HasPackageReference(doc, "Microsoft.Extensions.Hosting.WindowsServices")
            .Should().BeTrue("Microsoft.Extensions.Hosting.WindowsServices is required for SCM host integration (T01)");
    }

    [Fact]
    public void Csproj_References_SerilogSinksEventLog()
    {
        var doc = LoadCliCsproj();
        HasPackageReference(doc, "Serilog.Sinks.EventLog")
            .Should().BeTrue("Serilog.Sinks.EventLog is required for Windows Event Log integration (T01)");
    }

    [Fact]
    public void Csproj_References_SystemServiceProcessServiceController()
    {
        var doc = LoadCliCsproj();
        HasPackageReference(doc, "System.ServiceProcess.ServiceController")
            .Should().BeTrue("System.ServiceProcess.ServiceController is required for managed SCM access (T01)");
    }

    [Fact]
    public void Csproj_DoesNotReference_SystemManagement_M3Guard()
    {
        // M-3 fix: T12 obtains service start time via Process.GetProcessById(...).StartTime
        // after locating the service PID via QueryServiceStatusEx — System.Management (WMI)
        // is intentionally NOT pulled in as a dependency.
        var doc = LoadCliCsproj();
        HasPackageReference(doc, "System.Management")
            .Should().BeFalse("System.Management must NOT be referenced — T12 uses Process.GetProcessById per M-3 fix");
    }
}
