using System;
using System.Collections.Concurrent;

namespace DotNetCore.Extensions.ObjectPool;

public class ObjectPool<T>
{
    private readonly ConcurrentBag<T> _objects;
    private readonly Func<T> _objectGenerator;
    private readonly int _maxSize;

    /// <summary>
    /// 构造对象池
    /// </summary>
    /// <param name="objectGenerator"></param>
    /// <param name="maxSize"></param>
    /// <exception cref="ArgumentNullException"></exception>
    public ObjectPool(Func<T> objectGenerator, int maxSize = 100)
    {
        _objects = new ConcurrentBag<T>();
        _objectGenerator = objectGenerator ?? throw new ArgumentNullException(nameof(objectGenerator));
        _maxSize = maxSize;
    }

    /// <summary>
    /// 从池获取对象
    /// </summary>
    /// <returns></returns>
    public T Get()
    {
        if (_objects.TryTake(out T item))
            return item;
        return _objectGenerator();
    }

    /// <summary>
    /// 归还对象到池
    /// </summary>
    /// <param name="item"></param>
    public void Return(T item)
    {
        if (item is null) return;

        // 超过最大容量则不缓存
        if (_objects.Count < _maxSize)
            _objects.Add(item);
    }

    /// <summary>
    /// 清空池
    /// </summary>
    public void Clear() => _objects.Clear();
}