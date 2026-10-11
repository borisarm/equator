using Equator.Core.Checking;

namespace Equator.Core.Tests;

public class WorkCheckerTests
{
    private static WorkReport Check(params string[] lines) => WorkChecker.Check(lines);

    private static void AssertVerdicts(WorkReport r, params Verdict[] expected) =>
        Assert.True(expected.SequenceEqual(r.Steps.Select(s => s.Verdict)), "Verdicts were: " + string.Join(", ", r.Steps.Select(s => $"{s.Verdict} ({s.Message})")) + "\n" + r.Summary);

    // ---- equations ----------------------------------------------------------------------------------

    [Fact]
    public void CorrectLinearSolution()
    {
        var r = Check("2x + 3 = 7", "2x = 4", "x = 2");
        AssertVerdicts(r, Verdict.Given, Verdict.Correct, Verdict.Correct);
        Assert.Equal(Verdict.Correct, r.Overall);
    }

    [Fact]
    public void ArithmeticSlipIsFoundAndLaterStepsFollowTheirOwnPremise()
    {
        var r = Check("2x + 3 = 7", "2x = 10", "x = 5");
        AssertVerdicts(r, Verdict.Given, Verdict.Incorrect, Verdict.Correct);
        Assert.Equal(Verdict.Incorrect, r.Overall);
        Assert.Contains("x = 2", r.Summary);
    }

    [Fact]
    public void FactoringAndAlternatives()
    {
        AssertVerdicts(Check("x^2 - 5x + 6 = 0", "(x - 2)(x - 3) = 0", "x = 2 or x = 3"), Verdict.Given, Verdict.Correct, Verdict.Correct);
        AssertVerdicts(Check("x^2 = 16", "x = ±4"), Verdict.Given, Verdict.Correct);
        AssertVerdicts(Check("x^2 = 16", "x = 4, x = -4"), Verdict.Given, Verdict.Correct);
    }

    [Fact]
    public void LostSolutionsAreErrors()
    {
        var r = Check("x^2 = 2x", "x = 2"); // divided by x
        AssertVerdicts(r, Verdict.Given, Verdict.Incorrect);
        Assert.Contains("x = 0", r.Steps[1].Message);
        AssertVerdicts(Check("x^2 = 9", "x = 3"), Verdict.Given, Verdict.Incorrect);
    }

    [Fact]
    public void ExtraneousRootsFromSquaringWarnUntilDiscarded()
    {
        var r = Check("sqrt(x + 3) = x - 3", "x + 3 = (x - 3)^2", "x^2 - 7x + 6 = 0", "x = 1 or x = 6", "x = 6");
        AssertVerdicts(r, Verdict.Given, Verdict.Warning, Verdict.Correct, Verdict.Correct, Verdict.Correct);
        Assert.Contains("discards x = 1", r.Steps[4].Message);
        Assert.Equal(Verdict.Warning, r.Overall);
        // Keeping the extraneous root in the final answer is wrong.
        var kept = Check("sqrt(x + 3) = x - 3", "x + 3 = (x - 3)^2", "x = 1 or x = 6");
        Assert.Equal(Verdict.Incorrect, kept.Overall);
    }

    [Fact]
    public void ChainedRightHandSidesAreChecked()
    {
        AssertVerdicts(Check("2x + 3 = 7", "2x = 7 - 3 = 4", "x = 2"), Verdict.Given, Verdict.Correct, Verdict.Correct);
        var r = Check("2x + 3 = 7", "2x = 7 - 3 = 5", "x = 5/2");
        Assert.Equal(Verdict.Incorrect, r.Steps[1].Verdict);
    }

    [Fact]
    public void Systems()
    {
        AssertVerdicts(Check("x + y = 3, x - y = 1", "2x = 4, x - y = 1", "x = 2, y = 1"), Verdict.Given, Verdict.Correct, Verdict.Correct);
        Assert.Equal(Verdict.Incorrect, Check("x + y = 3, x - y = 1", "x = 2, y = 2").Overall);
    }

