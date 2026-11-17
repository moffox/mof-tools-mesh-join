using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MofTools.Editor;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace MofTools.Tests
{
    public static class DependencyValidationRunner
    {
        private static int checks;
        private static void Check(bool value, string name)
        { checks++; if (!value) throw new Exception(name); }

        public static void Run()
        {
            int code = 0;
            var results = new List<string>();
            string language = ToolLocalization.Language;
            var package = PackageInfo.FindForAssembly(typeof(ToolDependencies).Assembly);
            string output = Path.Combine(package.resolvedPath, ".validation");
            Directory.CreateDirectory(output);
            try
            {
                EditorPrefs.SetString("nadena.dev.ndmf.language-selection", "ja-jp");
                var versions = new Dictionary<string, string>
                {
                    ["nadena.dev.modular-avatar"] = "1.18.7", ["nadena.dev.ndmf"] = "1.14.8",
                    ["com.vrchat.avatars"] = "3.10.6", ["com.vrchat.base"] = "3.10.6"
                };
                Check(ToolDependencies.Evaluate(versions).All(s => s.Compatible), "対応バージョンを受理");
                versions.Remove("nadena.dev.modular-avatar");
                var missing = ToolDependencies.Evaluate(versions);
                string message = ToolDependencies.Message(missing);
                Check(missing.Count(s => !s.Compatible) == 1, "不足しているMAだけを報告");
                Check(message.Contains("Modular Avatarを導入してください。"), "MA導入案内");
                Check(message.IndexOf("導入されていません。", StringComparison.Ordinal) < message.IndexOf("導入してください。", StringComparison.Ordinal), "実際のエラーが注意文より前");
                versions["nadena.dev.modular-avatar"] = "1.18.6";
                Check(!ToolDependencies.Evaluate(versions)[0].Compatible, "古いMAを拒否");
                versions["nadena.dev.modular-avatar"] = "1.18.7-alpha.1";
                Check(!ToolDependencies.Evaluate(versions)[0].Compatible, "最低バージョンのプレリリースを拒否");
                versions["nadena.dev.modular-avatar"] = "2.0.0";
                Check(!ToolDependencies.Evaluate(versions)[0].Compatible, "対応外のメジャーバージョンを拒否");
                versions["nadena.dev.modular-avatar"] = "1.19.0";
                Check(ToolDependencies.Evaluate(versions)[0].Compatible, "対応範囲の新バージョンを受理");
                var empty = ToolDependencies.Evaluate(new Dictionary<string,string>());
                Check(empty.Length == 4 && empty.All(s => !s.Compatible), "前提アセット全未導入を報告");
                EditorPrefs.SetString("nadena.dev.ndmf.language-selection", "en-us");
                Check(ToolDependencies.Message(empty).Contains("Please install Modular Avatar."), "依存なしでも英語表示");
                EditorPrefs.SetString("nadena.dev.ndmf.language-selection", "ja-jp");
                var actual = ToolDependencies.GetStatus();
                results.Add("Actual dependencies: " + string.Join(", ", actual.Select(s => s.DisplayName + "=" + (s.InstalledVersion ?? "missing"))));
                bool ready = actual.All(s => s.Compatible);
                bool blocked = false;
                try { new DependencyBuildGuard().OnPreprocessBuild(null); }
                catch (BuildFailedException e) { blocked = true; Check(e.Message.Contains("導入"), "ビルド停止時の具体的なエラー"); }
                Check(blocked == !ready, "不足時はUnityビルドを停止");
                bool featureLoaded = AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "MofTools.MeshJoin.Editor");
                Check(featureLoaded == ready, "依存なしでは機能アセンブリを無効化");
                var vrcGuard = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("MofTools.Editor.AvatarDependencyGuard", false)).FirstOrDefault(t => t != null);
                if (vrcGuard != null)
                {
                    var avatar = new GameObject("DependencyGuardValidation");
                    try
                    {
                        bool allowed = (bool)vrcGuard.GetMethod("OnPreprocessAvatar").Invoke(Activator.CreateInstance(vrcGuard), new object[] { avatar });
                        Check(allowed == ready, "不足時はVRChatアバタービルドを停止");
                    }
                    finally { UnityEngine.Object.DestroyImmediate(avatar); }
                }
                var processor = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("nadena.dev.ndmf.AvatarProcessor", false)).FirstOrDefault(t => t != null);
                if (!ready && processor != null)
                {
                    var avatar = new GameObject("NdmfDependencyGuardValidation");
                    try
                    {
                        var descriptor = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("VRC.SDK3.Avatars.Components.VRCAvatarDescriptor", false)).FirstOrDefault(t => t != null);
                        if (descriptor != null) avatar.AddComponent(descriptor);
                        var platform = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("nadena.dev.ndmf.platform.AmbientPlatform", false)).First(t => t != null);
                        var method = processor.GetMethods().First(m => m.Name == "ProcessAvatar" && m.GetParameters().Length == 2 && m.GetParameters()[1].ParameterType.Name == "INDMFPlatformProvider");
                        var context = method.Invoke(null,new[] { (object)avatar, platform.GetProperty("DefaultPlatform").GetValue(null) });
                        Check(!(bool)context.GetType().GetProperty("Successful").GetValue(context), "MA未導入時のNDMF直接処理も停止");
                    }
                    finally { UnityEngine.Object.DestroyImmediate(avatar); }
                }
                // Construct the window without requiring any MA/NDMF types or an avatar.
                var window = ScriptableObject.CreateInstance<MofToolsWindow>();
                Check(window, "依存なしでも一覧ウィンドウを生成できる");
                UnityEngine.Object.DestroyImmediate(window);
                results.Add($"PASS: {checks} dependency checks");
            }
            catch (Exception e) { code = 1; results.Add("FAIL: " + e); Debug.LogException(e); }
            finally
            {
                EditorPrefs.SetString("nadena.dev.ndmf.language-selection", language);
                string folder = new DirectoryInfo(Directory.GetCurrentDirectory()).Name;
                File.WriteAllLines(Path.Combine(output, "dependencies-" + folder + ".txt"),results);
                Debug.Log(string.Join("\n",results));
                EditorApplication.Exit(code);
            }
        }
    }
}
