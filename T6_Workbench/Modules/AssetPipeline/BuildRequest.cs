namespace T6_Workbench
{
    /// <summary>
    /// Paths and base zones used to build a T6 fastfile with OAT.
    /// </summary>
    public sealed class BuildRequest
    {
        public string BinDirectory { get; set; }
        public string ZoneFile { get; set; }
        public string AssetDirectory { get; set; }
        public string OutputRoot { get; set; }
        public string CaptureFile { get; set; }
        public string[] BaseZones { get; set; }

        public BuildRequest(string BinDirectory, string ZoneFile, string AssetDirectory, string OutputRoot, string CaptureFile, string[] BaseZones)
        {
            this.BinDirectory = BinDirectory;
            this.ZoneFile = ZoneFile;
            this.AssetDirectory = AssetDirectory;
            this.OutputRoot = OutputRoot;
            this.CaptureFile = CaptureFile;
            this.BaseZones = BaseZones;
        }
    }
}
