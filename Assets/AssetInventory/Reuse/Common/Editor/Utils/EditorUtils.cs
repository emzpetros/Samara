using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;

namespace ImpossibleRobert.Common
{
    public static class EditorUtils
    {
        private static List<string> GetCurrentDefines() => PlayerSettings.GetScriptingDefineSymbols(NamedBuildTarget.FromBuildTargetGroup(EditorUserBuildSettings.selectedBuildTargetGroup)).Split(';').ToList();
        private static void SetCurrentDefines(IEnumerable<string> keywords)
        {
            BuildTargetGroup selectedBuildTargetGroup = EditorUserBuildSettings.selectedBuildTargetGroup;
            if (selectedBuildTargetGroup == BuildTargetGroup.Unknown) return;

            NamedBuildTarget namedBuildTarget = NamedBuildTarget.FromBuildTargetGroup(selectedBuildTargetGroup);
            if (namedBuildTarget == NamedBuildTarget.Unknown) return;

            PlayerSettings.SetScriptingDefineSymbols(namedBuildTarget, string.Join(";", keywords));
        }

        public static bool HasDefine(string keyword) => GetCurrentDefines().Contains(keyword);

        public static void AddDefine(string keyword) => SetCurrentDefines(GetCurrentDefines().Union(new List<string> {keyword}));
        public static void RemoveDefine(string keyword) => SetCurrentDefines(GetCurrentDefines().Where(d => d != keyword));

        private static List<string> GetCurrentCompilerArguments() => PlayerSettings.GetAdditionalCompilerArguments(NamedBuildTarget.FromBuildTargetGroup(EditorUserBuildSettings.selectedBuildTargetGroup)).ToList();
        private static void SetCurrentCompilerArguments(IEnumerable<string> args)
        {
            BuildTargetGroup selectedBuildTargetGroup = EditorUserBuildSettings.selectedBuildTargetGroup;
            if (selectedBuildTargetGroup == BuildTargetGroup.Unknown) return;

            NamedBuildTarget namedBuildTarget = NamedBuildTarget.FromBuildTargetGroup(selectedBuildTargetGroup);
            if (namedBuildTarget == NamedBuildTarget.Unknown) return;

            PlayerSettings.SetAdditionalCompilerArguments(namedBuildTarget, args.ToArray());
        }

        public static bool HasCompilerArgument(string arg) => GetCurrentCompilerArguments().Contains(arg);
        public static void AddCompilerArgument(string arg) => SetCurrentCompilerArguments(GetCurrentCompilerArguments().Union(new List<string> {arg}));
        public static void RemoveCompilerArgument(string arg) => SetCurrentCompilerArguments(GetCurrentCompilerArguments().Where(d => d != arg));
    }
}