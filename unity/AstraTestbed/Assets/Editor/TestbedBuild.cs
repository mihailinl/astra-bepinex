// SPDX-License-Identifier: MIT
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
#if !ASTRA_TESTBED_BUILTIN
using UnityEngine.Rendering.Universal;
#endif

/// <summary>
/// Builds the testbed: URP, linear colour, the camera depth texture on — and the same game twice,
/// as a Linux MONO player (<c>Build/mono</c>, for BepInEx 5) and a Linux IL2CPP one
/// (<c>Build/il2cpp</c>, for BepInEx 6):
/// <code>Unity -batchmode -quit -projectPath unity/AstraTestbed -executeMethod TestbedBuild.Build</code>
/// The same file builds it in the BUILT-IN pipeline (the define <c>ASTRA_TESTBED_BUILTIN</c>), and
/// with an older editor's URP: <c>tools/build-testbed.sh</c> makes such a project from this one.
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

        Directory.CreateDirectory("Assets/Testbed/Resources");
#if ASTRA_TESTBED_BUILTIN
        GraphicsSettings.renderPipelineAsset = null;
        for (int i = 0; i < QualitySettings.names.Length; i++)
        {
            QualitySettings.SetQualityLevel(i, false);
            QualitySettings.antiAliasing = 4;
            QualitySettings.shadowResolution = ShadowResolution.VeryHigh;
            QualitySettings.shadowDistance = 60;
        }
        const string LitShader = "Standard";
#else
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
        Pretty(urp);
        GraphicsSettings.defaultRenderPipeline = urp;
        for (int i = 0; i < QualitySettings.names.Length; i++)
        {
            QualitySettings.SetQualityLevel(i, false);
            QualitySettings.renderPipeline = urp;
        }

        const string LitShader = "Universal Render Pipeline/Lit";
#endif

        // The material every primitive is painted with (URP Lit, or Standard), and the one the glass
        // scene's pane and sparks are (premultiplied alpha, drawn by every pipeline): kept in
        // Resources so their shaders ship.
        if (AssetDatabase.LoadAssetAtPath<Material>("Assets/Testbed/Resources/Lit.mat") == null)
            AssetDatabase.CreateAsset(new Material(Shader.Find(LitShader)), "Assets/Testbed/Resources/Lit.mat");
        if (AssetDatabase.LoadAssetAtPath<Material>("Assets/Testbed/Resources/Glass.mat") == null)
            AssetDatabase.CreateAsset(new Material(Shader.Find("Sprites/Default")), "Assets/Testbed/Resources/Glass.mat");
        AssetDatabase.SaveAssets();

        if (!File.Exists(Scene))
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            EditorSceneManager.SaveScene(scene, Scene);
        }
        EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(Scene, true) };
        AssetDatabase.SaveAssets();
    }

#if !ASTRA_TESTBED_BUILTIN
    /// <summary>
    /// "Normal" URP graphics, the way a game ships it — so she is seen against a real picture, not a
    /// flat-shaded one: 4096 soft shadows in four cascades, shadows from lamps too, 4×MSAA and HDR;
    /// SSAO; and a global volume with ACES tonemapping, bloom, a little colour grading and a vignette.
    /// </summary>
    static void Pretty(UniversalRenderPipelineAsset urp)
    {
        var a = new SerializedObject(urp);
        void Set(string name, int v) { var p = a.FindProperty(name); if (p != null) p.intValue = v; else Debug.LogWarning($"[testbed] URP asset has no {name}"); }
        void SetF(string name, float v) { var p = a.FindProperty(name); if (p != null) p.floatValue = v; else Debug.LogWarning($"[testbed] URP asset has no {name}"); }
        Set("m_MainLightShadowmapResolution", 4096);
        Set("m_ShadowCascadeCount", 4);
        SetF("m_ShadowDistance", 60);
        Set("m_SoftShadowsSupported", 1);
        Set("m_SoftShadowQuality", 3);
        Set("m_AdditionalLightShadowsSupported", 1);
        Set("m_AdditionalLightsShadowmapResolution", 2048);
        Set("m_MSAA", 4);
        Set("m_SupportsHDR", 1);
        Set("m_RequireDepthTexture", 1);
        a.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(urp);

        var data = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(Renderer);
        if (data.postProcessData == null)
            data.postProcessData = AssetDatabase.LoadAssetAtPath<PostProcessData>("Packages/com.unity.render-pipelines.universal/Runtime/Data/PostProcessData.asset");
#if UNITY_6000_0_OR_NEWER
        // URP 17's SSAO feature and the project's default volume; an older URP's testbed goes without.
        if (!data.rendererFeatures.Exists(f => f is ScreenSpaceAmbientOcclusion))
        {
            var ssao = ScriptableObject.CreateInstance<ScreenSpaceAmbientOcclusion>();
            ssao.name = "SSAO";
            AssetDatabase.AddObjectToAsset(ssao, data);
            data.rendererFeatures.Add(ssao);
        }
        EditorUtility.SetDirty(data);

        var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>("Assets/DefaultVolumeProfile.asset");
        if (profile == null) throw new System.Exception("Assets/DefaultVolumeProfile.asset is missing (URP 17 makes it)");
        T Get<T>() where T : VolumeComponent
        {
            if (!profile.TryGet(out T c))
            {
                c = profile.Add<T>(true);
                c.name = typeof(T).Name;
                AssetDatabase.AddObjectToAsset(c, profile);
            }
            c.active = true;
            return c;
        }
        Get<Tonemapping>().mode.Override(TonemappingMode.ACES);
        var bloom = Get<Bloom>();
        bloom.intensity.Override(0.6f);
        bloom.threshold.Override(1.0f);
        bloom.scatter.Override(0.7f);
        var grade = Get<ColorAdjustments>();
        grade.postExposure.Override(0.35f);
        grade.contrast.Override(12f);
        grade.saturation.Override(8f);
        Get<Vignette>().intensity.Override(0.22f);
        EditorUtility.SetDirty(profile);
        // It is the project's DEFAULT volume (URP 17's global settings point at it), so every camera
        // that renders post-processing gets it with no volume in the scene.
#endif
        AssetDatabase.SaveAssets();
    }
#endif

    static void Player(ScriptingImplementation backend, string path)
    {
#if UNITY_2021_2_OR_NEWER
        PlayerSettings.SetScriptingBackend(UnityEditor.Build.NamedBuildTarget.Standalone, backend);
#else
        PlayerSettings.SetScriptingBackend(BuildTargetGroup.Standalone, backend);
#endif
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
