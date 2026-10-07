// SPDX-License-Identifier: MIT
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Builds the testbed: URP, linear colour, the camera depth texture on — and the same game twice,
/// as a Linux MONO player (<c>Build/mono</c>, for BepInEx 5) and a Linux IL2CPP one
/// (<c>Build/il2cpp</c>, for BepInEx 6):
/// <code>Unity -batchmode -quit -projectPath unity/AstraTestbed -executeMethod TestbedBuild.Build</code>
/// </summary>
public static class TestbedBuild
{
    const string Scene = "Assets/Testbed/Testbed.unity";
    const string Urp = "Assets/Testbed/URP.asset";
    const string Renderer = "Assets/Testbed/URP Renderer.asset";

    public static void Build()
    {
        Setup();
        Player(ScriptingImplementation.Mono2x, "Build/mono/AstraTestbed.x86_64");
        Player(ScriptingImplementation.IL2CPP, "Build/il2cpp/AstraTestbed.x86_64");
    }

    public static void BuildIl2Cpp()
    {
        Setup();
        Player(ScriptingImplementation.IL2CPP, "Build/il2cpp/AstraTestbed.x86_64");
    }

    public static void BuildMono()
    {
        Setup();
        Player(ScriptingImplementation.Mono2x, "Build/mono/AstraTestbed.x86_64");
    }

    static void Setup()
    {
        PlayerSettings.companyName = "Astra";
        PlayerSettings.productName = "AstraTestbed";
        PlayerSettings.colorSpace = ColorSpace.Linear;
        PlayerSettings.defaultScreenWidth = 1280;
        PlayerSettings.defaultScreenHeight = 720;
        PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
        PlayerSettings.resizableWindow = true;
        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneLinux64, false);
        PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneLinux64, new[] { GraphicsDeviceType.Vulkan });

        if (AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(Urp) == null)
        {
            var data = ScriptableObject.CreateInstance<UniversalRendererData>();
            AssetDatabase.CreateAsset(data, Renderer);
            var asset = UniversalRenderPipelineAsset.Create(data);
            asset.supportsCameraDepthTexture = true;
            AssetDatabase.CreateAsset(asset, Urp);
            AssetDatabase.SaveAssets();
        }
        var urp = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(Urp);
        GraphicsSettings.defaultRenderPipeline = urp;
        for (int i = 0; i < QualitySettings.names.Length; i++)
        {
            QualitySettings.SetQualityLevel(i, false);
            QualitySettings.renderPipeline = urp;
        }

        // The material every primitive is painted with: URP Lit, kept in Resources so the shader ships.
        Directory.CreateDirectory("Assets/Testbed/Resources");
        if (AssetDatabase.LoadAssetAtPath<Material>("Assets/Testbed/Resources/Lit.mat") == null)
        {
            AssetDatabase.CreateAsset(new Material(Shader.Find("Universal Render Pipeline/Lit")), "Assets/Testbed/Resources/Lit.mat");
            AssetDatabase.SaveAssets();
        }

        if (!File.Exists(Scene))
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            EditorSceneManager.SaveScene(scene, Scene);
        }
        EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(Scene, true) };
        AssetDatabase.SaveAssets();
    }

    static void Player(ScriptingImplementation backend, string path)
    {
        PlayerSettings.SetScriptingBackend(UnityEditor.Build.NamedBuildTarget.Standalone, backend);
        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = new[] { Scene },
            locationPathName = path,
            target = BuildTarget.StandaloneLinux64,
            options = BuildOptions.None,
        });
        if (report.summary.result != BuildResult.Succeeded) throw new System.Exception($"{backend} build: {report.summary.result}");
        Debug.Log($"[testbed] {backend} → {path} ({report.summary.totalSize / (1024 * 1024)} MiB)");
    }
}
