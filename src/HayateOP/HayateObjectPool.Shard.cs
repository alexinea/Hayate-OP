using DotNetCore.HayateOP.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace DotNetCore.HayateOP;

public partial class HayatePoolBasic<T>
{
    internal class Shard
    {
        private readonly IHayateLogger _logger;
        private readonly ConcurrentQueue<HayateObject<T>> _queue = new();
        private int _maxSize;

        // 公平模式票号机制
        private long _ticketCounter = 0;
        private long _nextTicket = 0;

        public int Index { get; }

        public int Count => _queue.Count;

        //public int Available => _queue.Count;

        //public int Capacity => Volatile.Read(ref _maxSize);

        public int MaxSize => Volatile.Read(ref _maxSize);

        public Shard(HayatePoolOptions options, int index, int maxSize, IHayateLogger logger)
        {
            Index = index;
            //_maxSize = options.MaxPoolSize / options.ShardCount;
            _maxSize = maxSize;
            _logger = logger;
        }

        public void UpdateMaxSize(int newMaxSize)
        {
            if(newMaxSize < 0)
                throw new ArgumentOutOfRangeException(nameof(newMaxSize));
            Interlocked.Exchange(ref _maxSize, newMaxSize);
        }
        
        public int BorrowedCount => _queue.Count(x => x.IsBorrowed);

        public void Add(HayateObject<T> w)
        {
            if (w is null) throw new ArgumentNullException(nameof(w));

            var currentMax = Volatile.Read(ref _maxSize);

            //if (_queue.Count >= _maxSize)
            if (_queue.Count >= currentMax)
            {
                //_logger.LogWarning("[Shard {Index}] capacity exceeded, object will be destroyed (shard size: {Size}, max: {Max})", Index, _queue.Count, _maxSize);
                _logger.LogWarning("[Shard {Index}] capacity exceeded, object will be destroyed (shard size: {Size}, max: {Max})", Index, _queue.Count, currentMax);

                try
                {
                    if (w.Value is IDisposable d) d.Dispose();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to dispose object when [shard {Index}] is full", Index);
                }

                return;
            }

            _queue.Enqueue(w);
            //Interlocked.Increment(ref _availableCount);
            _logger.LogDebug("[Shard {Index}] Object added to shard (current size: {Size})", Index, _queue.Count);
        }

        //public bool TryTake(out HayateObject<T> w, TimeSpan timeout, bool useFair, ILogger logger = null)
        public bool TryTake(out HayateObject<T> w, bool useFairMode)
        {
            w = null;
            //var sw = ValueStopwatch.StartNew();
            long myTicket = 0;

            if (useFairMode)
            {
                myTicket = Interlocked.Increment(ref _ticketCounter) - 1;
            }

            try
            {
                //while (sw.Elapsed < timeout)
                //{
                if (useFairMode && Interlocked.Read(ref _nextTicket) != myTicket)
                {
                    //_spinWait.SpinOnce();
                    //continue;
                    return false;
                }

                //if (Interlocked.Read(ref _availableCount) > 0 && _queue.TryDequeue(out w))
                if (_queue.TryDequeue(out w))
                {
                    //Interlocked.Decrement(ref _availableCount);
                    if (useFairMode)
                    {
                        Interlocked.Increment(ref _nextTicket);
                    }

                    _logger.LogDebug("[Shard {Index}] Object taken from shard (current size: {Size})", Index, _queue.Count);
                    return true;
                }

                //_spinWait.SpinOnce();
                //}

                //if (useFair && Interlocked.Read(ref _nextTicket) == myTicket)
                //{
                //    Interlocked.Increment(ref _nextTicket);
                //}

                return false;
            }
            finally
            {
                // 公平模式：如果拿到了票但没获取到对象，归还票
                if (useFairMode && Interlocked.Read(ref _nextTicket) == myTicket)
                {
                    Interlocked.Increment(ref _nextTicket);
                }
            }
        }

        public void Remove(HayateObject<T> w)
        {
            if (w == null) return;

            var ww = _queue.ToList();
            
            if (ww.Remove(w))
            {
                //Interlocked.Decrement(ref _availableCount);

                // 重建队列
                _queue.Clear();
                foreach (var l in ww) _queue.Enqueue(l);
                _logger.LogDebug("[Shard {Index}] Object removed from shard (current size: {Size})", Index, _queue.Count);
            }
        }

        public IEnumerable<HayateObject<T>> GetAll() => _queue.ToArray();

        public void Clear()
        {
            int clearedCount = 0;
            while (_queue.TryDequeue(out var w))
            {
                try
                {
                    if (w.Value is IDisposable d) d.Dispose();
                    clearedCount++;
                }
                catch
                {
                    // ignore
                }
            }

            //Interlocked.Exchange(ref _availableCount, 0);
            _logger.LogInformation("[Shard {Index}] Shard cleared, {Count} objects destroyed", Index, clearedCount);
        }
    }


}