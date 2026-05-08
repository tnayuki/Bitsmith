namespace Bitsmith.Llvm.IR;

/// <summary>
/// IR-level linkage. Maps to bitcode encoding via <see cref="Bitsmith.Llvm.Codes.LinkageCodes"/>.
/// </summary>
public enum Linkage
{
    External = 0,
    Internal = 1,
    Private = 2,
    LinkOnceAny = 3,
    LinkOnceOdr = 4,
    WeakAny = 5,
    WeakOdr = 6,
    Appending = 7,
    AvailableExternally = 8,
    ExternalWeak = 9,
    Common = 10,
}

internal static class LinkageEncoding
{
    /// <summary>
    /// Maps a <see cref="Linkage"/> to the bitcode encoding used by LLVM 15.
    /// Values come from BitcodeWriter.cpp <c>getEncodedLinkage</c>.
    /// </summary>
    public static uint Encode(Linkage linkage) => linkage switch
    {
        Linkage.External => 0,
        Linkage.WeakAny => 16,
        Linkage.Appending => 2,
        Linkage.Internal => 3,
        Linkage.LinkOnceAny => 18,
        Linkage.WeakOdr => 17,
        Linkage.LinkOnceOdr => 19,
        Linkage.AvailableExternally => 12,
        Linkage.Private => 9,
        Linkage.ExternalWeak => 7,
        Linkage.Common => 8,
        _ => 0,
    };
}
