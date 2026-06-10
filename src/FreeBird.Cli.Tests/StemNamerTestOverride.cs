using System;
using Autofac;
using FreeBird.Core.Abstractions;
using FreeBird.Core.Decoding;
using FreeBird.Cli;

namespace FreeBird.Cli.Tests;

/// <summary>
/// v3 T13 test helper. After the v3 DI swap, the default <see cref="IFileNamer"/>
/// is <see cref="FreeBird.Core.Naming.MetadataAwareFileNamer"/>, which produces
/// <c>{musicId}.{ext}</c> filenames when metadata is unresolved.
///
/// v1/v2 E2E tests pre-populate stem-named inputs (e.g. <c>42-song.uc</c>,
/// <c>alpha.uc</c>) and assert on stem-named outputs (<c>42-song.mp3</c>,
/// <c>alpha.mp3</c>). To keep those tests valid against v3 wiring, this helper
/// installs <see cref="StemBasedFileNamer"/> as the resolved <see cref="IFileNamer"/>
/// via the runners' <c>AdditionalContainerSetup</c> hooks (Autofac
/// last-registration-wins). Restore the previous hook value in <c>Dispose</c>.
///
/// Per the T13 brief: "instantiating StemBasedFileNamer directly (preferred for
/// naming-isolated tests)" — this is the integration-test analogue: register
/// it as the singleton override instead of new-ing it inline.
/// </summary>
public sealed class StemNamerTestOverride : IDisposable
{
    private readonly Action<ContainerBuilder>? _previousScanHook;
    private readonly Action<ContainerBuilder>? _previousWatchHook;

    public StemNamerTestOverride()
    {
        _previousScanHook = ScanRunner.AdditionalContainerSetup;
        _previousWatchHook = WatchRunner.AdditionalContainerSetup;

        Action<ContainerBuilder> overrideHook = b => b
            .RegisterType<StemBasedFileNamer>()
            .As<IFileNamer>()
            .SingleInstance();

        ScanRunner.AdditionalContainerSetup = Combine(_previousScanHook, overrideHook);
        WatchRunner.AdditionalContainerSetup = Combine(_previousWatchHook, overrideHook);
    }

    public void Dispose()
    {
        ScanRunner.AdditionalContainerSetup = _previousScanHook;
        WatchRunner.AdditionalContainerSetup = _previousWatchHook;
    }

    private static Action<ContainerBuilder>? Combine(
        Action<ContainerBuilder>? a,
        Action<ContainerBuilder> b)
    {
        if (a is null) { return b; }
        return cb => { a(cb); b(cb); };
    }
}
