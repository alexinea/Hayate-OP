using DotNetCore.HayateOP.Policies;

namespace DotNetCore.HayateOP.DependencyInjection;

/// <summary>
/// Resolves the policy a pool of <typeparamref name="T"/> is built with, from the pool's name.
/// </summary>
/// <typeparam name="T">The pooled object type.</typeparam>
/// <remarks>
/// This is the way to give pools of one element type <b>different</b> policies — "primary uses
/// connection string A, replica uses connection string B". <see cref="IHayateObjectPolicy{T}"/> has
/// no pool-name parameter in any member, so the container otherwise resolves exactly one policy per
/// element type and every named pool of that type shares it.<br />
/// A factory is <b>optional</b>. With none registered, the container keeps resolving
/// <see cref="IHayateObjectPolicy{T}"/> by element type alone, which is what every earlier version
/// did. With one registered it is consulted <b>once per pool build</b>, on the cold path — never per
/// borrow — and for every pool of <typeparamref name="T"/>, the unnamed one included.<br />
/// Register it with <c>AddHayatePolicyFactory</c>. The alternative form — turning
/// <see cref="IHayateObjectPolicy{T}"/> itself into a keyed service — was rejected: keyed
/// registration does not exist in the 6.0 / 7.0 abstractions this package targets, and it would
/// change what an existing registration means. See <c>docs/named-pool-policies.md</c>.
/// </remarks>
/// <example>
/// <code>
/// sealed class ConnectionPolicyFactory : IHayateObjectPolicyFactory&lt;MyConnection&gt;
/// {
///     public IHayateObjectPolicy&lt;MyConnection&gt; Create(string poolName) =&gt; poolName switch
///     {
///         "MyConnection:primary" =&gt; new ConnectionPolicy(primaryConnectionString),
///         "MyConnection:replica" =&gt; new ConnectionPolicy(replicaConnectionString),
///         _ =&gt; throw new InvalidOperationException($"No policy for '{poolName}'."),
///     };
/// }
/// </code>
/// </example>
public interface IHayateObjectPolicyFactory<T> where T : class
{
    /// <summary>
    /// Creates the policy for the pool named <paramref name="poolName"/>.
    /// </summary>
    /// <param name="poolName">
    /// The pool's registry name: the element type name for the unnamed pool (<c>MyConnection</c>),
    /// and the element type name and the logical name joined by <c>:</c> for a named one
    /// (<c>MyConnection:primary</c>) — see <see cref="HayateServiceKey.RegistryName"/>.
    /// </param>
    /// <returns>The policy the pool is built with. Must not be <c>null</c>.</returns>
    IHayateObjectPolicy<T> Create(string poolName);
}
