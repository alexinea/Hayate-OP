using DotNetCore.HayateOP;
using Microsoft.Extensions.ObjectPool;
using System;

namespace DotNetCore.HayateOP.ObjectPoolCompat;

/// <summary>
/// T15：把 HayateOP 池包装为 <see cref="ObjectPool{T}"/>（Microsoft.Extensions.ObjectPool 抽象类），
/// 使现有 MEOP 调用方（只依赖 <c>ObjectPool&lt;T&gt;</c> 的 Get/Return）零代码改动切换到 HayateOP。
/// </summary>
/// <typeparam name="T">池化对象类型。</typeparam>
/// <remarks>
/// 语义映射：<c>Get()</c> → <see cref="IHayateObjectPool{T}.Acquire"/>；
/// <c>Return(obj)</c> → <see cref="IHayateObjectPool{T}.Release"/>。
/// 拒绝/溢出时 HayateOP 在 Release 内部销毁对象，与 MEOP「池满即丢弃」语义对齐。
/// 注意：Get 的阻塞/创建行为由被包装池自身的拒绝策略决定——
/// MEOP 语义（空池同步立即创建）请使用 <see cref="HayateObjectPoolCompatProvider"/> 构建池。
/// </remarks>
public sealed class HayateObjectPoolAdapter<T> : ObjectPool<T>, IDisposable where T : class
{
    /// <summary>被包装的 HayateOP 池实例。</summary>
    public IHayateObjectPool<T> InnerPool { get; }

    /// <summary>包装一个已构建的 HayateOP 池。</summary>
    public HayateObjectPoolAdapter(IHayateObjectPool<T> innerPool)
    {
        InnerPool = innerPool ?? throw new ArgumentNullException(nameof(innerPool));
    }

    /// <inheritdoc />
    public override T Get() => InnerPool.Acquire();

    /// <inheritdoc />
    public override void Return(T obj) => InnerPool.Release(obj);

    /// <summary>释放底层 HayateOP 池。</summary>
    public void Dispose() => InnerPool.Dispose();
}
