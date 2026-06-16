using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FreeBird.Cli.Service;
using FreeBird.Core.Abstractions;
using FreeBird.Core.DependencyInjection;
using FreeBird.Core.Service;
using Xunit;

namespace FreeBird.Cli.Tests.Service;

/// <summary>
/// T07 — Contract tests for <see cref="IServiceController"/>.
///
/// These tests pin down:
///   (1) the interface is mockable + every method is invokable through the
///       interface surface (compilation-only proof);
///   (2) no concrete <see cref="IServiceController"/> implementation lives in
///       the Core assembly (guards the B-5/B-6 invariant: scm-aware code stays
///       in the Cli assembly so <see cref="CoreModule"/>'s assembly scan never
///       accidentally registers a Windows-only impl);
///   (3) the interface extends <see cref="IDependency"/> so DI registration
///       works once the impls land in T11/T13;
///   (4) the DTO shapes are immutable records with the documented members;
///   (5) the async methods take a <see cref="CancellationToken"/> as the last
///       parameter (locks down the async cancellation contract).
/// </summary>
public class IServiceControllerContractTests
{
    [Fact]
    public async Task Interface_CanBeImplemented_AndAllMembersInvokedViaInterface()
    {
        IServiceController sut = new FakeController();
        var req = new ServiceInstallRequest(
            ShortName: "fb",
            DisplayName: "FreeBird",
            Description: "desc",
            BinPath: "/usr/local/bin/fb",
            Account: null,
            Password: null,
            RestartPolicy: RestartPolicy.Manual);

        sut.Install(req);
        sut.Uninstall("fb");
        await sut.StartAsync("fb", TimeSpan.FromSeconds(5), CancellationToken.None);
        await sut.StopAsync("fb", TimeSpan.FromSeconds(5), CancellationToken.None);
        var status = sut.Query("fb");
        var exists = sut.Exists("fb");

        var fake = (FakeController)sut;
        fake.InstallCalls.Should().Be(1);
        fake.UninstallCalls.Should().Be(1);
        fake.StartCalls.Should().Be(1);
        fake.StopCalls.Should().Be(1);
        fake.QueryCalls.Should().Be(1);
        fake.ExistsCalls.Should().Be(1);
        status.ShortName.Should().Be("fb");
        exists.Should().BeTrue();
    }

    [Fact]
    public void IServiceController_HasNoConcreteImplementation_InCoreAssembly()
    {
        var coreAssembly = typeof(CoreModule).Assembly;
        var offenders = coreAssembly.GetTypes()
            .Where(t => typeof(IServiceController).IsAssignableFrom(t)
                        && !t.IsInterface
                        && !t.IsAbstract)
            .ToArray();

        offenders.Should().BeEmpty(
            "T07 forbids any IServiceController implementation in FreeBird.Core (B-5/B-6 invariant)");
    }

    [Fact]
    public void IServiceController_ExtendsIDependency()
    {
        typeof(IDependency).IsAssignableFrom(typeof(IServiceController))
            .Should().BeTrue("IServiceController must be DI-discoverable via the IDependency marker");
    }

    [Fact]
    public void ServiceState_HasExpectedMembers()
    {
        var names = Enum.GetNames(typeof(ServiceState));
        names.Should().BeEquivalentTo(new[]
        {
            "NotInstalled",
            "Stopped",
            "StartPending",
            "Running",
            "StopPending",
            "Paused",
            "Other",
        });
    }

