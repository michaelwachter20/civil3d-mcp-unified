// ScriptReferenceResolver.cs - part of the Roslyn code-execution engine. ScriptEngine holds one instance and sets it as
// the scripts' MetadataResolver, wrapping Roslyn's default. It exists only to keep memory flat: each missing assembly
// is resolved once and reused across compiles. No drawing, no file writes (Roslyn reads the assembly files).
using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Civil3DMcpPlugin;

/// <summary>
/// Scripting's default resolver, except that an assembly it finds beyond the listed references is read once.
/// <para>
/// A reference can depend on an assembly that isn't loaded yet, and Roslyn asks the resolver for it during every compile.
/// The default answers with a new reference each time, and each one holds a native copy of the file, which is how a
/// new script cost hundreds of MB that the cached script then kept alive. The GC can't see that memory.
/// </para>
/// </summary>
internal sealed class ScriptReferenceResolver : MetadataReferenceResolver
{
  private readonly MetadataReferenceResolver _inner;
  private readonly ConcurrentDictionary<string, PortableExecutableReference?> _resolved = new(StringComparer.OrdinalIgnoreCase);

  /// <summary>Wraps <paramref name="inner"/> (ScriptOptions.Default.MetadataResolver).</summary>
  public ScriptReferenceResolver(MetadataReferenceResolver inner)
  {
    _inner = inner;
  }

  /// <summary>How many assemblies have been found beyond the listed references (logged after each compile).</summary>
  public int ResolvedCount => _resolved.Count;

  /// <summary>As the inner resolver.</summary>
  public override bool ResolveMissingAssemblies => _inner.ResolveMissingAssemblies;

  /// <summary>The inner resolver's answer for this assembly identity, asked once and cached by display name (a null
  /// answer is cached too).</summary>
  public override PortableExecutableReference? ResolveMissingAssembly(MetadataReference definition, AssemblyIdentity referenceIdentity)
  {
    return _resolved.GetOrAdd(referenceIdentity.GetDisplayName(), _ => _inner.ResolveMissingAssembly(definition, referenceIdentity));
  }

  /// <summary>Passed straight to the inner resolver (#r references; not cached).</summary>
  public override ImmutableArray<PortableExecutableReference> ResolveReference(string reference, string? baseFilePath, MetadataReferenceProperties properties)
  {
    return _inner.ResolveReference(reference, baseFilePath, properties);
  }

  // MetadataReferenceResolver requires Equals/GetHashCode; identity fits, as ScriptEngine shares one instance.
  public override bool Equals(object? other) => ReferenceEquals(this, other);

  public override int GetHashCode() => _inner.GetHashCode();
}
