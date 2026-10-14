// SPDX-License-Identifier: MIT
using System.Collections.Generic;
using System.IO;
using UnityEditor;
#if UNITY_2020_2_OR_NEWER
using UnityEditor.Rendering;
#endif
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
        CheckCompiles(target, apis);
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

    /// <summary>A pass that does not compile for one of the bundle's graphics APIs is left OUT of the
    /// bundle with no error at build time, and the whole shader is then "not supported" in every game
    /// (a new pass once hit an FXC internal error so): every pass is compiled for every API first.</summary>
    static void CheckCompiles(BuildTarget target, GraphicsDeviceType[] apis)
    {
        var shader = AssetDatabase.LoadAssetAtPath<UnityEngine.Shader>(Shader);
        var errors = new List<string>();
#if UNITY_2020_2_OR_NEWER
        var data = ShaderUtil.GetShaderData(shader);
        for (int s = 0; s < data.SubshaderCount; s++)
        {
            var sub = data.GetSubshader(s);
            for (int p = 0; p < sub.PassCount; p++)
            {
                var pass = sub.GetPass(p);
                foreach (var api in apis)
                {
                    var platform = api == GraphicsDeviceType.Vulkan ? ShaderCompilerPlatform.Vulkan
                        : api == GraphicsDeviceType.OpenGLCore ? ShaderCompilerPlatform.OpenGLCore : ShaderCompilerPlatform.D3D;
                    foreach (var stage in new[] { ShaderType.Vertex, ShaderType.Fragment })
                    {
                        var r = pass.CompileVariant(stage, new string[0], platform, target);
                        if (r.Success) continue;
                        foreach (var m in r.Messages) errors.Add($"{pass.Name} {platform} {stage}: {m.message}");
                        if (r.Messages.Length == 0) errors.Add($"{pass.Name} {platform} {stage}: failed");
                    }
                }
            }
        }
#else
        // This line cannot compile one pass for one API from a script: the shader's own messages.
        if (ShaderUtil.ShaderHasError(shader))
            foreach (var m in ShaderUtil.GetShaderMessages(shader)) errors.Add($"{m.platform}: {m.message}");
#endif
        if (errors.Count > 0) throw new System.Exception($"the shader does not compile for {target}: {string.Join("; ", errors)}");
    }
}
