namespace T6_Workbench
{
    /// <summary>
    /// Result of one standalone-package validation step.
    /// </summary>
    public sealed class TestOutcome
    {
        public string Name { get; set; }
        public string Status { get; set; }
        public string Details { get; set; }
        public string Directory { get; set; }

        public TestOutcome(string Name, string Status, string Details, string Directory)
        {
            this.Name = Name;
            this.Status = Status;
            this.Details = Details;
            this.Directory = Directory;
        }
    }
}
