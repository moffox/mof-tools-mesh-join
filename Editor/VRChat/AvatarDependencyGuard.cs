using UnityEngine;
using VRC.SDKBase.Editor.BuildPipeline;

namespace MofTools.Editor
{
    public sealed class AvatarDependencyGuard : IVRCSDKPreprocessAvatarCallback
    {
        public int callbackOrder => int.MinValue;
        public bool OnPreprocessAvatar(GameObject avatarGameObject)
        {
            string message = ToolDependencies.Message(ToolDependencies.GetStatus());
            if (string.IsNullOrEmpty(message)) return true;
            Debug.LogError("MA Mesh Join by Mof\n" + message, avatarGameObject);
            return false;
        }
    }
}
