using DotNetCore.HayateOP.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace DotNetCore.HayateOP;

public partial class HayatePoolBasic<T>
{
    internal class Shard
    {
        private readonly IHayateLogger _logger;

        // 空闲对象链表：Add 尾插、TryTake 头取，天然保持 FIFO；
        // Remove 通过 HayateObject<T>.Node 做 O(1) 摘除。
        // 旧实现是 ConcurrentQueue + "ToList → Remove → Clear → Enqueue" 重建队列，
        // 重建窗口内并发的 TryTake / Add 会丢对象或产生重复项，本次彻底替换为链表。
        private readonly LinkedList<HayateObject<T>> _list = new();

        // 保护 _list 以及对象位置状态迁移。临界区只做若干指针操作，极短。
        // 硬约束：临界区内绝不回调用户代码（Dispose / policy 一律在锁外执行），避免重入死锁。
        private SpinLock _lock = new(enableThreadOwnerTracking: false);

        private int _maxSize;

        // 借出计数说明（为何 Shard 不维护 BorrowedCount）：
        // TryTake 先把对象物理摘出本分片链表、再置 Borrowed，借出的对象根本不在
        // 链表中，因此「链表内 IsBorrowed 计数」结构上恒为 0；且归还时若被 Release
        // 拒绝（OnRelease=false）会直接销毁、不经过本分片 Add，增减无法一一配对。
        // 池级借出数由 TakeSnapshot 以 _objectMap.Count - 各分片 Count 之和 派生。

        public int Index { get; }

        public int Count
        {
            get
            {
                var taken = false;
                try
                {
                    _lock.Enter(ref taken);
                    return _list.Count;
                }
                finally { if (taken) _lock.Exit(); }
            }
        }

        public int MaxSize => Volatile.Read(ref _maxSize);

        public Shard(HayatePoolOptions options, int index, int maxSize, IHayateLogger logger)
        {
            Index = index;
            _maxSize = maxSize;
            _logger = logger;
        }

        public void UpdateMaxSize(int newMaxSize)
        {
            if (newMaxSize < 0)
                throw new ArgumentOutOfRangeException(nameof(newMaxSize));
            Interlocked.Exchange(ref _maxSize, newMaxSize);
        }

        /// <summary>
        /// Appends an object to the shard free list.
        /// </summary>
        /// <returns>
        /// <c>true</c> when the object was accepted; <c>false</c> when it was rejected —
        /// either the shard is at capacity (the object is disposed by this method), or the
        /// object was already claimed by eviction / validation / destroy and must not be
        /// handed out again.
        /// </returns>
        public bool Add(HayateObject<T> w)
        {
            if (w is null) throw new ArgumentNullException(nameof(w));

            var currentMax = Volatile.Read(ref _maxSize);
            HayateObject<T> overflow = null;
            var accepted = false;
            var size = 0;

            var taken = false;
            try
            {
                _lock.Enter(ref taken);

                // 只接收「刚创建」或「已借出后归还」两种状态。
                // 被驱逐 / 校验 / 销毁流程认领过（Removing / Destroyed）的对象一律拒绝，
                // 否则 Dispose 过的对象会被重新放回池中——这是上一轮 T04 回归的直接根因。
                var location = w.Location;
                if (location != HayateObjectLocation.None && location != HayateObjectLocation.Borrowed)
                    return false;

                // 兜底：理论上对象归还时 Node 必为 null，若因异常路径残留则先摘除再尾插，
                // 保证同一个对象在链表中最多只出现一次。
                var stale = w.Node;
                if (stale != null && ReferenceEquals(stale.List, _list))
                    _list.Remove(stale);

                size = _list.Count;
                if (size >= currentMax)
                {
                    overflow = w;
                    return false;
                }

                w.Node = _list.AddLast(w);
                w.Location = HayateObjectLocation.InPool;
                size++;
                accepted = true;
            }
            finally
            {
                if (taken) _lock.Exit();
            }

            // P0-新-1 修复：overflow 时不在本方法内自行处置（裸 Dispose 会漏掉 _policy.OnDestroy，
            // 且提前置 Destroyed=1 会让调用方的完整 Destroy(w) 因幂等 CAS 直接 return，OnDestroy 永不触发）。
            // 故这里只做日志 + 返回 false，把销毁完整地交给唯一真实调用方（Release → Destroy(w)，
            // 其内部会触发 _policy.OnDestroy → Dispose → _objectMap.TryRemove，且带幂等保护）。
            // PreWarm / ForceScaleUp 均先按容量 clamp / UpdateShardMaxSizes，不会在此真实 overflow。
            if (overflow != null)
            {
                _logger.LogWarning("[Shard {Index}] capacity exceeded, object rejected and will be destroyed by caller (shard size: {Size}, max: {Max})",
                    Index, size, currentMax);
            }
            else if (accepted)
            {
                _logger.LogDebug("[Shard {Index}] Object added to shard (current size: {Size})", Index, size);
            }

            return accepted;
        }

