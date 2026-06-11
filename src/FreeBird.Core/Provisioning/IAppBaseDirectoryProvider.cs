namespace FreeBird.Core.Provisioning;

/// <summary>
/// Resolves the directory where the fb executable lives. Used by FlacBinaryResolver to find
/// flac{.exe} next to fb. Abstracted so tests can override without manipulating AppContext.
/// </summary>
/// <remarks>
/// Default impl returns AppContext.BaseDirectory, which is correct for all common deployment shapes:
/// - dotnet /path/to/fb.dll -> /path/to/ (where the .dll lives)
/// - Published self-contained fb.exe -> directory of the .exe
/// - dotnet run --project src/FreeBird.Cli -> bin/Debug/net10.0/ (dev mode)
///
/// In dev mode the user can override with --flac-bin or place flac in PATH; this is acceptable.
/// </remarks>
public interface IAppBaseDirectoryProvider
{
    /// <summary>Full path to the directory containing the running fb executable / dll.</summary>
    string GetBaseDirectory();
}

/// <summary>Default impl backed by AppContext.BaseDirectory.</summary>
public sealed class AppContextBaseDirectoryProvider : IAppBaseDirectoryProvider
{
    public string GetBaseDirectory()
    {
        var dir = AppContext.BaseDirectory;
        // AppContext.BaseDirectory commonly has a trailing separator; normalize away so resolver paths are clean
        return dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
