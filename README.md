# Equator

A worksheet-style computer algebra system for Windows. The symbolic engine is written from scratch in C#;
the front end is WinUI 3 with Win2D typesetting and plotting.

```
(1)  integrate(x^2*sin(x), x)
     → −x² cos(x) + 2x sin(x) + 2 cos(x)                                   %1

(2)  dsolve([y'' + y = 0, y(0) = 0, y'(0) = 1], y, x)
     → y(x) = sin(x)                                                       %2
```

[![Microsoft Store](https://img.shields.io/badge/Microsoft%20Store-Equator-0067b8?logo=microsoft)](https://apps.microsoft.com/detail/9P7WTJNFC7J8)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

Available on the **Microsoft Store**: <https://apps.microsoft.com/detail/9P7WTJNFC7J8> (free trial).

## Features

**Engine (`Equator.Core`)**

- Exact arithmetic: arbitrary-precision rationals, surds (`sqrt(12)` → `2√3`), complex numbers, π, e, ∞.
- Arbitrary-precision floating point: `N(expr, digits)` to any number of significant digits (elementary
  functions, Γ, erf, ζ, complex values on principal branches); long decimal literals keep their digits;
  `identify(x)` recognises exact constants (rationals, algebraic numbers, combinations of π, e, logs) by PSLQ.
- Algebra: `expand`, complete `factor`ing over ℚ of univariate and multivariate polynomials (Zassenhaus with
  Hensel lifting; multivariate Hensel lifting with Wang's leading-coefficient trick), polynomial
  `gcd`/`lcm`, `sqrfree`, `polydiv`/`quo`/`rem`, `resultant`, `discriminant`, `simplify` (rational,
  trigonometric incl. angle addition, logarithmic, radical denesting, rationalised denominators),
  `together`, `cancel`, `apart`, `collect`, `subs`.
- Solving: exact polynomial roots (rational, quadratic, cubic by Cardano with the trigonometric form for
  three real roots, binomials, cyclotomic factors, biquadratics; numeric otherwise), inverse-function
  isolation, substitution (`e^(2x) − 3e^x + 2 = 0`), radical equations by resultant elimination, rational
  trigonometric equations via tan(x/2), real inequalities and chains (`solve(x^2 - 4 < 0, x)`), linear and
  nonlinear systems, numeric root finding.
- Calculus: derivatives (including mixed partials, `f'(x)` for user functions), indefinite integrals
  (table, partial fractions including irreducible factors of any degree with exact roots, substitution, by
  parts, trigonometric powers, rational trigonometric functions, quadratic radicals); every antiderivative is
  verified by differentiation. Definite integrals detect divergence and split at singularities and jumps.
  Limits (L'Hôpital, growth comparison via logarithms and Stirling, bounded·0, one-sided, a 150-digit numeric
  fallback with exact identification), Taylor/Laurent series, finite and infinite sums (closed forms,
  telescoping, ζ(p), alternating p-series), gradient / Hessian / Jacobian / divergence / Laplacian.
- Assumptions: `assume(x > 0)`, `assume(n, integer)`, `forget`, `refine`, `is`, `simplify(expr, x > 0)`.
  Symbols are complex unless declared otherwise; assumptions refine radicals, `abs`, `sign`, `log`, powers
  of −1, trigonometric values at multiples of π, solution sets and parametric integrals. Integration and
  limit variables range over the reals, summation indices over the integers.
- ODEs: separable and linear first-order equations, linear constant-coefficient equations of any order
  (variation of parameters for the forcing term), initial conditions.
- Linear algebra: exact determinants, inverses, RREF, rank, null space, eigenvalues/eigenvectors,
  characteristic polynomial, linear systems; MATLAB-style `*` for matrix products and `A^n`. Factorisations
  `lu`, `qr`, `cholesky`, `svd` (exact when the eigenvalues of AᵀA are, numerical otherwise), `singularvalues`,
  `cond`; `diagonalize`, `jordan` (Jordan chains from the kernels of (A − λI)^k), `minpoly`, `expm(A, t)` (from
  the Jordan form, with sines and cosines for real matrices); `gramschmidt`, `columnspace`, `rowspace`, `proj`,
  `pinv` (from a rank factorisation) and `lsq`, `adjugate`.
- Number theory and statistics: factorisation (Pollard's ρ), primality (Miller–Rabin), gcd/lcm, binomials,
  ζ, mean/median/variance.
- Output as typeset math, linear text (re-parseable) and LaTeX.

**Notebook (`Equator.App`)**

- Each cell is a step: a numbered badge marks steps that have run, and the result appears in an answer panel
  under the input, with a `%n` chip that inserts a reference to it. Shift+Enter runs a cell and moves on.
- Native math typesetting: fractions, radicals, scripts, stretchy delimiters, matrices, ∫ ∑ lim.
- Interactive 2D plots (pan, zoom, crosshair readout), parametric, polar and list plots, and rotatable,
  shaded 3D surfaces.
- Function palette with search, a symbols palette, a variables inspector and a guide with clickable examples.
- Calculations run on a dedicated engine thread and can be stopped; notebooks saved as `.eqnb` (JSON); copy any output as
  text, LaTeX or an image; light/dark themes; zoom.
- Eight interface languages: English, Simplified Chinese, Hindi, Spanish, French, Arabic (right to left), Bengali
  and Brazilian Portuguese. The default follows Windows; the Language menu switches live. Menus, the guide,
  function descriptions, error messages, `check()` reports and `help()` are translated; command names and maths
  are the same in every language, so notebooks work everywhere.

**Checking worked solutions**

- `check(["2x + 3 = 7", "2x = 4", "x = 2"])` checks a worked solution step by step with the CAS. The first line
  is the task:
  - an equation or system: each line must have the same real solution set as the previous one. Lost
    solutions (dividing by an expression that can be zero, forgetting ±) are errors; extra roots introduced by
    squaring are warnings that must be discarded by the final answer;
  - an inequality: the solution sets must agree (e.g. forgetting to flip the sign is caught);
  - an expression, derivative, integral or limit: each line must be equal to the previous one (integrals
    up to a constant of integration), with a concrete counterexample when it is not.
  - a matrix: Gaussian elimination by hand. Each matrix must be row-equivalent to the previous one; a line may
    name its operations before a colon (`"R2 → R2 - 3R1, R3 → R3 + R1: [[...]]"`, also `-3R1 + R2 → R2`,
    `R1 ↔ R2`, `F`/`L` row names, `|` in augmented matrices), which are then applied and compared entry by entry.
    Unnamed operations are recognised and described; a wrong row is reported with the operation it was probably
    meant to be, and the summary says whether the result is in (reduced) row echelon form.
- Later steps are checked against the previous line, and the final answer against the correct solution.
  `checklatex([...])` takes the steps in LaTeX.

## Building and running

Requirements: Windows 10 1904x or later, .NET 10 SDK. NuGet restores the Windows App SDK and Win2D.

```powershell
dotnet build Equator.slnx
dotnet run --project src/Equator.App          # or open Equator.slnx in Visual Studio
dotnet test tests/Equator.Core.Tests
```

Besides expected-output tests, the suite checks results against independent computations (oracle tests):
definite integrals against quadrature, antiderivatives by differentiation, `solve` results by substitution,
series by their truncation order, inequalities by direct evaluation, random factorisations by re-expansion
and irreducibility, arbitrary-precision values against reference digits and machine evaluation, and
refinements under random assumptions by evaluation at points satisfying them. Seeded random expressions
fuzz `expand`, `together`, `cancel`, `factor`, `simplify` and `diff`. Matrix factorisations are checked by
their defining properties (the factors multiply back, Q and U are orthogonal, the Penrose conditions hold).

`tests/Equator.Core.Tests/Data/openstax.tsv` holds about 2,500 exercises with the books' own answers from the
OpenStax algebra textbooks (CC BY 4.0), converted from their CNXML sources by `tools/openstax_corpus.py`:
equations, inequalities and systems are solved, polynomials factored and expressions simplified, and each
result is compared with the book's answer (solution sets numerically, factorisations by factor count,
simplifications by value and size). `openstax-known.tsv` lists the exceptions with their reasons.

By default the app builds unpackaged and self-contained, so it runs from the build folder with no installer:

```powershell
dotnet publish src/Equator.App -c Release -p:Platform=x64 -o publish/win-x64
publish\win-x64\Equator.exe [notebook.eqnb]
```

Use `-p:Platform=ARM64` for ARM devices. Passing a notebook path on the command line opens and evaluates it.

### Microsoft Store package (MSIX)

```powershell
.\tools\package.ps1 -Version 1.0.1.0      # omit -Version to keep the manifest's version
```

This builds x64 and ARM64 MSIX packages (`-p:EquatorPackaged=true`), bundles them with `makeappx`, and writes
`AppPackages\Equator_<version>.msixupload`, the file to upload in Partner Center. Visual Studio is not
needed; the Windows 10/11 SDK provides `makeappx`. The packages are unsigned because the Store signs them.

- `src/Equator.App/Package.appxmanifest` holds the Store identity (`25433BorisArmenta.EquatorSolver`, publisher
  `CN=991E5DC7-7C6A-44B8-9614-FDF783EE4452`, display name `EquatorSolver`, the reserved name), the `.eqnb` file
  association, and the only capability, `runFullTrust` (a desktop app). The app does not use the network.
- The packaged app depends on the Windows App SDK runtime (`Microsoft.WindowsAppRuntime.2`), which the Store
  installs automatically; .NET is included in the package.
- Logo assets in `src/Equator.App/Assets/Package` are generated by `python tools/make_store_assets.py` from the
  same artwork as the icon.
- Every submission needs a higher version, and its last field must be 0.

Submission checklist (Partner Center):

1. Restricted capability `runFullTrust`: explain that Equator is a WinUI 3 desktop (Win32) application.
2. Before uploading, run the Windows App Certification Kit from an elevated prompt:
   `& "${env:ProgramFiles(x86)}\Windows Kits\10\App Certification Kit\appcert.exe" test -appxpackagepath AppPackages\bundle\Equator.App_<version>_x64.msix -reportoutputpath wack.xml`

To try a package locally with Developer Mode on, unzip one of the `.msix` files into a folder and run
`Add-AppxPackage -Register <folder>\AppxManifest.xml`; remove it with
`Get-AppxPackage 25433BorisArmenta.EquatorSolver | Remove-AppxPackage`.

## Language reference

| Syntax | Meaning |
| --- | --- |
| `x := 2`, `f(x, y) := x^2 + y` | assignment / function definition |
| `x^2 + 3x = 7` | equation (`==` tests equality) |
| `2x`, `3 sin(x)`, `(x+1)(x-1)` | implicit multiplication |
| `pi`, `e`, `I`, `oo` (or `π`, `∞`) | constants |
| `[1, 2, 3]`, `[[1, 2], [3, 4]]` | lists, matrices (`{…}` also accepted); `A[i, j]` indexes from 1 |
| `A * B`, `A^-1`, `u . v` | matrix product, inverse, dot product |
| `y'`, `y''`, `f'(2)` | derivatives in ODEs / of user functions |
| `%`, `%%`, `%5`, `ans` | previous outputs |
| `expr;` | evaluate without showing the result |
| `# comment` | comment |

Type `help()` for the command list or `help(integrate)` for details. The **Guide** tab has runnable examples.

## Architecture

```
src/Equator.Core
  Expr.cs                 immutable expression tree; canonical constructors (Plus, Times, Power)
  Functions.cs            automatic rules for elementary functions (exact values, symmetries)
  Parsing/Parser.cs       tokenizer + precedence parser → raw @-operator tree
  Engine/Kernel.cs        evaluator: variables, user functions, history, bound-variable scoping
  Engine/Builtins.cs      command library with help metadata (drives help() and the UI palette)
  Algebra/                expansion, polynomials over ℚ (MPoly, PolyRing), gcd, factorisation
                          (ZFactor, PolyFactor), rational functions, simplify, solve, inequalities,
                          linear algebra
  Calculus/               derivative, integrate, limits, series & sums, ODEs
  Numerics/               complex evaluation, compilation to delegates, quadrature, root finding,
                          arbitrary precision (BigFloat, BigMath, BigEval), PSLQ, numeric self-checks
  Engine/Assumptions.cs   assumption sets and sign/range inference by interval arithmetic
  Printing/               display tree → plain text, LaTeX, and typesetting boxes
  Parsing/LatexConverter  LaTeX → Equator input lines (used by checklatex)
  Checking/WorkChecker    step-by-step verification of worked solutions
  Messages.cs             every message shown to people, as English templates a front end can translate
src/Equator.App
  Rendering/MathLayout.cs box layout with Win2D font metrics (TeX-like spacing and metrics)
  Controls/               MathView, PlotView, SurfaceView, CellControl
  Services/KernelHost.cs  kernel thread (large stack, cooperative abort)
  Localization/           string lookup (Loc), XAML bindings (Tr), language choice
  Strings/<lang>.json     interface strings and function descriptions, one table per language
  Strings/Engine/         engine messages per language, keyed by the English template
tests/Equator.Core.Tests  unit and oracle tests (results checked against independent computations)
```

Design notes:

- Expressions are always canonical: sums and products are flattened and sorted, like terms and powers are
  combined, and numeric coefficients are exact. Only `-1` is distributed over sums,
  so factored forms such as `2(x + 1)` survive.
- The parser produces inert operator nodes (`@plus`, `@times`, …) that the kernel evaluates in order. This
  keeps matrix multiplication non-commutative while scalar algebra stays canonical.
- Calculus commands shadow their bound variable while evaluating their arguments, so `diff(x^2, x)` still
  works after `x := 7`.
- Engine text goes through `Messages.T("Index {0} is out of range 1..{1}.", i, n)`; the app installs a
  translator keyed by the English template. `TranslationTests` fails when a message or interface string is
  added without a translation in every language, or when placeholders differ.

## Limitations

This is a compact CAS. Integration is heuristic (verified, but
there is no Risch algorithm). Quartics and higher-degree irreducible polynomials have numeric roots unless
they are binomial or cyclotomic. Equation solving returns principal solutions, not general families such as
`x = π/6 + 2kπ`.

The solution checker works over the real numbers and handles equations, systems, inequalities in one variable
and expression transformations, one step per line, not proofs in prose.

## Diagnostics

Unhandled UI errors are logged to `%TEMP%\Equator\equator.log`. For automated visual checks, set
`EQUATOR_SNAPSHOT=<path.png>` (optionally `EQUATOR_SNAPSHOT_PAGES=n`, `EQUATOR_SNAPSHOT_DELAY=seconds`,
`EQUATOR_THEME=dark|light`, `EQUATOR_LANG=<code>` (e.g. `ar`), `EQUATOR_WINDOW_SIZE=WxH`, `EQUATOR_ZOOM=1.2` and
`EQUATOR_SIDE_TAB=guide|variables`); the app then writes renders of its own window about seven seconds after
start. The 7-day free trial comes from the Store license and only applies to Store installs; to see its banner and
the trial-ended state in a development build, set `EQUATOR_LICENSE=trial:<days>` or `EQUATOR_LICENSE=expired`
(*Buy* then simulates a purchase). `store/` holds the Microsoft Store listing texts, screenshots and submission notes. Regenerate the icon with
`python tools/make_icon.py`.
