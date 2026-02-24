using System;

namespace SqlInliner;

/// <summary>
/// Describes hints about join cardinality that can be specified via SQL comments
/// to enable safe join removal.
/// </summary>
/// <remarks>
/// <para>
/// Place hints as SQL comments on or near the JOIN clause:
/// <code>
/// LEFT JOIN /* @join:unique */ dbo.Address a ON a.PersonId = p.Id
/// INNER JOIN /* @join:unique @join:required */ dbo.Status s ON s.Id = p.StatusId
/// </code>
/// </para>
/// <para>
/// Safety rules for join removal when columns are unused:
/// <list type="bullet">
/// <item><c>LEFT JOIN @join:unique</c> — Safe: at most 1 match, all left-side rows preserved.</item>
/// <item><c>INNER JOIN @join:unique @join:required</c> — Safe: exactly 1 match per row, no filtering.</item>
/// <item><c>INNER JOIN @join:unique</c> (without required) — Not safe: may filter rows without a match.</item>
/// </list>
/// </para>
/// </remarks>
[Flags]
public enum JoinHint
{
    /// <summary>
    /// No hint specified.
    /// </summary>
    None = 0,

    /// <summary>
    /// The join produces at most one matching row per source row (the join condition
    /// references a unique or primary key on the joined table).
    /// Specified via <c>/* @join:unique */</c> in SQL.
    /// </summary>
    Unique = 1,

    /// <summary>
    /// Every source row has a matching row in the joined table (the foreign key is NOT NULL
    /// and referential integrity is enforced).
    /// Specified via <c>/* @join:required */</c> in SQL.
    /// </summary>
    Required = 2,
}
