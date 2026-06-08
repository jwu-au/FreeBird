namespace FreeBird.Core.Models;

/// <summary>
/// Audio format detected by magic-byte sniffing after XOR decryption.
/// </summary>
public enum AudioFormat
{
    Unknown = 0,
    Mp3 = 1,
    Flac = 2,
    M4a = 3,
}
