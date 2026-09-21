using System;
using DotNetCore.HayateOP.Common;

namespace DotNetCore.HayateOP.Policies;

/// <summary>
/// The library's own object policies, so a caller can obtain the default one — to use it as-is, to
/// decorate it, or to hand it to a pool that is built without naming a policy.
/// </summary>
/// <remarks>
/// From 2.9 the pool-building entry points (fluent builder, DI registrations, configuration bindings) no
/// longer require a public parameterless constructor at compile time, because connection-like objects
/// rarely have one. The default policy still creates objects with <c>new T()</c>, so that requirement did
/// not disappear — it moved from the signature to the moment the default policy is actually needed, which
/// is where <see cref="Default{T}"/> reports it. Pooling a type without a parameterless constructor means
/// supplying a policy that knows how to create it (<c>HayatePoolBuilder&lt;T&gt;.WithPolicy</c>, or an
/// <c>IHayateObjectPolicy&lt;T&gt;</c> registration in the container), which is also what makes the
/// asynchronous creation contract of 2.9 usable — a connection is created by I/O, not by a constructor.
/// </remarks>
/// <example>
/// <code>
/// // The default policy: new T(), reset IHayateResettable, validity from IHayateValidatable.
/// var policy = HayateObjectPolicies.Default&lt;MyBuffer&gt;();
/// </code>
/// </example>
public static class HayateObjectPolicies
{
    /// <summary>
    /// Creates the default policy for <typeparamref name="T"/>: objects are created with their public
    /// parameterless constructor, a released object is reset when it implements
    /// <see cref="IHayateResettable"/>, and its validity follows <see cref="IHayateValidatable"/>.
    /// </summary>
    /// <typeparam name="T">The pooled object type.</typeparam>
    /// <returns>A new default policy. It holds no state, so the caller owns it and may share it.</returns>
    /// <exception cref="InvalidOperationException"><typeparamref name="T"/> has no public parameterless
    /// constructor, so the default policy cannot create objects of it; supply a policy that can.</exception>
    /// <example>
    /// <code>
    /// var pool = new HayatePoolBuilder&lt;MyBuffer&gt;()
    ///     .WithPolicy(HayateObjectPolicies.Default&lt;MyBuffer&gt;())
    ///     .Build();
    /// </code>
    /// </example>
    public static IHayateObjectPolicy<T> Default<T>() where T : class
        => HayateDefaultConstruction.Policy<T>();
}
