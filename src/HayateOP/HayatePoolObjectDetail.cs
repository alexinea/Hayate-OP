namespace DotNetCore.HayateOP;

/// <summary>
/// M20（2.5）：快照中的逐对象生命周期明细。
/// 由 <see cref="HayatePoolSnapshot.ObjectDetails"/> 携带，一次快照覆盖全部存活包装对象
/// （空闲 + 借出）。字段为快照瞬间取值，不构成任何一致性保证（诊断用途）。
/// </summary>
public sealed class HayatePoolObjectDetail
{
    /// <summary>对象归属分片索引。</summary>
    public int ShardIndex { get; set; }

    /// <summary>快照瞬间是否处于借出状态。</summary>
    public bool IsBorrowed { get; set; }

    /// <summary>累计借出次数。</summary>
    public int LeaseCount { get; set; }

    /// <summary>创建时刻的挂钟时间戳（<see cref="DateTimeOffset.UtcNow.Ticks"/> 口径）。</summary>
    public long CreatedAtTick { get; set; }

    /// <summary>最近一次租约时长（毫秒；未归还时为最近一次记录值或 0）。</summary>
    public long LeaseTimeMs { get; set; }

    /// <summary>分代标记（0 年轻代 / 1 老年代，仅分代优化开启时升级）。</summary>
    public int Generation { get; set; }

    /// <summary>所属池的逻辑名。</summary>
    public string OwnerPoolName { get; set; }
}
