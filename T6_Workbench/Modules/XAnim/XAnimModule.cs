using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace T6_Workbench
{
    public sealed class XAnimModelChoice
    {
        public string Name { get; set; }
        public string File { get; set; }
        public int MatchedBones { get; set; }
        public int RequiredBones { get; set; }
        public int JointCount { get; set; }
        public bool IsBundledHands { get; set; }
        public string[] MissingBones { get; set; }
        public XAnimModelChoice(string Name, string File, int MatchedBones, int RequiredBones, int JointCount, bool IsBundledHands, string[] MissingBones) { this.Name = Name; this.File = File; this.MatchedBones = MatchedBones; this.RequiredBones = RequiredBones; this.JointCount = JointCount; this.IsBundledHands = IsBundledHands; this.MissingBones = MissingBones; }
        public string Display => EnglishText.Format("{0} · bones {1}/{2} · total joints {3}", (IsBundledHands ? EnglishText.Text("FBI hands (resource)") : Name), MatchedBones, RequiredBones, JointCount);
    }

    /// <summary>Compact information shown when an XAnim asset is selected.</summary>
    public sealed class XAnimClipInfo 
    { 
        public int Version { get; set; } 
        public int Frames { get; set; } 
        public int FrameRate { get; set; } 
        public int Bones { get; set; } 
        public int AnimatedBones { get; set; } 
        public bool Looped { get; set; } 
        public bool HasDelta { get; set; } 
        public XAnimClipInfo(int Version, int Frames, int FrameRate, int Bones, int AnimatedBones, bool Looped, bool HasDelta = false) 
        { 
            this.Version = Version; 
            this.Frames = Frames; 
            this.FrameRate = FrameRate; 
            this.Bones = Bones; 
            this.AnimatedBones = AnimatedBones; 
            this.Looped = Looped; 
            this.HasDelta = HasDelta; 
        } 
    }
    public sealed class XAnimRigChoice 
    { 
        public int SkinIndex { get; set; } 
        public string Display { get; set; } 
        public XAnimRigChoice(int SkinIndex, string Display) 
        { 
            this.SkinIndex = SkinIndex; 
            this.Display = Display; 
        } 
    }
    public sealed class XAnimDependencyModel 
    { 
        public string Field { get; set; } 
        public string Name { get; set; } 
        public string Glb { get; set; } 
        public string[] AnimatedBones { get; set; } 
        public string Problem { get; set; } 
        public float OffsetX { get; set; } 
        public float OffsetY { get; set; } 
        public float OffsetZ { get; set; } 
        public float Pitch { get; set; } 
        public float Yaw { get; set; } 
        public float Roll { get; set; } 
        public string Tag { get; set; } 
        public XAnimDependencyModel(string Field, string Name, string Glb, string[] AnimatedBones, string Problem, float OffsetX = 0, 
            float OffsetY = 0, float OffsetZ = 0, float Pitch = 0, float Yaw = 0, float Roll = 0, string Tag = null) 
        { 
            this.Field = Field; 
            this.Name = Name; 
            this.Glb = Glb; 
            this.AnimatedBones = AnimatedBones; 
            this.Problem = Problem; 
            this.OffsetX = OffsetX; 
            this.OffsetY = OffsetY; 
            this.OffsetZ = OffsetZ; 
            this.Pitch = Pitch; 
            this.Yaw = Yaw; 
            this.Roll = Roll; 
            this.Tag = Tag; 
        } 
    }
    public sealed class XAnimDependencyWeapon 
    { 
        public string Name { get; set; } 
        public string AnimationField { get; set; } 
        public XAnimDependencyModel[] Models { get; set; } 
        public XAnimDependencyWeapon(string Name, string AnimationField, XAnimDependencyModel[] Models) 
        { 
            this.Name = Name; 
            this.AnimationField = AnimationField; 
            this.Models = Models; 
        } 
    }
    public sealed class XAnimDependencyReport 
    { 
        public XAnimDependencyWeapon[] Weapons { get; set; } 
        public int UnreadableWeapons { get; set; } 
        public XAnimDependencyReport(XAnimDependencyWeapon[] Weapons, int UnreadableWeapons) 
        { 
            this.Weapons = Weapons; this.UnreadableWeapons = UnreadableWeapons; 
        } 
    }

    /// <summary>
    /// Exports T6 XAnim clips into model GLBs and imports edited GLB clips to project edits.
    /// </summary>
    public sealed class XAnimModule
    {
        /// <summary>
        /// Find project model GLBs compatible with the selected animation skeleton.
        /// </summary>
        public IReadOnlyList<XAnimModelChoice> FindCompatibleModels(WorkbenchProject project, string assetName) => XAnimHelpers.FindCompatibleModels(project, assetName);

        /// <summary>
        /// Read the header and track counts without changing the source asset.
        /// </summary>
        public XAnimClipInfo Describe(WorkbenchProject project, string assetName) => XAnimHelpers.Describe(project, assetName);

        /// <summary>
        /// Find project weapon definitions that explicitly reference this clip and their view models.
        /// </summary>
        public XAnimDependencyReport FindDependencies(WorkbenchProject project, string assetName) => XAnimHelpers.FindDependencies(project, assetName);

        /// <summary>
        /// Write one animated GLB containing the chosen model, its skin and the selected XAnim clip.
        /// </summary>
        public string ExportToGlb(WorkbenchProject project, string assetName, string modelGlb, string outputGlb) => XAnimHelpers.ExportToGlb(project, assetName, modelGlb, outputGlb);

        /// <summary>
        /// Optionally attach an existing weapon GLB for Blender preview.
        /// </summary>
        public string ExportToGlb(WorkbenchProject project, string assetName, string modelGlb, string outputGlb, string weaponGlb, Action<string> warning = null)
        {
            string temporary = outputGlb + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                XAnimHelpers.ExportToGlb(project, assetName, modelGlb, temporary);
                int weaponNodeOffset = XAnimWeaponPreview.Attach(temporary, weaponGlb, project, warning);
                XAnimHelpers.AddWeaponTracks(project, assetName, temporary, weaponNodeOffset, warning);
                if (File.Exists(outputGlb))
                    File.Replace(temporary, outputGlb, null);
                else
                    File.Move(temporary, outputGlb);
                return outputGlb;
            }
            finally
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
        }

        /// <summary>
        /// Export with the unique set of view models referenced by the project's weapon definitions.
        /// </summary>
        public string ExportAutomatic(WorkbenchProject project, string assetName, string modelGlb, string outputGlb,
            XAnimDependencyReport report, Action<string> log = null)
        {
            var sets = report.Weapons.Select(weapon => weapon.Models).ToArray();
            if (sets.Length > 1 && sets.Skip(1).Any(dependencyModels => !ModelsEquivalent(sets[0], dependencyModels)))
                throw new InvalidDataException(EnglishText.Text("Animation is used by weapons with different models or attachments; automatic selection is ambiguous: ") + string.Join(", ", report.Weapons.Select(w => w.Name)));
            var models = sets.FirstOrDefault() ?? new XAnimDependencyModel[0];
            if (sets.Length == 0) log?.Invoke(EnglishText.Text("Weapon referencing the animation was not found; exporting the selected skeleton model without a weapon."));
            foreach (var model in models)
                if (model.Problem != null || model.Glb == null)
                    throw new InvalidDataException(EnglishText.Text("Model not found ") + model.Name + ": " + model.Problem);
            var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string temporary = outputGlb + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                XAnimHelpers.ExportToGlb(project, assetName, modelGlb, temporary);
                bool attachedPrimary = false;
                foreach (var model in models)
                {
                    if (!unique.Add(model.Glb))
                        throw new InvalidDataException(EnglishText.Text("One model is specified in multiple weapon slots: ") + model.Name + EnglishText.Text(". Automatic merging is ambiguous."));
                    bool primary = model.Field.StartsWith("gunModel", StringComparison.OrdinalIgnoreCase);
                    if (!primary && !attachedPrimary)
                        throw new InvalidDataException(EnglishText.Text("Weapon model is missing; cannot determine additional model placement ") + model.Name);
                    if (!primary && (!string.IsNullOrWhiteSpace(model.Tag) || model.Pitch != 0 || model.Yaw != 0 || model.Roll != 0))
                        throw new NotSupportedException(EnglishText.Text("For ") + model.Name + EnglishText.Text(" an attachment or rotation is specified, whose automatic placement is not yet supported."));
                    int offset = XAnimWeaponPreview.Attach(temporary, model.Glb, project, log, !primary,
                        model.OffsetX, model.OffsetY, model.OffsetZ);
                    XAnimHelpers.AddWeaponTracks(project, assetName, temporary, offset, log);
                    attachedPrimary |= primary;
                    log?.Invoke(EnglishText.Text("Model added: ") + model.Name + " (" + model.Field + ")");
                }
                if (File.Exists(outputGlb)) File.Replace(temporary, outputGlb, null);
                else File.Move(temporary, outputGlb);
                return outputGlb;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        static bool ModelsEquivalent(XAnimDependencyModel[] left, XAnimDependencyModel[] right) =>
            left.Length == right.Length && left.Zip(right, (a, b) =>
                string.Equals(a.Field, b.Field, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase) &&
                a.OffsetX == b.OffsetX && a.OffsetY == b.OffsetY && a.OffsetZ == b.OffsetZ &&
                a.Pitch == b.Pitch && a.Yaw == b.Yaw && a.Roll == b.Roll &&
                string.Equals(a.Tag ?? "", b.Tag ?? "", StringComparison.OrdinalIgnoreCase)).All(equal => equal);
        /// <summary>
        /// Import an edited GLB animation into the selected T6 XAnim asset under project edits.
        /// </summary>
        public string ImportFromGlb(WorkbenchProject project, string assetName, string editedGlb) => XAnimHelpers.ImportFromGlb(project, assetName, editedGlb);

        public IReadOnlyList<XAnimRigChoice> FindImportRigs(WorkbenchProject project, string assetName, string editedGlb) => XAnimHelpers.FindImportRigs(project, assetName, editedGlb);

        public string ImportFromGlb(WorkbenchProject project, string assetName, string editedGlb, int skinIndex) => XAnimHelpers.ImportFromGlb(project, assetName, editedGlb, skinIndex);
    }
}
