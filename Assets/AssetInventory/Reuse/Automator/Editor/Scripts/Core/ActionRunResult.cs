namespace Automator
{
    public sealed class ActionRunResult
    {
        public bool Success { get; set; }
        public string Error { get; set; }
        public int StepsExecuted { get; set; }
        public int StepsFailed { get; set; }
    }
}
