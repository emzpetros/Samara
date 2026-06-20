using UnityEditor;

namespace ImpossibleRobert.Common
{
    /// <summary>
    /// Wrapper for Unity's Progress API with null-safe operations.
    /// </summary>
    public static class MetaProgress
    {
        public static int Start(string name, string description = null, int parentId = -1)
        {
            return Progress.Start(name, description, Progress.Options.None, parentId);
        }

        public static void Report(int id, float progress, string description)
        {
            if (id <= 0) return;
            Progress.Report(id, progress, description);
        }

        public static void Report(int id, int currentStep, int totalSteps, string description)
        {
            if (id <= 0) return;
            Progress.Report(id, currentStep, totalSteps, description);
        }

        public static int Remove(int id)
        {
            if (id <= 0) return id;
            return Progress.Remove(id);
        }
    }
}
