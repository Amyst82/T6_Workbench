using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace T6_Workbench
{
    /// <summary>
    /// Stores portable application state beside the executable using atomic replacement.
    /// </summary>
    internal sealed class WorkbenchStateStore
    {
        readonly string filePath;

        internal WorkbenchStateStore(string filePath)
        {
            this.filePath = filePath;
        }

        internal WorkbenchState Load()
        {
            if (!File.Exists(filePath))
                return null;
            var state = JsonSerializer.Deserialize<WorkbenchState>(File.ReadAllText(filePath));
            if (state == null || state.Version != 1)
                throw new InvalidDataException("The saved Workbench state has an unsupported format.");
            return state;
        }

        internal void Save(WorkbenchState state)
        {
            string json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
            string temporary = filePath + ".tmp";
            try
            {
                File.WriteAllText(temporary, json, new UTF8Encoding(false));
                if (File.Exists(filePath))
                {
                    File.Replace(temporary, filePath, null);
                }
                else
                {
                    File.Move(temporary, filePath);
                }
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
        }
    }
}
