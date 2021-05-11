namespace SqlInliner
{
    public class InlinerOptions
    {
        public bool StripUnusedColumns { get; set; } = true;

        public bool StripUnusedJoins { get; set; }
    }
}