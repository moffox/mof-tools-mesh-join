using System;
using UnityEditor;
using UnityEngine;

namespace MofTools.Editor
{
    public static class ToolLocalization
    {
        // Shared MA/NDMF preference, readable even when neither package is installed.
        public static string Language => EditorPrefs.GetString("nadena.dev.ndmf.language-selection",
            Application.systemLanguage == SystemLanguage.Japanese ? "ja-jp" : "en-us");

        public static string Text(string japanese, string english)
        {
            return Language.StartsWith("ja", StringComparison.OrdinalIgnoreCase) ? japanese : english;
        }
    }
}
