using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace T6_Workbench
{
    /// <summary>
    /// Small project-scoped undo/redo journal for files under edits.
    /// </summary>
    internal sealed class ProjectEditHistory
    {
        sealed class Change 
        { 
            public string RelativePath { get; set; } 
            public string Before { get; set; } 
            public string After { get; set; } 
            public Change(string RelativePath, string Before, string After) 
            { 
                this.RelativePath = RelativePath; 
                this.Before = Before; 
                this.After = After; 
            } 
        }
        sealed class Entry 
        { 
            public string Label { get; set; } 
            public string PatchDirectory { get; set; } 
            public IReadOnlyList<Change> Changes { get; set; } 
            public Entry(string Label, string PatchDirectory, IReadOnlyList<Change> Changes) 
            { 
                this.Label = Label; 
                this.PatchDirectory = PatchDirectory; 
                this.Changes = Changes; 
            } 
        }
        readonly List<Entry> entries = new List<ProjectEditHistory.Entry>(new ProjectEditHistory.Entry[0]);
        int cursor;
        string projectRoot;
        const int Limit = 20;

        internal bool CanUndo => cursor > 0;
        internal bool CanRedo => cursor < entries.Count;
        internal int AppliedCount => cursor;
        internal string UndoLabel => CanUndo ? entries[cursor - 1].Label : null;
        internal string RedoLabel => CanRedo ? entries[cursor].Label : null;

        internal void Attach(WorkbenchProject project)
        {
            if (projectRoot != null && !projectRoot.Equals(project.Root, StringComparison.OrdinalIgnoreCase))
                Clear();
            projectRoot = project.Root;
        }

        internal bool Execute(WorkbenchProject project, string label, Action action)
        {
            if (projectRoot != null && !projectRoot.Equals(project.Root, StringComparison.OrdinalIgnoreCase)) Clear();
            projectRoot = project.Root;
            string historyRoot = Path.Combine(project.Root, ".workbench", "undo");
            Directory.CreateDirectory(historyRoot);
            string work = Path.Combine(historyRoot, ".capture-" + Guid.NewGuid().ToString("N"));
            string before = Path.Combine(work, "before");
            Snapshot(project.Edits, before);
            try { action(); }
            catch { RestoreDirectory(before, project.Edits); TryDelete(work); throw; }
            string after = Path.Combine(work, "after");
            Snapshot(project.Edits, after);
            var changes = Diff(before, after, work);
            if (changes.Count == 0) { TryDelete(work); return false; }
            string patch = Path.Combine(historyRoot, Guid.NewGuid().ToString("N"));
            Directory.Move(work, patch);
            var entry = new Entry(label, patch, changes);
            while (entries.Count > cursor) { TryDelete(entries[entries.Count - 1].PatchDirectory); entries.RemoveAt(entries.Count - 1); }
            entries.Add(entry); cursor++;
            while (entries.Count > Limit) { TryDelete(entries[0].PatchDirectory); entries.RemoveAt(0); cursor--; }
            return true;
        }

        internal bool Undo(WorkbenchProject project)
        {
            EnsureProject(project);
            if (!CanUndo) return false;
            Entry entry = entries[cursor - 1];
            VerifyCurrent(project.Edits, entry.PatchDirectory, entry.Changes, useAfter: true);
            foreach (var change in entry.Changes) Apply(project.Edits, entry.PatchDirectory, change, useAfter: false);
            cursor--; return true;
        }

        internal bool Redo(WorkbenchProject project)
        {
            EnsureProject(project);
            if (!CanRedo) return false;
            Entry entry = entries[cursor];
            VerifyCurrent(project.Edits, entry.PatchDirectory, entry.Changes, useAfter: false);
            foreach (var change in entry.Changes) Apply(project.Edits, entry.PatchDirectory, change, useAfter: true);
            cursor++; return true;
        }

        internal void Clear()
        {
            foreach (var entry in entries) TryDelete(entry.PatchDirectory);
            entries.Clear(); cursor = 0; projectRoot = null;
        }

        void EnsureProject(WorkbenchProject project)
        {
            if (projectRoot != null && !projectRoot.Equals(project.Root, StringComparison.OrdinalIgnoreCase)) Clear();
            projectRoot = project.Root;
        }

        static void Snapshot(string source, string target)
        {
            Directory.CreateDirectory(target);
            if (!Directory.Exists(source)) return;
            foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                string relative = Helpers.GetRelativePath(source, file);
                string destination = Path.Combine(target, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination));
                File.Copy(file, destination, true);
            }
        }

        static List<Change> Diff(string before, string after, string work)
        {
            string[] oldFiles = Directory.GetFiles(before, "*", SearchOption.AllDirectories);
            string[] newFiles = Directory.GetFiles(after, "*", SearchOption.AllDirectories);
            var old = oldFiles.ToDictionary(p => Helpers.GetRelativePath(before, p), p => p, StringComparer.OrdinalIgnoreCase);
            var current = newFiles.ToDictionary(p => Helpers.GetRelativePath(after, p), p => p, StringComparer.OrdinalIgnoreCase);
            var changes = new List<Change>(); int index = 0;
            foreach (string relative in old.Keys.Union(current.Keys, StringComparer.OrdinalIgnoreCase))
            {
                old.TryGetValue(relative, out string a); current.TryGetValue(relative, out string b);
                if (a != null && b != null && Hash(a).SequenceEqual(Hash(b))) continue;
                string oldCopy = null, newCopy = null;
                string folder = Path.Combine(work, "patch", index++.ToString("D4"));
                if (a != null) { oldCopy = Path.Combine("patch", Path.GetFileName(folder), "before"); Directory.CreateDirectory(folder); File.Copy(a, Path.Combine(work, oldCopy)); }
                if (b != null) { newCopy = Path.Combine("patch", Path.GetFileName(folder), "after"); Directory.CreateDirectory(folder); File.Copy(b, Path.Combine(work, newCopy)); }
                changes.Add(new Change(relative, oldCopy, newCopy));
            }
            return changes;
        }

        static byte[] Hash(string path) { using (var sha = SHA256.Create()) { using (var stream = File.OpenRead(path)) { return sha.ComputeHash(stream); } } }
        static void VerifyCurrent(string edits, string patch, IReadOnlyList<Change> changes, bool useAfter)
        {
            foreach (var change in changes)
            {
                string relative = useAfter ? change.After : change.Before;
                string expected = relative == null ? null : Path.Combine(patch, relative);
                string live = Path.Combine(edits, change.RelativePath);
                if (expected == null ? File.Exists(live) : !File.Exists(live) || !Hash(live).SequenceEqual(Hash(expected)))
                    throw new IOException("An edited file changed outside Workbench. The undo history was kept; reload the project to reconcile it.");
            }
        }
        static void Apply(string edits, string patch, Change change, bool useAfter)
        {
            string relative = useAfter ? change.After : change.Before;
            string source = relative == null ? null : Path.Combine(patch, relative);
            string destination = Path.Combine(edits, change.RelativePath);
            if (source == null) { if (File.Exists(destination)) File.Delete(destination); return; }
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            string temporary = destination + ".workbench-undo-" + Guid.NewGuid().ToString("N");
            File.Copy(source, temporary);
            if (File.Exists(destination)) File.Replace(temporary, destination, null); else File.Move(temporary, destination);
        }
        static void RestoreDirectory(string snapshot, string destination)
        {
            if (Directory.Exists(destination)) Directory.Delete(destination, true);
            Directory.CreateDirectory(destination);
            foreach (string file in Directory.GetFiles(snapshot, "*", SearchOption.AllDirectories))
            {
                string target = Path.Combine(destination, Helpers.GetRelativePath(snapshot, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(file, target, true);
            }
        }
        static void TryDelete(string path) { try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { } }
    }
}
