namespace T6_Workbench
{
    /// <summary>
    /// Settings used to export assets from a T6 fastfile through OAT.
    /// </summary>
    public sealed class ExportRequest
    {
        public string BinDirectory { get; set; }

        public string Fastfile { get; set; }

        public string CaptureFile { get; set; }

        public string OutputRoot { get; set; }

        public string[] SearchPaths { get; set; }

        public string ModelFormat { get; set; }

        public string ImageFormat { get; set; }

        public string IncludeAssets { get; set; }

        public bool AllowMissingIdentityNormal { get; set; }

        public string EditedAssetsRoot { get; set; }

        public string SpecificAssetType { get; set; }

        public string SpecificAssetName { get; set; }

        public string ProjectAssetsRoot { get; set; }

        public ExportRequest(string BinDirectory, string Fastfile, string CaptureFile, string OutputRoot, string[] SearchPaths, string ModelFormat = "GLB", string ImageFormat = "DDS", string IncludeAssets = "", bool AllowMissingIdentityNormal = false, string EditedAssetsRoot = null, string SpecificAssetType = "", string SpecificAssetName = "", string ProjectAssetsRoot = null)
        {
            this.BinDirectory = BinDirectory;
            this.Fastfile = Fastfile;
            this.CaptureFile = CaptureFile;
            this.OutputRoot = OutputRoot;
            this.SearchPaths = SearchPaths;
            this.ModelFormat = ModelFormat;
            this.ImageFormat = ImageFormat;
            this.IncludeAssets = IncludeAssets;
            this.AllowMissingIdentityNormal = AllowMissingIdentityNormal;
            this.EditedAssetsRoot = EditedAssetsRoot;
            this.SpecificAssetType = SpecificAssetType;
            this.SpecificAssetName = SpecificAssetName;
            this.ProjectAssetsRoot = ProjectAssetsRoot;
        }

        public ExportRequest(ExportRequest original)
        {
            this.BinDirectory = original.BinDirectory;
            this.Fastfile = original.Fastfile;
            this.CaptureFile = original.CaptureFile;
            this.OutputRoot = original.OutputRoot;
            this.SearchPaths = original.SearchPaths;
            this.ModelFormat = original.ModelFormat;
            this.ImageFormat = original.ImageFormat;
            this.IncludeAssets = original.IncludeAssets;
            this.AllowMissingIdentityNormal = original.AllowMissingIdentityNormal;
            this.EditedAssetsRoot = original.EditedAssetsRoot;
            this.SpecificAssetType = original.SpecificAssetType;
            this.SpecificAssetName = original.SpecificAssetName;
            this.ProjectAssetsRoot = original.ProjectAssetsRoot;
        }
    }
}
