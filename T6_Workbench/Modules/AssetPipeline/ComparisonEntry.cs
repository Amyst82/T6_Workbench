namespace T6_Workbench
{
    /// <summary>
    /// One file's result in an exported-asset comparison.
    /// </summary>
    public sealed class ComparisonEntry
    {
        public string File { get; set; }
        public string Status { get; set; }
        public string Details { get; set; }

        public ComparisonEntry(string File, string Status, string Details)
        {
            this.File = File;
            this.Status = Status;
            this.Details = Details;
        }
    }
}
