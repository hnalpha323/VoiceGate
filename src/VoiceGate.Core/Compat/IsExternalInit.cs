#if NETSTANDARD2_0

namespace System.Runtime.CompilerServices;

/// <summary>
/// The C# compiler requires this type to emit init-only setters and records.
/// It ships in the BCL from .NET 5 onward, so netstandard2.0 needs its own copy.
/// </summary>
internal static class IsExternalInit
{
}

#endif
