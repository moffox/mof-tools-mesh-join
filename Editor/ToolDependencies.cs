using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace MofTools.Editor
{
    public sealed class DependencyStatus
    {
        public string PackageName { get; }
        public string DisplayName { get; }
        public string MinimumVersion { get; }
        public int MaximumMajor { get; }
        public string InstalledVersion { get; }
        public bool Installed => !string.IsNullOrEmpty(InstalledVersion);
        public bool Compatible { get; }
        public string RequiredVersion => $">= {MinimumVersion}, < {MaximumMajor}.0.0-a";

        internal DependencyStatus(string packageName, string displayName, string minimumVersion, int maximumMajor, string installedVersion)
        {
            PackageName = packageName; DisplayName = displayName; MinimumVersion = minimumVersion;
            MaximumMajor = maximumMajor; InstalledVersion = installedVersion;
            if (!Installed) return;
            string numeric = installedVersion.Split('-', '+')[0];
            if (!Version.TryParse(numeric, out var version)) return;
            var minimum = Version.Parse(minimumVersion);
            Compatible = version >= minimum && version.Major < maximumMajor &&
                !(version == minimum && installedVersion.Contains("-"));
        }

        public string Error => !Installed
            ? ToolLocalization.Text($"{DisplayName} が導入されていません。", $"{DisplayName} is not installed.")
            : Compatible ? "" : ToolLocalization.Text(
                $"{DisplayName} {InstalledVersion} は対応範囲外です（必要: {RequiredVersion}）。",
                $"{DisplayName} {InstalledVersion} is unsupported (required: {RequiredVersion}).");

        public string Guidance => !Installed
            ? ToolLocalization.Text($"{DisplayName}を導入してください。", $"Please install {DisplayName}.")
            : Compatible ? "" : ToolLocalization.Text(
                $"{DisplayName}を対応範囲のバージョンに変更してください。", $"Use a supported version of {DisplayName}.");
    }

    public static class ToolDependencies
    {
        public static DependencyStatus[] GetStatus()
        {
            return Evaluate(PackageInfo.GetAllRegisteredPackages().ToDictionary(p => p.name, p => p.version));
        }

        public static DependencyStatus[] Evaluate(IReadOnlyDictionary<string, string> installed)
        {
            DependencyStatus Status(string id, string name, string minimum, int maximum)
            { installed.TryGetValue(id, out string version); return new DependencyStatus(id, name, minimum, maximum, version); }
            return new[]
            {
                Status("nadena.dev.modular-avatar", "Modular Avatar", "1.18.7", 2),
                Status("nadena.dev.ndmf", "NDMF", "1.14.8", 2),
                Status("com.vrchat.avatars", "VRChat SDK - Avatars", "3.10.6", 4),
                Status("com.vrchat.base", "VRChat SDK - Base", "3.10.6", 4)
            };
        }

        public static bool Ready => GetStatus().All(s => s.Compatible);
        public static string Message(IEnumerable<DependencyStatus> statuses)
        {
            var missing = statuses.Where(s => !s.Compatible).ToArray();
            if (missing.Length == 0) return "";
            // Show concrete failures before the instructions.
            return string.Join("\n", missing.Select(s => s.Error)) + "\n\n" + string.Join("\n", missing.Select(s => s.Guidance));
        }

        public static void RequireReady()
        {
            string message = Message(GetStatus());
            if (!string.IsNullOrEmpty(message)) throw new BuildFailedException("MA Mesh Join by Mof\n" + message);
        }

        [InitializeOnLoadMethod]
        private static void ReportMissingDependencies()
        {
            EditorApplication.delayCall += () =>
            {
                string message = Message(GetStatus());
                if (!string.IsNullOrEmpty(message)) Debug.LogError("MA Mesh Join by Mof\n" + message);
            };
        }
    }

    public sealed class DependencyBuildGuard : IPreprocessBuildWithReport
    {
        public int callbackOrder => int.MinValue;
        public void OnPreprocessBuild(BuildReport report) { ToolDependencies.RequireReady(); }
    }
}
