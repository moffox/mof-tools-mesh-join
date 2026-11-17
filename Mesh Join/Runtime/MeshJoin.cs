using System;
using nadena.dev.modular_avatar.core;
using nadena.dev.ndmf;
using UnityEngine;

namespace MofTools.MeshJoin
{
    public enum MaskColor { Black, White }
    public enum FaceSelection { AnyVertex, AllVertices, Centroid }

    [DisallowMultipleComponent]
    [RequireComponent(typeof(SkinnedMeshRenderer))]
    [AddComponentMenu("MofTools/Mof Mesh Join")]
    public sealed class MeshJoin : MonoBehaviour, INDMFEditorOnly
    {
        public AvatarObjectReference Target = new AvatarObjectReference();
        [Min(0)] public float JoinDistance = 0.00001f;
        public int Order;
        public bool DeleteFaces;
        public Texture2D DeleteMask;
        [Min(0)] public int MaterialSlot;
        [Range(0, 7)] public int UVChannel;
        public MaskColor DeleteColor = MaskColor.White;
        public FaceSelection Selection = FaceSelection.Centroid;

        // Reset runs only when explicitly adding/resetting the component, not on reload/import.
        public static event Action<MeshJoin> Created;
        private void Reset() { if (!Application.isPlaying) Created?.Invoke(this); }
    }
}
