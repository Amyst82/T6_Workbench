
namespace T6_Workbench
{
    /// <summary>
    /// Summary and warnings returned by an asset operation.
    /// </summary>
    public sealed class OperationResult
    {
        public string Directory { get; set; }
        public string Message { get; set; }
        public string[] Warnings { get; set; }

        public OperationResult(string Directory, string Message, string[] Warnings)
        {
            this.Directory = Directory;
            this.Message = Message;
            this.Warnings = Warnings;
        }

        public OperationResult(OperationResult original)
        {
            this.Directory = original.Directory;
            this.Message = original.Message;
            this.Warnings = original.Warnings;
        }
    }
}