    [Fact]
    public void NoRealSolution()
    {
        var r = Check("x^2 = -4", "no solution");
        AssertVerdicts(r, Verdict.Given, Verdict.Correct);
        Assert.Contains("no real solution", r.Summary);
        Assert.Equal(Verdict.Incorrect, Check("x^2 = -4", "x = 2").Overall);
    }

    [Fact]
    public void RationalEquationWithExcludedValue()
    {
        AssertVerdicts(Check("3/(x - 1) = 1", "3 = x - 1", "x = 4"), Verdict.Given, Verdict.Correct, Verdict.Correct);
        // x = 1 makes the original undefined: keeping it is wrong.
        Assert.Equal(Verdict.Incorrect, Check("x/(x - 1) = 1/(x - 1)", "x = 1").Overall);
    }

    [Fact]
    public void UnsolvedLastLineIsNotedButCorrect()
    {
        var r = Check("2x + 3 = 7", "2x = 4");
        Assert.Equal(Verdict.Correct, r.Overall);
        Assert.Contains("not yet solved", r.Summary);
    }

    // ---- transformations ----------------------------------------------------------------------------

    [Fact]
    public void ExpressionTransformations()
    {
        AssertVerdicts(Check("(x + 1)^2 - (x - 1)^2", "x^2 + 2x + 1 - x^2 + 2x - 1", "4x"), Verdict.Given, Verdict.Correct, Verdict.Correct);
        var r = Check("(x + 1)^2", "x^2 + 1");
        AssertVerdicts(r, Verdict.Given, Verdict.Incorrect);
        Assert.Contains("versus", r.Steps[1].Message); // a concrete counterexample
        Assert.Contains("(x + 1)^2", r.Summary);
    }

    [Fact]
    public void ContinuationLinesAndChains()
    {
        AssertVerdicts(Check("(a + b)^2", "= a^2 + 2a*b + b^2"), Verdict.Given, Verdict.Correct);
        AssertVerdicts(Check("(x + 2)(x - 2) = x^2 - 4"), Verdict.Correct);
        Assert.Equal(Verdict.Incorrect, Check("(x + 2)(x - 2) = x^2 - 4 = x^2 + 4").Overall);
    }

    [Fact]
    public void DerivativesAndIntegrals()
    {
        AssertVerdicts(Check("diff(x^2*sin(x), x)", "2x*sin(x) + x^2*cos(x)"), Verdict.Given, Verdict.Correct);
        Assert.Equal(Verdict.Incorrect, Check("diff(x^2*sin(x), x)", "2x*cos(x)").Overall);
        // Integration by parts with an unevaluated integral, and a constant of integration.
        var r = Check("integrate(x*exp(x), x)", "x*exp(x) - integrate(exp(x), x)", "x*exp(x) - exp(x) + 5");
        AssertVerdicts(r, Verdict.Given, Verdict.Correct, Verdict.Correct);
        Assert.Equal(Verdict.Correct, r.Overall);
        Assert.Equal(Verdict.Incorrect, Check("integrate(x*exp(x), x)", "x*exp(x) + exp(x)").Overall);
        AssertVerdicts(Check("integrate(x^2, x, 0, 3)", "27/3", "9"), Verdict.Given, Verdict.Correct, Verdict.Correct);
        AssertVerdicts(Check("limit(sin(3x)/x, x, 0)", "3"), Verdict.Given, Verdict.Correct);
    }

    [Fact]
    public void TrigonometricIdentities()
    {
        AssertVerdicts(Check("sin(x)^2 + cos(x)^2 + tan(x)^2", "1 + tan(x)^2", "1/cos(x)^2"), Verdict.Given, Verdict.Correct, Verdict.Correct);
        Assert.Equal(Verdict.Incorrect, Check("sin(2x)", "2sin(x)").Overall);
    }

