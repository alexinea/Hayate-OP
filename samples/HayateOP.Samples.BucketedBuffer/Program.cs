// HayateOP sample: bucketed buffer pool (HayateBufferPool<T>)
// Build & run:  dotnet run --project samples/HayateOP.Samples.BucketedBuffer
//
// Maps to the README feature "Bucketed Buffer Pool": an optional HayateBufferPool<T> is a fixed ladder
// of size buckets. Each rent routes (round-up to the nearest size) to the smallest bucket that fits, and
// a returned array parks in its home bucket until that bucket's capacity is full.
using System;
using DotNetCore.HayateOP.Buffers;

Console.OutputEncoding = System.Text.Encoding.UTF8;

// NOTE: top-level statements => global-namespace Program. `DotNetCore.HayateOP.Buffers` brings in
// HayateBufferPool<T>, HayateSegmentDefinition and PooledBuffer<T>.

// Declare a 3-bucket ladder. The pool orders the buckets ascending by size regardless of the argument
// order, so routing always picks the smallest bucket that can serve a request.
using var pool = new HayateBufferPool<byte>(
    new HayateSegmentDefinition(256, 4),    // bucket 0: arrays of exactly 256 bytes, retain up to 4
    new HayateSegmentDefinition(1024, 2),   // bucket 1: arrays of exactly 1024 bytes, retain up to 2
    new HayateSegmentDefinition(4096, 1));  // bucket 2: arrays of exactly 4096 bytes, retain 1

Console.WriteLine("=== Declared bucket ladder (ascending size) ===");
foreach (var seg in pool.Segments)
{
    Console.WriteLine($"   bucket: {seg.ArraySize} bytes, retains up to {seg.Capacity}");
}

// Rent 300 bytes -> routes to the 1024 bucket (round up, never grow). The handle exposes the pooled T[].
Console.WriteLine("=== Rent / return ===");
using (var buf = pool.Rent(300))
{
    Console.WriteLine($"   Rent(300) -> array length {buf.Length} (rounded up to the 1024-byte bucket)");
    buf.Array[0] = 0xAB;   // write through the handle's Array property
} // Dispose returns the buffer to its home bucket (idempotent: a second Dispose is a no-op)

// Rent 5000 bytes -> above the largest declared bucket -> rejected; the pool never serves an undeclared
// size, keeping the ladder exactly what was configured.
Console.WriteLine("=== Oversize request ===");
try
{
    pool.Rent(5000);
    Console.WriteLine("   ERROR: oversize rent was accepted (it should have been rejected)");
}
catch (ArgumentException ex)
{
    Console.WriteLine($"   Rent(5000) rejected as expected: {ex.GetType().Name}");
}

Console.WriteLine();
Console.WriteLine("Bucketed buffer pool sample completed.");
