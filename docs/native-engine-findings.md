# Native engine evaluation: findings and decision

**Decision (2026-11-11): Equator stays on the C# engine (`Equator.Core`).** A complete C++20 port of the engine,
exposed to the app through C++/WinRT, was built, tested and measured. It was not adopted: it is slower where it
matters most. The full record is on the unmerged branch `migration/core-cpp-winrt`: the plan, an execution log with
every command and result, measurement reports (`docs/migration/measurements/`), and the tools.

## What was built and verified

- A native engine, a Windows Runtime component and a C# projection, for x64 and ARM64. The app ran on them,
  unpackaged and as an installed MSIX, on both architectures.
- Every one of the 4,693 cases of `Equator.Core.Tests`, including the 2,524 OpenStax exercises (same 31 known
  failures), ported to native and interop tests that pass in Debug and Release on x64 and ARM64. A script
  compared the two suites method by method: no discrepancies.

So the native engine matched the C# one in behaviour. The question was whether it was better.

## Measurements

Release builds, both engines in one process, timing `Evaluate` on a fresh kernel. Ratio = native time ÷ C# time,
so above 1 means native is slower.

| | x64 | ARM64 |
| --- | ---: | ---: |
| 30 typical cells, after .NET's tiered JIT has fully optimised the C# code (geometric mean) | **2.89** | **2.32** |
| 2,528 OpenStax exercises, same conditions | 2.72 | 2.17 |
| 30 typical cells, first evaluation in a new process | 0.15 | 0.15 |
| 2,528 OpenStax exercises, first pass in a new process | 0.45 | 0.46 |

- **Steady state (most of a session): the C# engine is 2.3 to 2.9 times faster.** The exception is arithmetic
  on very large integers, where both are about equal. Printing a number with tens of thousands of digits is
  4.5 times slower natively (Boost's decimal conversion and `gcd`).
- **First use of a feature: native is much faster.** The C# engine spends 20 to 230 ms compiling the code a
  cell needs the first time (JIT); native needs about 5 ms.
- **Why native is slow:** a sampling profile put about 45 % of native time in the Windows heap. The engine
  allocates and frees every expression node, child list and big-integer buffer individually, while .NET
  allocates by bumping a pointer and frees in bulk. A faster system allocator (the segment heap) gained only 14 %.
- **Other native regressions:**
  - about 2.6 KB of stack per nesting level, against 0.6 to 1.7 KB in C#, so roughly 190,000 nested brackets
    fit on the 512 MiB engine thread against 390,000 to 900,000;
  - Stop takes about 1.3 % of the time the cell had been running, to free its partial results, where C#
    returns at once and leaves that to the GC;
  - a higher peak of private memory.
- **Equal or better natively:** cancellation checks themselves, absence of leaks, and plot sampling (the batch
  calls were 5 to 37 % faster than the C# delegates).

## Why the C# engine stays

- Speed was the reason for the migration, and in steady state the native engine is slower.
- Its one clear advantage, no JIT on first use, is worth little in practice. Precompiling the C# app with
  ReadyToRun recovers most of it, and even that was not worth its size (below).
- A second language, the C++ toolchain, the C runtime setup and WinRT registration would weigh on every build.
- The iOS port that was planned at the time is .NET (Uno Platform), where the C# engine runs as is; the native
  engine would have needed new bindings. (The macOS and iOS ports were shelved when the project was paused,
  also on 2026-11-11; see the README's *Project status*.)

## ReadyToRun, measured and not adopted

ReadyToRun (`<PublishReadyToRun>true</PublishReadyToRun>`) precompiles the published assemblies to native code,
so less is JIT-compiled at startup and on first use. It was measured on 2026-11-11 (x64, medians) and not
adopted.

| | JIT only | ReadyToRun |
| --- | ---: | ---: |
| Store package (MSIX): window appears | 530 ms | 511 ms (−4 %) |
| Store package: welcome notebook evaluated | 1,018 ms | 980 ms (−4 %) |
| Unpackaged folder: window appears | 421 ms | 361 ms (−14 %) |
| Unpackaged folder: welcome notebook evaluated | 813 ms | 699 ms (−14 %) |
| Engine, first use of a feature (new process, per cell) | 22 to 92 ms | 8 to 49 ms |
| Engine, warm cells after tiered optimisation | | 0 to 10 % slower |
| x64 Store package | 63.4 MB | 77.9 MB (+23 %) |

- Most of the size is the Windows SDK and WinUI projections. Leaving them out of ReadyToRun kept the size down
  (+9 MB unpackaged) but also halved the launch gain.
- In the Store package the gain is about 40 ms at launch, and 20 to 40 ms on the first use of each feature:
  hardly noticeable, for a 23 % larger download per architecture. Reconsider only if startup becomes a complaint.

## If a native engine is reconsidered

Start from the branch. To be competitive it would need:

- far fewer allocations: inline storage for small child lists and small integers, pooled nodes, fewer temporaries;
- a faster large-integer decimal conversion and `gcd`.

Then re-run its measurement tool (`tools/migration/Measure`) against the C# engine, steady and cold.
