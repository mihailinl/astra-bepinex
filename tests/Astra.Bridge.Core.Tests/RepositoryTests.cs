// SPDX-License-Identifier: MIT
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

/// <summary>
/// What the build and the release lean on outside the code: the package versions a pack gets (a local
/// pack must never carry a published package's id and version), the runner manifests' version, and the
/// integration template's pins, which move together.
/// </summary>
public class RepositoryTests
{
    static readonly string Root = FindRoot();

    static string FindRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "Directory.Build.props")) && File.Exists(Path.Combine(d.FullName, "tools", "pack.sh")))
                return d.FullName;
        throw new InvalidOperationException("not inside the astra-bepinex checkout");
    }

    static string Read(string rel) => File.ReadAllText(Path.Combine(Root, rel));

    static string FoundationVersion => Regex.Match(Read("Directory.Build.props"), "<Version>([^<]+)</Version>").Groups[1].Value;

    /// <summary>Runs <c>dotnet msbuild</c> on a project; its exit code and its output.</summary>
    static (int code, string output) MsBuild(string project, params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Root };
        psi.ArgumentList.Add("msbuild");
        psi.ArgumentList.Add(project);
        psi.ArgumentList.Add("-nologo");
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi);
        string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output.Trim());
    }

    static string PackageVersion(string project, params string[] props) =>
        MsBuild(project, props.Append("-getProperty:PackageVersion").ToArray()).output;

    [Fact]
    public void the_foundation_version_is_a_plain_version_bepinex_can_parse()
    {
        // BepInEx reads the plugin version (MyPluginInfo, from $(Version)) as System.Version.
        Assert.True(Version.TryParse(FoundationVersion, out _), FoundationVersion);
    }

    /// <summary>F1/F2: a release with both halves is the plain version; anything else a prerelease.</summary>
    [Fact]
    public void only_a_release_with_both_halves_packs_the_sdk_under_the_foundation_version()
    {
        const string sdk = "src/Astra.Unity.Sdk/Astra.Unity.Sdk.csproj";
        string v = FoundationVersion;
        Assert.Equal(v, PackageVersion(sdk, "-p:AstraRelease=true", "-p:AstraSdkIl2Cpp=true"));
        Assert.Equal(v + "-dev", PackageVersion(sdk));
        Assert.Equal(v + "-dev", PackageVersion(sdk, "-p:AstraSdkIl2Cpp=true"));
        Assert.Equal(v + "-mono-dev", PackageVersion(sdk, "-p:AstraSdkIl2Cpp=false"));
        Assert.Equal(v + "-mono-dev", PackageVersion(sdk, "-p:AstraRelease=true", "-p:AstraSdkIl2Cpp=false"));
    }

    [Fact]
    public void a_release_sdk_without_its_il2cpp_half_is_refused()
    {
        var (code, output) = MsBuild("src/Astra.Unity.Sdk/Astra.Unity.Sdk.csproj",
            "-t:AstraCheckPlugins", "-p:AstraRelease=true", "-p:AstraSdkIl2Cpp=false");
        Assert.NotEqual(0, code);
        Assert.Contains("carries both halves", output);
    }

    [Fact]
    public void the_template_package_is_a_prerelease_unless_released()
    {
        const string templates = "templates/Astra.Unity.Templates.csproj";
        Assert.Equal(FoundationVersion + "-dev", PackageVersion(templates));
        Assert.Equal(FoundationVersion, PackageVersion(templates, "-p:AstraRelease=true"));
    }

    [Theory]
    [InlineData("runner/astra-gi.toml")]
    [InlineData("runner/il2cpp/astra-gi.toml")]
    public void the_runner_manifests_name_the_foundation_version(string manifest) =>
        Assert.Equal(FoundationVersion, Regex.Match(Read(manifest), "(?m)^version = \"([^\"]+)\"").Groups[1].Value);

    /// <summary>The template's three pins name one foundation release (tools/pin-template.sh moves them).</summary>
    [Fact]
    public void the_template_pins_name_one_release()
    {
        string pinned = Regex.Match(Read("templates/astra-integration/AstraIntegration.csproj"), "<AstraVersion>([^<]+)</AstraVersion>").Groups[1].Value;
        Assert.Matches(@"^\d+\.\d+\.\d+$", pinned);
        Assert.Contains($"range = \">={pinned}, <1\"", Read("templates/astra-integration/astra-item.toml"));
        var table = Regex.Matches(Read("templates/astra-integration/tools/fetch.sh"), @"(?m)^  ([0-9.]+)\) SDK_SHA256=[0-9a-f]{64}; RUNNER_SHA256=[0-9a-f]{64} ;;$");
        Assert.Equal(2, table.Count); // one per runtime
        Assert.All(table, m => Assert.Equal(pinned, m.Groups[1].Value));
    }
}
