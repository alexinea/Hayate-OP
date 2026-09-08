using DotNetCore.HayateOP.Policies;
using Microsoft.Extensions.ObjectPool;
using System;

namespace DotNetCore.HayateOP.ObjectPoolCompat;

/// <summary>
/// T15：把 MEOP 的 <see cref="IPooledObjectPolicy{T}"/> 适配为 HayateOP 的
/// <see cref="IHayateObjectPolicy{T}"/>，使既有策略实现原样驱动 HayateOP 池。
/// </summary>
/// <typeparam name="T">池化对象类型。</typeparam>
/// <remarks>
/// 钩子映射（MEOP 仅有两个钩子，其余映射为无操作）：
/// <list type="bullet">
/// <item><c>Create()</c> → <see cref="IPooledObjectPolicy{T}.Create"/></item>
/// <item><c>OnRelease(item)</c> → <see cref="IPooledObjectPolicy{T}.Return"/>
/// （返回 false = 拒绝回池，HayateOP 将销毁该对象，与 MEOP「丢弃」语义一致）</item>
/// <item><c>Validate</c> → 恒 true（MEOP 无借出时校验语义；IResettable.TryReset
/// 由 MEOP 默认策略内部处理，经 Return 钩子自然到达）</item>
/// <item><c>OnAcquire/OnPassivate/OnDestroy</c> → 无操作</item>
/// </list>
/// </remarks>
public sealed class HayateCompatPooledObjectPolicy<T> : IHayateObjectPolicy<T> where T : class
{
    private readonly IPooledObjectPolicy<T> _inner;

    /// <summary>以既有 MEOP 策略驱动适配器。</summary>
    public HayateCompatPooledObjectPolicy(IPooledObjectPolicy<T> inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    /// <inheritdoc />
    public T Create() => _inner.Create();

    /// <inheritdoc />
    public bool OnRelease(T item) => _inner.Return(item);

    /// <inheritdoc />
    public bool Validate(T item) => true;

    /// <inheritdoc />
    public void OnAcquire(T item) { }

    /// <inheritdoc />
    public void OnPassivate(T item) { }

    /// <inheritdoc />
    public void OnDestroy(T item) { }
}
