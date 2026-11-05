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
