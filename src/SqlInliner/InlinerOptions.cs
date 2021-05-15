namespace SqlInliner
{
    public sealed class InlinerOptions
    {
        public bool StripUnusedColumns { get; set; } = true;

        public bool StripUnusedJoins { get; set; }
    }
}