    [Fact]
    public void ServiceInstallRequest_IsImmutableRecord()
    {
        var t = typeof(ServiceInstallRequest);
        t.IsValueType.Should().BeFalse("records used here must be reference-type records, not record structs");
        // Compiler-generated members proving record-ness:
        t.GetMethod("<Clone>$", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Should().NotBeNull("record types emit a <Clone>$ method");

        // All properties must be init-only (no public setters).
        foreach (var prop in t.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            var setter = prop.GetSetMethod(nonPublic: false);
            if (setter is null) { continue; }
            // init-only setters carry the IsExternalInit modifier.
            var isInitOnly = setter.ReturnParameter
                .GetRequiredCustomModifiers()
                .Any(m => m.FullName == "System.Runtime.CompilerServices.IsExternalInit");
            isInitOnly.Should().BeTrue($"property {prop.Name} must be init-only");
        }

        // Value-based equality round-trip.
        var a = new ServiceInstallRequest(
            ShortName: "fb",
            DisplayName: "FreeBird",
            Description: "d",
            BinPath: "/bin/fb",
            Account: "LocalSystem",
            Password: null,
            RestartPolicy: RestartPolicy.Restart);
        var b = new ServiceInstallRequest(
            ShortName: "fb",
            DisplayName: "FreeBird",
            Description: "d",
            BinPath: "/bin/fb",
            Account: "LocalSystem",
            Password: null,
            RestartPolicy: RestartPolicy.Restart);
        a.Should().Be(b);
        a.GetHashCode().Should().Be(b.GetHashCode());
    }

    [Fact]
    public void ServiceStatusInfo_IsImmutableRecord()
    {
        var t = typeof(ServiceStatusInfo);
        t.IsValueType.Should().BeFalse();
        t.GetMethod("<Clone>$", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Should().NotBeNull("record types emit a <Clone>$ method");

        foreach (var prop in t.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            var setter = prop.GetSetMethod(nonPublic: false);
            if (setter is null) { continue; }
            var isInitOnly = setter.ReturnParameter
                .GetRequiredCustomModifiers()
                .Any(m => m.FullName == "System.Runtime.CompilerServices.IsExternalInit");
            isInitOnly.Should().BeTrue($"property {prop.Name} must be init-only");
        }

        var started = DateTimeOffset.UtcNow;
        var a = new ServiceStatusInfo(
            ShortName: "fb",
            DisplayName: "FreeBird",
            State: ServiceState.Running,
            Account: "LocalSystem",
            StartType: "Auto",
            StartedAtUtc: started,
            ConfigPath: "/etc/fb.json",
            LogFilePath: "/var/log/fb.log");
        var b = new ServiceStatusInfo(
            ShortName: "fb",
            DisplayName: "FreeBird",
            State: ServiceState.Running,
            Account: "LocalSystem",
            StartType: "Auto",
            StartedAtUtc: started,
            ConfigPath: "/etc/fb.json",
            LogFilePath: "/var/log/fb.log");
        a.Should().Be(b);
        a.GetHashCode().Should().Be(b.GetHashCode());
    }

    [Fact]
    public void IServiceController_StartAsyncAndStopAsync_AcceptCancellationToken()
    {
        AssertLastParamIsCancellationToken(nameof(IServiceController.StartAsync));
        AssertLastParamIsCancellationToken(nameof(IServiceController.StopAsync));

        static void AssertLastParamIsCancellationToken(string methodName)
        {
            var method = typeof(IServiceController).GetMethod(methodName);
            method.Should().NotBeNull();
            var parameters = method!.GetParameters();
            parameters.Should().NotBeEmpty();
            parameters[^1].ParameterType.Should().Be<CancellationToken>(
                $"{methodName} must accept CancellationToken as its last parameter");
        }
    }

    // ---------------------------------------------------------------- helpers

    private sealed class FakeController : IServiceController
    {
        public int InstallCalls { get; private set; }
        public int UninstallCalls { get; private set; }
        public int StartCalls { get; private set; }
        public int StopCalls { get; private set; }
        public int QueryCalls { get; private set; }
        public int ExistsCalls { get; private set; }

        public void Install(ServiceInstallRequest req) => InstallCalls++;
        public void Uninstall(string shortName) => UninstallCalls++;

        public Task StartAsync(string shortName, TimeSpan timeout, CancellationToken ct)
        {
            StartCalls++;
            return Task.CompletedTask;
        }

        public Task StopAsync(string shortName, TimeSpan timeout, CancellationToken ct)
        {
            StopCalls++;
            return Task.CompletedTask;
        }

        public ServiceStatusInfo Query(string shortName)
        {
            QueryCalls++;
            return new ServiceStatusInfo(
                ShortName: shortName,
                DisplayName: shortName,
                State: ServiceState.NotInstalled,
                Account: null,
                StartType: null,
                StartedAtUtc: null,
                ConfigPath: null,
                LogFilePath: null);
        }

        public bool Exists(string shortName)
        {
            ExistsCalls++;
            return true;
        }
    }
}
