namespace MofTools.MeshJoin.Editor
{
    internal static class JoinLocalization
    {
        // MA uses the same public NDMF preference, independently of the Unity Editor UI language.
        internal static string Text(string japanese, string english)
        {
            return MofTools.Editor.ToolLocalization.Text(japanese, english);
        }
    }
}
