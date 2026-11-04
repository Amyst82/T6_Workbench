namespace T6_Workbench
{
    /// <summary>
    /// Input paths for an OAT export and round-trip comparison.
    /// </summary>
    public sealed class RoundTripRequest
    {
        public string BinDirectory { get; set; }
        public string ExportDirectory { get; set; }
        public string OutputRoot { get; set; }

        public RoundTripRequest(string BinDirectory, string ExportDirectory, string OutputRoot)
        {
            this.BinDirectory = BinDirectory;
            this.ExportDirectory = ExportDirectory;
            this.OutputRoot = OutputRoot;
        }
    }
}