    // ---- inequalities -------------------------------------------------------------------------------

    [Fact]
    public void Inequalities()
    {
        AssertVerdicts(Check("2x - 1 > 3", "2x > 4", "x > 2"), Verdict.Given, Verdict.Correct, Verdict.Correct);
        var r = Check("-2x < 4", "x < -2"); // forgot to flip the sign
        AssertVerdicts(r, Verdict.Given, Verdict.Incorrect);
        AssertVerdicts(Check("-2x < 4", "x > -2"), Verdict.Given, Verdict.Correct);
        AssertVerdicts(Check("x^2 < 9", "-3 < x < 3"), Verdict.Given, Verdict.Correct);
        Assert.Equal(Verdict.Incorrect, Check("x^2 < 9", "x < 3").Overall);
    }

    // ---- row reduction ----------------------------------------------------------------------------------

    [Fact]
    public void RowReductionWithoutNamedOperationsIsRecognised()
    {
        var r = Check("[[1, 2, 3], [3, 4, 5]]", "[[1, 2, 3], [0, -2, -4]]", "[[1, 2, 3], [0, 1, 2]]", "[[1, 0, -1], [0, 1, 2]]");
        AssertVerdicts(r, Verdict.Given, Verdict.Correct, Verdict.Correct, Verdict.Correct);
        Assert.Equal("R2 → R2 - 3R1", r.Steps[1].Message);
        Assert.Equal("R2 → -(1/2)R2", r.Steps[2].Message);
        Assert.Equal("R1 → R1 - 2R2", r.Steps[3].Message);
        Assert.Contains("reduced row echelon form", r.Summary);
        Assert.Equal("R1 ↔ R2", Check("[[0, 1, 2], [1, 1, 1]]", "[[1, 1, 1], [0, 1, 2]]").Steps[1].Message);
    }

    [Fact]
    public void ArithmeticSlipInARowIsFoundWithTheIntendedOperation()
    {
        var r = Check("[[1, 2, 3], [3, 4, 5]]", "[[1, 2, 3], [0, -2, -6]]", "[[1, 2, 3], [0, 1, 3]]");
        AssertVerdicts(r, Verdict.Given, Verdict.Incorrect, Verdict.Correct);
        Assert.Contains("row 2", r.Steps[1].Message);
        Assert.Contains("R2 → R2 - 3R1 would give [0, -2, -4]", r.Steps[1].Message);
        Assert.Contains("[[1, 0, -1], [0, 1, 2]]", r.Summary);
        Assert.Equal(Verdict.Incorrect, r.Overall);
    }

    [Fact]
    public void NamedOperationsAreAppliedAndCompared()
    {
        AssertVerdicts(Check("[[1, 2, 3], [3, 4, 5]]", "R2 → R2 - 3R1: [[1, 2, 3], [0, -2, -4]]", "R2 -> (-1/2)R2: [[1, 2, 3], [0, 1, 2]]"),
            Verdict.Given, Verdict.Correct, Verdict.Correct);
        // Textbook order (-2R1 + R2 → R2), Spanish row names, = as the arrow, several operations at once.
        AssertVerdicts(Check("[[1, 1, 2], [2, 3, 5], [1, 0, 4]]", "-2R1 + R2 → R2, F3 = F3 - F1: [[1, 1, 2], [0, 1, 1], [0, -1, 2]]",
            "R3 → R3 + R2: [[1, 1, 2], [0, 1, 1], [0, 0, 3]]"), Verdict.Given, Verdict.Correct, Verdict.Correct);
        AssertVerdicts(Check("[[1, 2], [3, 4]]", "R1 ↔ R2: [[3, 4], [1, 2]]"), Verdict.Given, Verdict.Correct);
        var wrong = Check("[[1, 2, 3], [3, 4, 5]]", "R2 → R2 - 3R1: [[1, 2, 3], [0, -2, -6]]");
        Assert.Equal(Verdict.Incorrect, wrong.Steps[1].Verdict);
        Assert.Contains("row 2 should be [0, -2, -4]", wrong.Steps[1].Message);
        // The matrix is right, but not for the operation written.
        var mislabelled = Check("[[1, 2], [3, 4]]", "R2 → R2 - 2R1: [[1, 2], [0, -2]]");
        Assert.Equal(Verdict.Incorrect, mislabelled.Steps[1].Verdict);
        Assert.Contains("still row-equivalent", mislabelled.Steps[1].Message);
    }

