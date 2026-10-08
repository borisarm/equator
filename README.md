# Equator

A notebook-style computer algebra system for Windows, in the spirit of Mathematica, Maple and MATLAB's
Symbolic Math Toolbox. The symbolic engine is written from scratch in C#; the front end is WinUI 3 with
Win2D typesetting and plotting.

```
In[1]:= integrate(x^2*sin(x), x)
Out[1]= −x² cos(x) + 2x sin(x) + 2 cos(x)

In[2]:= dsolve([y'' + y = 0, y(0) = 0, y'(0) = 1], y, x)
Out[2]= y(x) = sin(x)
```

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
  characteristic polynomial, linear systems; MATLAB-style `*` for matrix products and `A^n`.
- Number theory and statistics: factorisation (Pollard's ρ), primality (Miller–Rabin), gcd/lcm, binomials,
  ζ, mean/median/variance.
- Output as typeset math, linear text (re-parseable) and LaTeX.

**Notebook (`Equator.App`)**

- Cells with `In[n]:=` / `Out[n]=` numbering; Shift+Enter evaluates and advances.
- Native math typesetting: fractions, radicals, scripts, stretchy delimiters, matrices, ∫ ∑ lim.
- Interactive 2D plots (pan, zoom, crosshair readout), parametric, polar and list plots, and rotatable,
  shaded 3D surfaces.
- Function palette with search, a symbols palette, a variables inspector and a guide with clickable examples.
- Evaluation on a dedicated kernel thread with Abort; notebooks saved as `.eqnb` (JSON); copy any output as
  text, LaTeX or an image; light/dark themes; zoom.

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
fuzz `expand`, `together`, `cancel`, `factor`, `simplify` and `diff`.

The app is unpackaged and self-contained, so it needs no installer:

```powershell
dotnet publish src/Equator.App -c Release -p:Platform=x64 -o publish/win-x64
publish\win-x64\Equator.exe [notebook.eqnb]
```

Use `-p:Platform=ARM64` for ARM devices. Passing a notebook path on the command line opens and evaluates it.

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
src/Equator.App
  Rendering/MathLayout.cs box layout with Win2D font metrics (TeX-like spacing and metrics)
  Controls/               MathView, PlotView, SurfaceView, CellControl
  Services/KernelHost.cs  kernel thread (large stack, cooperative abort)
tests/Equator.Core.Tests  136 tests, including integrals verified by differentiating back
```

Design notes:

- Expressions are always canonical: sums and products are flattened and sorted, like terms and powers are
  combined, and numeric coefficients are exact. Only `-1` is distributed over sums (as Mathematica does),
  so factored forms such as `2(x + 1)` survive.
- The parser produces inert operator nodes (`@plus`, `@times`, …) that the kernel evaluates in order. This
  keeps matrix multiplication non-commutative while scalar algebra stays canonical.
- Calculus commands shadow their bound variable while evaluating their arguments, so `diff(x^2, x)` still
  works after `x := 7`.

## Limitations

This is a compact CAS, not a replacement for Mathematica or Maple. Integration is heuristic: there is no
Risch algorithm, and denominators that are irreducible over ℚ but split over the reals (e.g. `x^4 + 1`) are
not handled. Polynomial factorisation finds linear and quadratic factors over ℚ only. Floating-point
results use double precision. Equation solving returns principal solutions, not general families such as
`x = π/6 + 2kπ`.

## Diagnostics

Unhandled UI errors are logged to `%TEMP%\Equator\equator.log`. For automated visual checks, set
`EQUATOR_SNAPSHOT=<path.png>` (optionally `EQUATOR_SNAPSHOT_PAGES=n` and `EQUATOR_THEME=dark|light`); the
app then writes renders of its own window about seven seconds after start. Regenerate the icon with
`python tools/make_icon.py`.