        public bool TryTake(out HayateObject<T> w)
        {
            w = null;

            var taken = false;
            try
            {
                _lock.Enter(ref taken);

                var node = _list.First;
                if (node == null) return false;

                w = node.Value;
                _list.Remove(node);
                w.Node = null;

                // 关键顺序：先摘链再置 Borrowed。
                // 此后 Remove 无法再认领该对象（其 Node 已不属于任何链表），
                // 因此驱逐线程不可能销毁一个已经交到调用方手里的对象。
                w.Location = HayateObjectLocation.Borrowed;

                _logger.LogDebug("[Shard {Index}] Object taken from shard (current size: {Size})", Index, _list.Count);
                return true;
            }
            finally { if (taken) _lock.Exit(); }
        }

        /// <summary>
        /// Claims an idle object for destruction and physically unlinks it from the shard.
        /// </summary>
        /// <returns>
        /// <c>true</c> only for the single caller that won the claim — that caller owns the
        /// object and is responsible for destroying it. <c>false</c> means the object is
        /// currently borrowed, lives in another shard, or has already been claimed, and the
        /// caller MUST NOT destroy it.
        /// </returns>
        public bool Remove(HayateObject<T> w)
        {
            if (w == null) return false;

            var claimed = false;
            var size = 0;

            var taken = false;
            try
            {
                _lock.Enter(ref taken);

                // 双重确认：状态为 InPool 且节点确实挂在本分片的链表上。
                // 二者缺一都说明对象此刻不在"可安全销毁"的位置（已借出 / 已认领 / 在他分片）。
                if (w.Location != HayateObjectLocation.InPool) return false;

                var node = w.Node;
                if (node == null || !ReferenceEquals(node.List, _list)) return false;

                _list.Remove(node);
                w.Node = null;
                w.Location = HayateObjectLocation.Removing;
                size = _list.Count;
                claimed = true;
            }
            finally
            {
                if (taken) _lock.Exit();
            }

            if (claimed)
            {
                _logger.LogDebug("[Shard {Index}] Object removed from shard (current size: {Size})", Index, size);
            }

            return claimed;
        }

        public IEnumerable<HayateObject<T>> GetAll()
        {
            var taken = false;
            try
            {
                _lock.Enter(ref taken);
                return _list.ToArray();
            }
            finally { if (taken) _lock.Exit(); }
        }

        public void Clear()
        {
            HayateObject<T>[] drained;

            var taken = false;
            try
            {
                _lock.Enter(ref taken);
                drained = _list.ToArray();
                _list.Clear();
                foreach (var w in drained)
                {
                    w.Node = null;
                    Interlocked.Exchange(ref w.Destroyed, 1);
                    w.Location = HayateObjectLocation.Destroyed;
                }
            }
            finally { if (taken) _lock.Exit(); }

            var clearedCount = 0;
            foreach (var w in drained)
            {
                if (SafeDispose(w, "shard clear")) clearedCount++;
            }

            _logger.LogInformation("[Shard {Index}] Shard cleared, {Count} objects destroyed", Index, clearedCount);
        }

        /// <summary>
        /// 在锁外安全释放对象，吞掉用户 Dispose 抛出的异常并返回是否释放成功。
        /// </summary>
        private bool SafeDispose(HayateObject<T> w, string reason)
        {
            try
            {
                if (w.Value is IDisposable d) d.Dispose();
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to dispose object when [{Reason}]", reason);
                return false;
            }
        }
    }
}
