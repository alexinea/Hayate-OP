// HayateOP sample: borrow order (Fifo vs Lifo)
// Build & run:  dotnet run --project samples/HayateOP.Samples.BorrowOrder
//
// Maps to the README feature "Borrow Order": the idle list can be served Fifo (oldest returned first,
// the default) or Lifo (most recently returned first) through HayateBorrowStrategy, set with
// WithBorrowStrategy.
using System;
using System.Collections.Generic;
using DotNetCore.HayateOP;
using DotNetCore.HayateOP.Samples.BorrowOrder;

Console.OutputEncoding = System.Text.Encoding.UTF8;

// NOTE: this file uses top-level statements, so the implicit `Program` lives in the GLOBAL namespace.
// Both `using` directives above must stay: `DotNetCore.HayateOP` brings in the pool types
// (HayatePoolBuilder<T>, HayateBorrowStrategy, IHayateObjectPool<T>), and
// `DotNetCore.HayateOP.Samples.BorrowOrder` brings in the `Widget` pooled type.

// A single-shard pool (one ordered idle list) makes the borrow order deterministic: with more than one
// shard the order would be decided per shard by affinity, which would obscure the Fifo/Lifo effect.
//
// CreateOnDemand is used on purpose: the default BlockTimeout policy lets a borrow on a non-empty pool
// WAIT for a return rather than creating a new object, which would make this sequential Fifo/Lifo
// demonstration block. CreateOnDemand still REUSES returned objects (so the order is observable), it
// only creates when the pool genuinely has room.
IHayateObjectPool<Widget> Build(HayateBorrowStrategy strategy) =>
    new HayatePoolBuilder<Widget>()
        .WithMinSize(0)
        .WithMaxSize(8)
        .WithEnableSharding(false)       // one shard => one ordered idle list
        .WithRejectPolicy(HayatePoolRejectPolicy.CreateOnDemand)
        .WithBorrowStrategy(strategy)
        .Build();

void Demo(string title, HayateBorrowStrategy strategy)
{
    Console.WriteLine($"=== {title} ===");
    var pool = Build(strategy);

    // Create three distinct widgets and return them in creation order: idle list becomes [a, b, c]
    // (a is the oldest, c the most recent).
    var a = pool.Acquire();
    var b = pool.Acquire();
    var c = pool.Acquire();
    Console.WriteLine($"   created widgets #{a.Id}, #{b.Id}, #{c.Id}");
    pool.Release(a);
    pool.Release(b);
    pool.Release(c);

    // The next borrow hands back the head of the idle list (Fifo) or its tail (Lifo).
    var next = pool.Acquire();
    var expected = strategy == HayateBorrowStrategy.Fifo ? a.Id : c.Id;
    Console.WriteLine($"   next borrow returned widget #{next.Id}  (expected #{expected})");
    pool.Release(next);
    pool.Dispose();
}

Demo("Fifo  — oldest returned object handed out first (default)", HayateBorrowStrategy.Fifo);
Demo("Lifo  — most recently returned object handed out first", HayateBorrowStrategy.Lifo);

Console.WriteLine();
Console.WriteLine("Borrow-order sample completed.");