    [Fact]
    public void AugmentedMatricesWithBars()
    {
        var r = Check("[[2, 1, -1 | 8], [-3, -1, 2 | -11], [-2, 1, 2 | -3]]",
            "R2 → R2 + (3/2)R1, R3 → R3 + R1: [[2, 1, -1 | 8], [0, 1/2, 1/2 | 1], [0, 2, 1 | 5]]",
            "R3 → R3 - 4R2: [[2, 1, -1 | 8], [0, 1/2, 1/2 | 1], [0, 0, -1 | 1]]");
        AssertVerdicts(r, Verdict.Given, Verdict.Correct, Verdict.Correct);
        Assert.Contains("row echelon form, but not yet reduced", r.Summary);
    }

    [Fact]
    public void InvalidRowOperations()
    {
        var zero = Check("[[1, 2], [3, 4]]", "R2 → 0R2: [[1, 2], [0, 0]]");
        Assert.Equal(Verdict.Incorrect, zero.Steps[1].Verdict);
        Assert.Contains("cannot be undone", zero.Steps[1].Message);
        var lost = Check("[[1, 2], [3, 4]]", "[[1, 2], [1, 2]]");
        Assert.Equal(Verdict.Incorrect, lost.Steps[1].Verdict);
        Assert.Contains("rank changed from 2 to 1", lost.Steps[1].Message);
        Assert.Contains("different size", Check("[[1, 2], [3, 4]]", "[[1, 2, 3], [3, 4, 5]]").Steps[1].Message);
        // An unreadable operation still lets the matrix be checked.
        var unreadable = Check("[[1, 2], [3, 4]]", "swap them: [[3, 4], [1, 2]]");
        Assert.Equal(Verdict.Correct, unreadable.Steps[1].Verdict);
        Assert.Contains("Could not read the row operation", unreadable.Steps[1].Message);
    }

    [Fact]
    public void SymbolicEntries()
    {
        AssertVerdicts(Check("[[a, 1], [1, a]]", "R2 → a R2 - R1: [[a, 1], [0, a^2 - 1]]"), Verdict.Given, Verdict.Correct);
        Assert.Equal(Verdict.Incorrect, Check("[[a, 1], [1, a]]", "R2 → a R2 - R1: [[a, 1], [0, a^2 + 1]]").Steps[1].Verdict);
    }

    // ---- input handling -------------------------------------------------------------------------------

    [Fact]
    public void UnreadableLinesAreReportedAndSkipped()
    {
        var r = Check("2x + 3 = 7", "2x = = 4", "x = 2");
        Assert.Equal(Verdict.Unchecked, r.Steps[1].Verdict);
        Assert.Equal(Verdict.Correct, r.Steps[2].Verdict);
    }

    [Fact]
    public void LatexStepsThroughTheKernel()
    {
        var text = ((StrExpr)new Kernel().EvalText(@"checklatex([""x ^ { 2 } - 5 x + 6 = 0"", ""( x - 2 ) ( x - 3 ) = 0"", ""x = 2 \text { or } x = 3""])")).Value;
        Assert.Contains("All steps are correct", text);
        var plain = ((StrExpr)new Kernel().EvalText(@"check([""2x + 3 = 7"", ""2x = 10""])")).Value;
        Assert.Contains("✗", plain);
    }
}
