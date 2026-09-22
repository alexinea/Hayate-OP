// Assembly-level "fence" for this suite (same intent as tests/HayateOP.Tests/AssemblyInfo.cs and
// tests/legacy/HayateOP.Tests.Legacy/AssemblyInfo.cs, but for a different reason).
//
// DefaultHayateLoggerRenderingTests verifies the console logger's single-substitution-pass rendering by
// replacing the **process-wide** Console.Out with a StringWriter, writing one line, and asserting on the
// captured text. [Collection("Console")] only serialises the classes that opt into that collection; every
// other class in this assembly still runs concurrently, and in a Debug build those classes create pools
// whose DefaultHayateLogger writes to Console. A line written by any of them while the buffer is installed
// lands *in the buffer*, and the assertion then reads a foreign line as the rendered message.
//
// That is not a hypothetical: CI failed on EveryLevel_ShouldRenderThroughTheSameSinglePass with
// Actual = "[Shard 0] Object added to shard (current size: 1)" (a pool pre-warm line from another class),
// and a local probe with one class writing to Console while another captured it reproduced it on 5 of 5
// captures. Widening the fence is not an option: in a Debug build *every* class that creates a pool is a
// potential writer, so "add them all to the collection" degenerates into "run serially" anyway.
//
// Hence: disable test parallelism at the **assembly level**. The suite is small (38 cases, well under a
// second), so serial execution costs nothing measurable, and it is the only fence that actually covers
// every current and future writer of Console.
//
// Generation note (FUTURE): this file belongs to tests/HayateOP.Tests.DI (future generation, xunit v3 + MTP).
// In v3, v2's CollectionBehavior.DisableTestParallelization is obsolete (compiler error CS0619), so
// assembly-level parallelism disabling uses v3's Xunit.v3.Parallelization(Mode = ParallelMode.None).
// The v2 syntax is in tests/legacy/HayateOP.Tests.Legacy/AssemblyInfo.cs. There is no counterpart here:
// the legacy DI suite (tests/legacy/HayateOP.Tests.DI.Legacy) has no Console-capturing case, so it needs
// no fence.
using Xunit.v3;

// v3: disable all parallelism at the assembly level (equivalent to v2's DisableTestParallelization)
[assembly: Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]
