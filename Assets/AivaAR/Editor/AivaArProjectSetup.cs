#if UNITY_EDITOR
using Aiva.AivaAR;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Aiva.AivaAR.Editor
{
    public static class AivaArProjectSetup
    {
        private const string ScenePath = "Assets/AivaAR/AivaAR.unity";

        [MenuItem("AIVA AR/Configure Android and Create Scene")]
        public static void Configure()
        {
            PlayerSettings.companyName = "OppaAI";
            PlayerSettings.productName = "AIVA AR";
            PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, "ai.oppa.aiva.ar");
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel24;
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            PlayerSettings.Android.forceInternetPermission = true;
            PlayerSettings.defaultInterfaceOrientation = UIOrientation.AutoRotation;

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var app = new GameObject("AIVA AR App");
            app.AddComponent<AivaArApp>();
            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            AssetDatabase.SaveAssets();

            Debug.Log(
                "AIVA AR scene created. In Project Settings > XR Plug-in Management > Android, " +
                "enable ARCore before building to your phone.");
        }
    }
}
#endif

