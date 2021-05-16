namespace SqlInliner
{
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
        /// Gets the recommended options that should be used for optimal results.
        /// </summary>
        public static InlinerOptions Recommended()
        {
            return new()
            {
                StripUnusedJoins = true,
            };
        }
    }
}