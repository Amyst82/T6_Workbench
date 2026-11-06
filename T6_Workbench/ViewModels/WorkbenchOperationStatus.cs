using AMUI;
using System.Text.Json.Serialization;

namespace T6_Workbench
{
    public class WorkbenchOperationStatus
    {
        public WorkbenchOperationState State { get; set; }

        public string Message { get; set; }

        public WorkbenchOperationStatus(WorkbenchOperationState State, string Message)
        {
            this.State = State;
            this.Message = Message;
        }

        [JsonIgnore]
        public bool IsRunning => (State == WorkbenchOperationState.Busy) || (State == WorkbenchOperationState.Building);

        [JsonIgnore]
        public bool IsError => (State == WorkbenchOperationState.Error);

        [JsonIgnore]
        public bool IsCanceled => (State == WorkbenchOperationState.Cancelled);

        [JsonIgnore]
        public bool IsBuilt => (State == WorkbenchOperationState.Built);

        [JsonIgnore]
        public bool IsBuiltWithWarnings => (State == WorkbenchOperationState.BuiltWithWarnings);

        [JsonIgnore]
        public bool IsReady => (State == WorkbenchOperationState.Ready);

    }

    public enum WorkbenchOperationState
    {
        Ready,
        Busy,
        Building,
        Built,
        BuiltWithWarnings,
        Error,
        Cancelled
    }
}
