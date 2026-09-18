using System;

namespace DotNetCore.HayateOP;

/// <summary>
/// Identifies a pool by the pooled element type together with a logical name, so that several
/// independently configured pools of the same element type can coexist and be addressed by name.
/// </summary>
/// <remarks>
/// A key is an identity, not a configuration: it says <i>which</i> pool is meant, never how it is
/// built. The canonical <see cref="RegistryName"/> is what the
/// <see cref="IHayateObjectPoolRegistry"/> and the named DI registrations store the pool under —
/// the element type name and the logical name joined by <see cref="NameSeparator"/> — so a named
/// pool never collides with the unnamed pool of the same type (which the registry keeps under the
/// bare type name).<br />
/// The registry name is a registry / options key, not a configuration path: it contains
/// <see cref="NameSeparator"/>, which configuration providers treat as a section separator, so it
/// must not be used to build an <c>IConfiguration</c> section path.
/// </remarks>
/// <example>
/// <code>
/// var primary = HayateServiceKey.Create&lt;MyConnection&gt;("primary");
/// var replica = HayateServiceKey.Create&lt;MyConnection&gt;("replica");
/// </code>
/// </example>
public sealed class HayateServiceKey : IEquatable<HayateServiceKey>
{
    /// <summary>The separator placed between the element type name and the logical pool name.</summary>
    public const string NameSeparator = ":";

    /// <summary>
    /// Creates a key for the given element type and logical name.
    /// </summary>
    /// <param name="elementType">The pooled element type (the <c>T</c> of <see cref="IHayateObjectPool{T}"/>).</param>
    /// <param name="name">The logical pool name; cannot be empty and cannot contain <see cref="NameSeparator"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="elementType"/> or <paramref name="name"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty or whitespace, contains
    /// <see cref="NameSeparator"/>, or <paramref name="elementType"/> is a value type.</exception>
    public HayateServiceKey(Type elementType, string name)
    {
        if (elementType is null) throw new ArgumentNullException(nameof(elementType));
        if (name is null) throw new ArgumentNullException(nameof(name));
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("The pool name cannot be empty or whitespace.", nameof(name));
        if (name.IndexOf(NameSeparator, StringComparison.Ordinal) >= 0)
            throw new ArgumentException($"The pool name cannot contain '{NameSeparator}'.", nameof(name));
        if (elementType.IsValueType)
            throw new ArgumentException($"The pooled element type must be a reference type; '{elementType}' is a value type.", nameof(elementType));

        ElementType = elementType;
        Name = name;
        RegistryName = string.Concat(elementType.Name, NameSeparator, name);
    }

    /// <summary>The pooled element type.</summary>
    public Type ElementType { get; }

    /// <summary>The logical pool name, exactly as supplied.</summary>
    public string Name { get; }

    /// <summary>
    /// The canonical name the pool is registered under: the element type name and
    /// <see cref="Name"/> joined by <see cref="NameSeparator"/>.
    /// </summary>
    public string RegistryName { get; }

    /// <summary>Creates a key for <typeparamref name="T"/> under the given logical name.</summary>
    /// <typeparam name="T">The pooled object type.</typeparam>
    /// <param name="name">The logical pool name.</param>
    /// <returns>The service key.</returns>
    public static HayateServiceKey Create<T>(string name) => new(typeof(T), name);

    /// <summary>Creates a key for the given element type under the given logical name.</summary>
    /// <param name="elementType">The pooled element type.</param>
    /// <param name="name">The logical pool name.</param>
    /// <returns>The service key.</returns>
    public static HayateServiceKey Create(Type elementType, string name) => new(elementType, name);

    /// <summary>
    /// Tries to create a key, returning <c>false</c> instead of throwing when the name is unusable.
    /// </summary>
    /// <typeparam name="T">The pooled object type.</typeparam>
    /// <param name="name">The logical pool name.</param>
    /// <param name="key">When this method returns <c>true</c>, the created key; otherwise <c>null</c>.</param>
    /// <returns><c>true</c> when a key was created.</returns>
    public static bool TryCreate<T>(string? name, out HayateServiceKey? key)
    {
        key = null;
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (name!.IndexOf(NameSeparator, StringComparison.Ordinal) >= 0) return false;
        if (typeof(T).IsValueType) return false;

        key = new HayateServiceKey(typeof(T), name);
        return true;
    }

    /// <inheritdoc />
    public bool Equals(HayateServiceKey? other) =>
        other is not null && (ReferenceEquals(this, other) ||
                              (ElementType == other.ElementType &&
                               string.Equals(Name, other.Name, StringComparison.Ordinal)));

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as HayateServiceKey);

    /// <inheritdoc />
    public override int GetHashCode() => ElementType.GetHashCode() ^ StringComparer.Ordinal.GetHashCode(Name);

    /// <summary>Returns the canonical registry name.</summary>
    public override string ToString() => RegistryName;
}
