#if UNITY_EDITOR

using UnityEditor;
using Enet=ENet;

namespace Mirror.ENet
{
    public class NativeLibraryDebugger
    {
        [MenuItem("Ignorance Debugging/Enet Library Name")]
        public static void RevealEnetLibraryName()
        {
            //EditorUtility.DisplayDialog("Enet Library Name", Enet.Native.nativeLibraryName, "Got it");
        }
    }
}

#endif
