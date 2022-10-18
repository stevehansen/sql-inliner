using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace SqlInliner;

/// <summary>
/// Contains the names of built-in SQL functions (e.g. DATEADD) with the indexes of parameters that can be ignored (e.g. the interval parameter of DATEADD shouldn't be tracked as a named column expression)
/// </summary>
internal static class ParametersToIgnore
{
    private static readonly Dictionary<string, int[]> parameterIndexesToIgnore = new(StringComparer.OrdinalIgnoreCase)
    {
        { "DATEADD", new[] { 0 } },
        { "DATEDIFF", new[] { 0 } },
        { "DATENAME", new[] { 0 } },
    };

    public static bool HasIgnoredParameters(string functionName,
#if NET5_0_OR_GREATER
        [NotNullWhen(true)]
#endif
        out int[]? indexes)
    {
        return parameterIndexesToIgnore.TryGetValue(functionName, out indexes);
    }
}