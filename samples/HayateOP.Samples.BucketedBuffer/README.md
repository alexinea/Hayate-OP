# HayateOP.Samples.BucketedBuffer

> **Bucketed Buffer Pool** — `HayateBufferPool<T>` is an optional, fixed ladder of size buckets. Each
> rent routes (round-up to the nearest size) to the smallest bucket that fits, and a returned array parks
> in its home bucket until that bucket's capacity is full.

## What this sample demonstrates

`HayateBufferPool<byte>` built from a 3-bucket ladder (256 / 1024 / 4096 bytes). It shows:

- The declared ladder is exposed and ordered ascending by size.
- `Rent(300)` rounds **up** to the 1024-byte bucket — you never receive a smaller array, and the pool
  never silently grows a bucket.
- A request above the largest bucket (`Rent(5000)`) is **rejected** with `ArgumentException` rather than
  served by an undeclared bucket.

## When to use this pattern

- You rent arrays of a *few known sizes* and want that shape under an explicit, inspectable configuration
  (the ladder is declared up front, every bucket's retention is fixed by declaration).
- You want an `ArrayPool<T>`-like primitive but owned by HayateOP naming/validation conventions, with no
  background worker and no hidden resizing.

## How to run

```bash
dotnet run --project samples/HayateOP.Samples.BucketedBuffer
```

## Walkthrough

| Step | Concept | Key API |
| --- | --- | --- |
| Declare the ladder | fixed buckets, ascending size | `new HayateSegmentDefinition(size, capacity)` |
| Construct the pool | one required bucket + any number of extras | `new HayateBufferPool<T>(segment, params segment[])` |
| Rent a rounded-up size | route to smallest fitting bucket | `HayateBufferPool<T>.Rent(long)` |
| Return via `using` | parks in home bucket, idempotent | `PooledBuffer<T>.Dispose()` |
| Oversize rent | rejected, ladder stays exact | `ArgumentException` |

## Key APIs used

- `DotNetCore.HayateOP.Buffers.HayateBufferPool<T>`
- `DotNetCore.HayateOP.Buffers.HayateSegmentDefinition`
- `DotNetCore.HayateOP.Buffers.PooledBuffer<T>` (`.Array`, `.Length`, `Dispose()`)

## Notes & gotchas

- Buckets cap only what they **retain**: any number of buffers may be outstanding at once; returns past a
  bucket's `Capacity` are dropped for the GC. There is no background worker of any kind.
- The handle (`PooledBuffer<T>`) exists so the `using` pattern returns the buffer; the pool never takes a
  bare array back, because it could not tell which bucket a caller-modified array belongs to.
- A request above the largest declared size is rejected — the ladder is exactly what was configured.
- The buffer's previous contents are **not** cleared on return (same contract as `ArrayPool<T>.Shared`):
  inspect only the elements you wrote.
