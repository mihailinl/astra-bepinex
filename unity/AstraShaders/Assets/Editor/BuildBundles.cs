// SPDX-License-Identifier: MIT
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Builds the shader bundles the plugins embed: one per player platform, each carrying every graphics
/// API a game on it may run (a Windows game: D3D11, D3D12, Vulkan, OpenGL; a Linux one: Vulkan,
/// OpenGL). Run from the command line:
/// <code>Unity -batchmode -quit -projectPath unity/AstraShaders -executeMethod BuildBundles.Build</code>
/// The bundles land in <c>Build/</c> named <c>astra-&lt;unity major&gt;-&lt;platform&gt;.bundle</c>;
/// a bundle loads in its own Unity line and newer, never older.
/// </summary>
public static class BuildBundles
{
    const string Shader = "Assets/Astra/AstraComposite.shader";

    [MenuItem("Astra/Build shader bundles")]
    public static void Build()
    {
        Directory.CreateDirectory("Build");
        string major = Application.unityVersion.Split('.')[0];
        Make(BuildTarget.StandaloneWindows64, $"astra-{major}-windows.bundle",
            GraphicsDeviceType.Direct3D11, GraphicsDeviceType.Direct3D12, GraphicsDeviceType.Vulkan, GraphicsDeviceType.OpenGLCore);
        Make(BuildTarget.StandaloneLinux64, $"astra-{major}-linux.bundle",
            GraphicsDeviceType.Vulkan, GraphicsDeviceType.OpenGLCore);
        Debug.Log("[astra] bundles built");
    }

    static void Make(BuildTarget target, string name, params GraphicsDeviceType[] apis)
    {
        PlayerSettings.SetUseDefaultGraphicsAPIs(target, false);
        PlayerSettings.SetGraphicsAPIs(target, apis);
        var build = new AssetBundleBuild { assetBundleName = name, assetNames = new[] { Shader } };
        string dir = Path.Combine("Build", target.ToString());
        Directory.CreateDirectory(dir);
        var manifest = BuildPipeline.BuildAssetBundles(dir, new[] { build },
            BuildAssetBundleOptions.ChunkBasedCompression | BuildAssetBundleOptions.ForceRebuildAssetBundle | BuildAssetBundleOptions.StrictMode,
            target);
        if (manifest == null) throw new System.Exception($"bundle for {target} failed");
        File.Copy(Path.Combine(dir, name), Path.Combine("Build", name), true);
    }
}
