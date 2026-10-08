namespace Equator.App.Services;

/// <summary>Built-in sample content: the welcome notebook and the side-panel guide.</summary>
public static class Examples
{
    public static readonly string[] Welcome =
    [
        "# Welcome to Equator. Edit any cell and press Shift+Enter to evaluate it.\nintegrate(x^2*sin(x), x)",
        "diff(atan(x)*exp(2x), x)",
        "solve(x^3 - 6x^2 + 11x - 6 = 0, x)",
        "limit((1 + 1/n)^n, n, oo)",
        "series(tan(x), x, 0, 9)",
        "sum(1/k^2, k, 1, oo)",
        "integrate(1/(x^3 + 1), x)",
        "plot([sin(x), cos(x), sin(x)/x], x, -2pi, 2pi)",
        "A := [[2, 1, 0], [1, 3, 1], [0, 1, 2]];\neigenvalues(A)",
        "inv(A)",
        "dsolve(y'' + 2y' + 5y = 0, y, x)",
        "f(x) := x*exp(-x^2)\nintegrate(f(x), x, 0, oo)",
        "plot3d(sin(x)*cos(y), x, -pi, pi, y, -pi, pi)",
        "polarplot(1 + 2cos(3t), t, 0, 2pi)",
    ];

    public static readonly (string Title, string Body, string[] Examples)[] Guide =
    [
        ("Basics",
            "Write expressions in plain math notation. Multiplication can be implicit (2x, 3 sin(x)). Use := to assign, = for equations, and end a line with ; to hide its result. % refers to the previous output.",
            ["a := 3; b := 4;\nsqrt(a^2 + b^2)", "f(x) := x^3 - 2x\nf(2)", "f'(x)", "N(pi, 20)"]),
        ("Algebra",
            "Exact rational arithmetic, polynomial algebra and equation solving.",
            ["expand((x + y)^5)", "factor(x^4 - 16)", "simplify((x^2 - 1)/(x^2 + 2x + 1))", "apart((3x + 5)/((x + 1)*(x + 2)^2), x)",
             "solve(x^2 + 2x + 5 = 0, x)", "solve([x + y = 10, x*y = 21], [x, y])", "nsolve(cos(x) = x^2, x)",
             "factor(x^3 + y^3 + z^3 - 3x*y*z)", "gcd(x^4 - y^4, x^6 - y^6)", "solve(x^3 - 3x + 1 = 0, x)",
             "solve(x^2 - 4 < 0, x)", "solve(sqrt(x + 3) = x - 3, x)", "discriminant(x^3 + p*x + q, x)"]),
        ("Calculus",
            "Derivatives, integrals (indefinite and definite), limits, series and sums.",
            ["diff(sin(x)^3*log(x), x)", "integrate(exp(x)*cos(2x), x)", "integrate(sqrt(1 - x^2), x, -1, 1)",
             "limit(sin(3x)/x, x, 0)", "series(exp(sin(x)), x, 0, 6)", "sum(k^2, k, 1, n)", "gradient(x^2*y + y^3, [x, y])"]),
        ("Differential equations",
            "Write derivatives with primes. Add initial conditions as extra equations.",
            ["dsolve(y' + y = exp(x), y, x)", "dsolve([y'' + 4y = 0, y(0) = 1, y'(0) = 0], y, x)", "dsolve(y' = x*y^2, y, x)"]),
        ("Linear algebra",
            "Matrices are lists of rows. * multiplies matrices; ^ takes matrix powers.",
            ["M := [[1, 2], [3, 4]];\ndet(M)", "inv(M)", "M^3", "eigenvectors([[2, 0, 0], [0, 3, 4], [0, 4, 9]])",
             "linsolve([[1, 1, 1], [0, 2, 5], [2, 5, -1]], [6, -4, 27])", "rref([[1, 2, 1], [2, 4, 0], [3, 6, 1]])"]),
        ("Plotting",
            "Drag to pan, scroll to zoom, double-click to reset. 3D plots rotate when dragged.",
            ["plot(x^3 - 3x, x, -3, 3)", "plot(tan(x), x, -2pi, 2pi)", "parametricplot([sin(3t), sin(4t)], t, 0, 2pi)",
             "listplot(table(prime(n), n, 1, 40))", "plot3d(exp(-(x^2 + y^2))*cos(2x), x, -3, 3, y, -3, 3)"]),
        ("Numbers",
            "",
            ["factorint(2^32 + 1)", "isprime(2^61 - 1)", "100!", "binomial(50, 25)", "gcd(1071, 462)"]),
        ("Precision",
            "N(expr, digits) evaluates to any number of significant digits; identify recognises exact constants from decimals.",
            ["N(pi, 100)", "N(exp(pi*sqrt(163)), 40)", "N(zeta(3), 50)", "identify(N((1 + sqrt(5))/2, 40))", "identify(1.4142135623730951)"]),
        ("Assumptions",
            "Variables are complex unless you say otherwise. assume(x > 0) or assume(n, integer) refine results; forget(x) removes them.",
            ["assume(x > 0);\nsqrt(x^2)", "assume(n, integer);\nsin(n*pi) + cos(n*pi)", "assume(a > 0);\nintegrate(exp(-a*x^2), x, -oo, oo)",
             "simplify(log(x*y) - log(x) - log(y), x > 0, y > 0)", "is(x^2 + 1 > 0)", "assumptions()"]),
    ];
}
