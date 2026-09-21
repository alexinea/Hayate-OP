using System;
using System.Collections.Concurrent;
using System.Reflection;
using DotNetCore.HayateOP.Policies;

namespace DotNetCore.HayateOP.Common;

/// <summary>
/// Runtime resolution of the default object policy and of the default creation factory, for the entry
/// points whose generic constraint is <c>where T : class</c>.
/// </summary>
/// <remarks>
/// <see cref="DefaultHayateObjectPolicy{T}"/> keeps its <c>new()</c> constraint, because it really does
/// create objects with <c>new T()</c>; a caller that no longer carries that constraint cannot therefore
/// name the type directly. Both members below establish first that the constraint is satisfiable — with an
/// exception that names the fix when it is not — and only then reach the constrained code, so the
/// requirement is reported rather than assumed.<br />
/// The policy goes through <see cref="Activator"/>: it is resolved once per pool, on the cold build path,
/// and a fresh instance per pool keeps the ownership story unchanged. The factory is a cached
/// <c>new T()</c> delegate instead, because an unbounded pool calls it once per object it creates — that
/// path keeps exactly the cost it had before the constraint was relaxed.
/// </remarks>
internal static class HayateDefaultConstruction
{
    private static readonly MethodInfo FactoryCoreMethod = typeof(HayateDefaultConstruction)
        .GetMethod(nameof(FactoryCore), BindingFlags.Static | BindingFlags.NonPublic)!;

    private static readonly ConcurrentDictionary<Type, Delegate> Factories = new();

    /// <summary>
    /// Creates the default policy for <typeparamref name="T"/>, so a builder or container that no longer
    /// requires a parameterless constructor can still fall back to it.
    /// </summary>
    /// <typeparam name="T">The pooled object type.</typeparam>
    /// <returns>A new default policy.</returns>
    /// <exception cref="InvalidOperationException"><typeparamref name="T"/> has no public parameterless
    /// constructor, so the default policy cannot create objects of it.</exception>
    internal static IHayateObjectPolicy<T> Policy<T>() where T : class
    {
        RequireParameterlessConstructor<T>();

        var closed = typeof(DefaultHayateObjectPolicy<>).MakeGenericType(typeof(T));
        return (IHayateObjectPolicy<T>)Activator.CreateInstance(closed)!;
    }

    /// <summary>
    /// Returns a factory that creates <typeparamref name="T"/> with its parameterless constructor, for the
    /// pools that take a creation delegate rather than a policy.
    /// </summary>
    /// <typeparam name="T">The pooled object type.</typeparam>
    /// <returns>A cached factory; calling it costs what <c>new T()</c> costs.</returns>
    /// <exception cref="InvalidOperationException"><typeparamref name="T"/> has no public parameterless
    /// constructor.</exception>
    internal static Func<T> Factory<T>() where T : class
    {
        RequireParameterlessConstructor<T>();

        return (Func<T>)Factories.GetOrAdd(typeof(T), static type => Delegate.CreateDelegate(
            typeof(Func<>).MakeGenericType(type),
            FactoryCoreMethod.MakeGenericMethod(type)));
    }

    private static T FactoryCore<T>() where T : class, new() => new T();

    /// <summary>
    /// Throws when <typeparamref name="T"/> cannot satisfy <c>new()</c>, naming the entry points that do not
    /// need it. It runs before the constrained code is reached, so the caller reads a sentence instead of a
    /// <see cref="MissingMethodException"/> from inside an object factory.
    /// </summary>
    /// <typeparam name="T">The pooled object type.</typeparam>
    private static void RequireParameterlessConstructor<T>() where T : class
    {
        if (typeof(T).IsAbstract || typeof(T).GetConstructor(Type.EmptyTypes) is null)
        {
            throw new InvalidOperationException(
                $"HayateOP: the default object policy creates '{typeof(T).FullName}' with its public " +
                "parameterless constructor, which this type does not have. Supply a policy that creates the " +
                "object instead -- HayatePoolBuilder<T>.WithPolicy(...), an IHayateObjectPolicy<T> " +
                "registration in the container, or HayateUnboundedPool<T>(maxIdle, factory).");
        }
    }
}
