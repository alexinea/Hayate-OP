using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.ExceptionServices;

namespace DotNetCore.HayateOP;

/// <summary>
/// Default <see cref="IHayatePoolFactory"/>: builds pools with <see cref="HayatePoolBuilder{T}"/>
/// and, when a registry is supplied, registers each built pool under the key's canonical name.
/// </summary>
/// <remarks>
/// Pool creation is a cold path — it happens when a tenant, a plugin or a subsystem first asks for a
/// pool, not per borrow — so the generic dispatch for the non-generic <see cref="Create"/> /
/// <see cref="GetOrCreate"/> overloads is done through a cached generic-method handle rather than
/// duplicated per type. Nothing the factory does is on the borrow or return path.
/// </remarks>
/// <example>
/// <code>
/// var factory = new HayatePoolFactory();
/// var pool = factory.CreatePool&lt;MyConnection&gt;("primary", o =&gt; o.MaxPoolSize = 64);
/// </code>
/// </example>
public class HayatePoolFactory : IHayatePoolFactory
{
    private static readonly MethodInfo BuildTypedMethod =
        typeof(HayatePoolFactory).GetMethod(nameof(BuildTyped), BindingFlags.Static | BindingFlags.NonPublic)!;

    private static readonly ConcurrentDictionary<Type, MethodInfo> BuildTypedCache = new();

    private readonly IHayateObjectPoolRegistry? _registry;

    /// <summary>
    /// Creates a factory that does not register the pools it builds.
    /// </summary>
    public HayatePoolFactory() : this(null)
    {
    }

    /// <summary>
    /// Creates a factory that registers every pool it builds into <paramref name="registry"/>.
    /// </summary>
    /// <param name="registry">The registry to register built pools into; <c>null</c> disables
    /// registration (and makes <see cref="GetOrCreate"/> equivalent to <see cref="Create"/>).</param>
    public HayatePoolFactory(IHayateObjectPoolRegistry? registry)
    {
        _registry = registry;
    }

    /// <summary>The registry built pools are registered into; <c>null</c> when registration is off.</summary>
    public IHayateObjectPoolRegistry? Registry => _registry;

    /// <inheritdoc />
    public IHayateObjectPool Create(HayateServiceKey key, Action<HayatePoolOptions>? configure = null)
    {
        if (key is null) throw new ArgumentNullException(nameof(key));

        var pool = Build(key, configure);
        _registry?.Register(key.RegistryName, pool);
        return pool;
    }

    /// <inheritdoc />
    public IHayateObjectPool GetOrCreate(HayateServiceKey key, Action<HayatePoolOptions>? configure = null)
    {
        if (key is null) throw new ArgumentNullException(nameof(key));

        if (_registry != null && _registry.TryGet(key.RegistryName, out var existing))
        {
            return existing;
        }

        var pool = Build(key, configure);
        _registry?.Register(key.RegistryName, pool);
        return pool;
    }

    private IHayateObjectPool Build(HayateServiceKey key, Action<HayatePoolOptions>? configure)
    {
        ValidateElementType(key.ElementType, nameof(key));

        var build = BuildTypedCache.GetOrAdd(key.ElementType,
            elementType => BuildTypedMethod.MakeGenericMethod(elementType));

        try
        {
            return (IHayateObjectPool)build.Invoke(null, new object?[] { key, configure })!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            // Surface the build failure itself (an invalid configuration, a rejected metrics sink)
            // instead of the reflection wrapper, so the message a caller sees is the one Build() wrote.
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;  // unreachable; keeps the compiler happy
        }
    }

    private static IHayateObjectPool BuildTyped<T>(HayateServiceKey key, Action<HayatePoolOptions>? configure)
        where T : class, new()
    {
        var builder = new HayatePoolBuilder<T>().WithPoolName(key.RegistryName);
        if (configure != null)
        {
            builder.Configure(configure);
        }

        return builder.Build();
    }

    private static void ValidateElementType(Type elementType, string paramName)
    {
        if (elementType.IsValueType || elementType.IsInterface || elementType.IsAbstract)
        {
            throw new ArgumentException(
                $"The pooled element type must be a concrete reference type; '{elementType}' is not.",
                paramName);
        }

        if (elementType.IsGenericTypeDefinition || elementType.ContainsGenericParameters)
        {
            throw new ArgumentException(
                $"The pooled element type must be a closed type; '{elementType}' still has open generic parameters.",
                paramName);
        }

        if (elementType.GetConstructor(Type.EmptyTypes) == null)
        {
            throw new ArgumentException(
                $"The pooled element type must expose a public parameterless constructor; '{elementType}' does not. " +
                "Supply a custom IHayateObjectPolicy through a builder when the pooled type cannot be created that way.",
                paramName);
        }
    }
}
