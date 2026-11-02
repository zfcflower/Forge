// Adapted from Tint (https://github.com/sebastienros/tint, MIT License, Copyright (c) 2026 Sebastien Ros):
// see THIRD_PARTY_NOTICES.md for the license text and the sources it was itself derived from.

using System.Runtime.CompilerServices;

namespace Forge.Editor.TextMate.Internal;

// How the per-line path asks the JIT to compile it.
internal static class Jit
{
    // The methods of the per-line path are compiled fully optimized on their first call instead of starting in tier 0:
    // tier-0 code runs several times slower because nothing is inlined, and a host that keeps compiling new methods, as
    // an editor does, postpones their promotion indefinitely since the runtime restarts its call-counting delay at every
    // new tier-0 method. Measured on 20k lines of C#: about 3% faster than tiered. Callees small enough to inline need
    // no attribute, since inlining is decided by the caller's compilation.
#if NETCOREAPP3_0_OR_GREATER
    public const MethodImplOptions FullOpts = MethodImplOptions.AggressiveOptimization;
#else
    public const MethodImplOptions FullOpts = 0;
#endif
}
