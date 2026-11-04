using System.Collections.Generic;

namespace T6_Workbench
{
    /// <summary>One ordered field/value pair from an OAT WEAPONFILE info string.</summary>
    internal sealed class WeaponFieldValue 
    { 
        public string Name { get; set; } 
        public string Value { get; set; } 
        public WeaponFieldValue(string Name, string Value) 
        { 
            this.Name = Name; 
            this.Value = Value; 
        } 
    }

    /// <summary>Parsed information needed to display and edit one T6 weapon definition.</summary>
    internal sealed class WeaponPreviewDocument 
    { 
        public string AssetName { get; set; } 
        public IReadOnlyDictionary<string, string> Fields { get; set; } 
        public IReadOnlyList<WeaponFieldValue> OrderedFields { get; set; } 
        public IReadOnlyList<string> ModelNames { get; set; } 
        public string Summary { get; set; } 
        public string Report { get; set; } 
        public WeaponPreviewDocument(string AssetName, IReadOnlyDictionary<string, string> Fields, IReadOnlyList<WeaponFieldValue> OrderedFields, IReadOnlyList<string> ModelNames, string Summary, string Report) 
        { 
            this.AssetName = AssetName; 
            this.Fields = Fields; 
            this.OrderedFields = OrderedFields; 
            this.ModelNames = ModelNames; 
            this.Summary = Summary; 
            this.Report = Report; 
        } 
    }

    /// <summary>Workbench entry points for reading and editing T6 weapon info strings.</summary>
    internal static class WeaponModule
    {
        /// <summary>Read one OAT WEAPONFILE and prepare its preview metadata.</summary>
        internal static WeaponPreviewDocument LoadPreview(string filePath, string assetName) => WeaponHelpers.LoadPreview(filePath, assetName);

        /// <summary>Find a raw weapon definition by its fastfile asset name under a project root.</summary>
        internal static string FindAssetFile(string projectRoot, string assetName) => WeaponHelpers.FindAssetFile(projectRoot, assetName);

        /// <summary>Return names stored in the selected weapon attachment-list field.</summary>
        internal static IReadOnlyList<string> GetModuleNames(WeaponPreviewDocument document, string fieldName) => WeaponHelpers.GetModuleNames(document, fieldName);

        /// <summary>Save edited weapon fields as a project override without touching the source fastfile dump.</summary>
        internal static string SaveEdits(WorkbenchProject project, WeaponPreviewDocument document, IReadOnlyDictionary<string, string> editedFields, IReadOnlyList<string> attachments,
            IReadOnlyList<string> attachmentUniques) => WeaponHelpers.SaveEdits(project, document, editedFields, attachments, attachmentUniques);
    }
}
