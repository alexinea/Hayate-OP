using System;

namespace DotNetCore.HayateOP;

public class HayateObject<T> where T : class
{
    public T Value { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime LastBorrowedAt { get; set; }
    public DateTime LastReturnedAt { get; set; }
    public string BorrowTrace { get; set; }
    public bool IsBorrowed { get; set; }
    public int Generation { get; set; } // 0 年轻代 1 老年代
    
    public int ValidationSkipCount { get; set; } // 老年代跳过验证计数
    
    public long LeaseTimeMs { get; set; }

    public HayateObject(T value)
    {
        Value = value ?? throw new ArgumentNullException(nameof(value));
        CreatedAt = DateTime.UtcNow;
        LastReturnedAt = DateTime.UtcNow;
    }
}