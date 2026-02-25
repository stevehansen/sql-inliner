namespace SqlInliner;

/// <summary>
/// Describes the options that should be used when inlining a SQL view.
/// </summary>
public sealed class InlinerOptions
{
    /// <summary>
    /// Gets or sets whether unused columns should be stripped inside a nested view statement. Defaults to <c>true</c>.
    /// </summary>
    public bool StripUnusedColumns { get; set; } = true;

    /// <summary>
    /// Gets or sets whether unused joins should be stripped inside a nested view statement. Defaults to <c>false</c> but is recommended to be set to <c>true</c>.
    /// </summary>
    public bool StripUnusedJoins { get; set; }

    /// <summary>
    /// Gets or sets whether join condition column references should be excluded from the usage count when stripping joins.
    /// When <c>true</c>, a table referenced only in its own ON clause (e.g. <c>INNER JOIN b ON a.Id = b.Id AND b.Type = 'X'</c>)
    /// will be stripped. This is more aggressive and can change results for INNER JOINs where the ON clause acts as a filter.
    /// Defaults to <c>false</c>.
    /// </summary>
    public bool AggressiveJoinStripping { get; set; }

    /// <summary>
    /// Gets the recommended options that should be used for optimal results.
    /// </summary>
    public static InlinerOptions Recommended()
    {
        return new()
        {
            StripUnusedJoins = true,
        };
    }

    /// <summary>
    /// Serializes the options to a metadata-friendly string.
    /// </summary>
    public string ToMetadataString()
    {
        return $"StripUnusedColumns={StripUnusedColumns}, StripUnusedJoins={StripUnusedJoins}, AggressiveJoinStripping={AggressiveJoinStripping}";
    }

    /// <summary>
    /// Attempts to parse <see cref="InlinerOptions"/> from a SQL string containing a <c>-- Options:</c> metadata line.
    /// Returns <c>null</c> if the options line is not found.
    /// </summary>
    public static InlinerOptions? TryParseFromMetadata(string sql)
    {
        const string prefix = "-- Options: ";
        var startIndex = sql.IndexOf(prefix, System.StringComparison.Ordinal);
        if (startIndex < 0)
            return null;

        startIndex += prefix.Length;
        var endIndex = sql.IndexOf('\n', startIndex);
        var line = endIndex < 0 ? sql.Substring(startIndex) : sql.Substring(startIndex, endIndex - startIndex);
        line = line.TrimEnd('\r');

        var options = new InlinerOptions();
        foreach (var pair in line.Split(','))
        {
            var parts = pair.Trim().Split('=');
            if (parts.Length != 2)
                continue;

            var key = parts[0].Trim();
            var value = parts[1].Trim();

            switch (key)
            {
                case nameof(StripUnusedColumns):
                    if (bool.TryParse(value, out var stripCols))
                        options.StripUnusedColumns = stripCols;
                    break;
                case nameof(StripUnusedJoins):
                    if (bool.TryParse(value, out var stripJoins))
                        options.StripUnusedJoins = stripJoins;
                    break;
                case nameof(AggressiveJoinStripping):
                    if (bool.TryParse(value, out var aggressive))
                        options.AggressiveJoinStripping = aggressive;
                    break;
                // Unknown keys are silently ignored for forward compatibility
            }
        }

        return options;
    }
}