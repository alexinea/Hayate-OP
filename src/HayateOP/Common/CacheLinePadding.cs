using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace DotNetCore.HayateOP.Common;

/// <summary>
/// Pushes the fields a derived class declares into the second cache line, so that an object whose hot
/// state is written by one thread never shares a cache line with its neighbour.
/// </summary>
/// <remarks>
/// The padding is split across two places because of two runtime restrictions: the runtime ignores
/// <see cref="StructLayoutAttribute.Size"/> on a class that holds references, and it rejects explicit
/// layout on a generic type. The leading half therefore lives here, in a non-generic base whose only
/// field sits at the end of the first cache line, and the trailing half is a <see cref="CacheLinePad"/>
/// field declared by the most derived class — the runtime places it after every base-class field. With
/// both halves in place a padded object is at least one cache line longer than its own fields at each
/// end, which is what keeps neighbouring objects out of each other's line once the garbage collector
/// compacts objects that different threads allocated next to each other.<br />
/// This is layout only. No member, default value or observable behaviour changes; the cost is the
/// padding bytes on the few long-lived instances that opt in.
/// </remarks>
[ExcludeFromCodeCoverage]
[StructLayout(LayoutKind.Explicit)]
internal abstract class CacheLinePadded
{
    /// <summary>
    /// The assumed cache-line size in bytes — the line size of x64 and of every ARM64 part this
    /// package runs on.
    /// </summary>
    internal const int CacheLineSize = 64;

#pragma warning disable CS0169, CS0649 // The field is there to occupy space, never to be read or written.
    [FieldOffset(CacheLineSize - sizeof(long))]
    private readonly long _leadingPad;
#pragma warning restore CS0169, CS0649
}

/// <summary>
/// Occupies one cache line as the trailing field of a <see cref="CacheLinePadded"/> class.
/// </summary>
[ExcludeFromCodeCoverage]
[StructLayout(LayoutKind.Sequential, Size = CacheLinePadded.CacheLineSize)]
internal readonly struct CacheLinePad
{
}
