using System.Data;

namespace Editor.Core.Engine;

/// <summary>
/// Evaluates simple arithmetic expressions for the Ctrl+R = expression register.
/// Delegates to DataTable.Compute which supports +, -, *, /, %, parentheses,
/// integer and floating-point literals.
/// </summary>
internal static class ExpressionEvaluator
{
    /// <summary>
    /// Evaluates <paramref name="expression"/> and returns the result as a string,
    /// or <c>null</c> if evaluation fails.
    /// </summary>
    internal static string? Evaluate(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return null;

        try
        {
            using var dt = new DataTable();
            var result = dt.Compute(expression, null);
            if (result is DBNull || result is null)
                return null;

            // Format numeric results cleanly: no trailing zeros for decimals.
            return result switch
            {
                double d  => FormatNumber(d),
                float f   => FormatNumber(f),
                decimal m => m == Math.Floor(m) ? ((long)m).ToString() : m.ToString("G"),
                _         => result.ToString()
            };
        }
        catch
        {
            return null;
        }
    }

    private static string FormatNumber(double value)
    {
        // If the value is a whole number, show it without decimal point.
        if (value == Math.Floor(value) && !double.IsInfinity(value))
            return ((long)value).ToString();
        return value.ToString("G");
    }
}
