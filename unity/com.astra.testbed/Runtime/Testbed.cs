// SPDX-License-Identifier: MIT
using System;
using System.Collections;
using System.IO;
using UnityEngine;

/// <summary>
/// A tiny "game" to test the plugins on, built entirely in code (no assets to keep in sync), shared by
/// the URP and the HDRP testbed projects (a local package): ground,
/// walls, a ramp, stairs, a sun, and a player — a capsule tagged <c>Player</c> with a
/// <see cref="CharacterController"/> — that walks a loop, runs, jumps, while a third-person camera
/// follows and now and then whips round (the turn that shows ghosting).
/// <para>
/// Command line: <c>-astraShots &lt;dir&gt;</c> saves a PNG every 4 s; <c>-astraQuit &lt;s&gt;</c> quits
/// then; <c>-astraOffscreen</c> renders into a texture (batch mode has no window);
/// <c>-astraScene dark</c> is the LIGHT test instead of the walk: a closed room the sun cannot reach,
/// a near-black ambient (what a cave's baked probes give), and — one after the other, 4 s each —
/// no light, a warm ceiling lamp, a flashlight from the camera, three coloured lamps around the room.
/// The shots at 2, 6, 10 and 14 s show her in each.
/// </para>
/// </summary>
public sealed class Testbed : MonoBehaviour
{
    static readonly Vector3[] Route =
    {
        new Vector3(0, 0, 0), new Vector3(10, 0, 0), new Vector3(10, 0, 10), new Vector3(-6, 0, 10), new Vector3(-6, 0, -4),
    };

    CharacterController body;
    Camera cam;
    int leg;
    float vy, yawOffset, nextWhip = 6, t;
    string shots;
    float quitAt = -1, nextShot = 2, shotEvery = 4;
    int shotN;
    RenderTexture offscreen;
    bool dark;
    Light lamp, torch;
    Light[] party;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot() => new GameObject("Testbed").AddComponent<Testbed>();

    void Start()
    {
        var args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "-astraShots") shots = args[i + 1];
            if (args[i] == "-astraQuit") quitAt = float.Parse(args[i + 1], System.Globalization.CultureInfo.InvariantCulture);
            if (args[i] == "-astraScene") dark = args[i + 1] == "dark";
        }
        if (shots != null) Directory.CreateDirectory(shots);
        Application.targetFrameRate = 60;
        BuildWorld();
        if (dark) BuildDarkRoom();
        if (Array.IndexOf(args, "-astraOffscreen") >= 0 || Application.isBatchMode)
        {
            offscreen = new RenderTexture(1280, 720, 24) { name = "Testbed offscreen" };
            cam.targetTexture = offscreen;
        }
        Debug.Log($"[testbed] ready (offscreen: {offscreen != null}, shots: {shots ?? "none"})");
    }

    void BuildWorld()
    {
        var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
        ground.name = "Ground";
        ground.transform.localScale = new Vector3(6, 1, 6);
        Paint(ground, new Color(0.62f, 0.66f, 0.58f), Floor, new Vector2(30, 30), 0.25f);

        Box("Wall A", new Vector3(4, 1.5f, 5), new Vector3(0.4f, 3, 6), new Color(0.6f, 0.55f, 0.5f));
        Box("Wall B", new Vector3(-2, 1, -8), new Vector3(8, 2, 0.4f), new Color(0.5f, 0.5f, 0.6f));
        Box("Pillar", new Vector3(6, 2, 8), new Vector3(1, 4, 1), new Color(0.7f, 0.4f, 0.3f));
        var ramp = Box("Ramp", new Vector3(-12, 0.9f, 2), new Vector3(3, 0.3f, 8), new Color(0.6f, 0.6f, 0.4f));
        ramp.transform.rotation = Quaternion.Euler(-14, 0, 0);
        for (int i = 0; i < 6; i++)
            Box($"Step {i}", new Vector3(14 + i * 0.6f, 0.1f + i * 0.2f, -6), new Vector3(0.6f, 0.2f + i * 0.4f, 3), new Color(0.5f, 0.5f, 0.5f));

        var sun = new GameObject("Sun").AddComponent<Light>();
        sun.type = LightType.Directional;
        sun.intensity = 1.2f;
        sun.color = new Color(1f, 0.95f, 0.85f);
        sun.shadows = LightShadows.Soft;
        sun.shadowStrength = 1;
        sun.transform.rotation = Quaternion.Euler(45, -30, 0);
        RenderSettings.sun = sun;
        // The sky lights the world (its ambient and its reflections), as a game's would.
        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Skybox;
        RenderSettings.ambientIntensity = 1;
        DynamicGI.UpdateEnvironment();

        // Things that show a real picture: chrome, gloss, matte; crates.
        Ball("Chrome ball", new Vector3(3, 0.6f, -2.5f), 1.2f, new Color(0.9f, 0.9f, 0.92f), 0.92f, 1f);
        Ball("Red ball", new Vector3(5, 0.45f, -1.2f), 0.9f, new Color(0.75f, 0.08f, 0.06f), 0.8f, 0f);
        Ball("Clay ball", new Vector3(-3, 0.5f, -3f), 1f, new Color(0.8f, 0.7f, 0.55f), 0.1f, 0f);
        for (int i = 0; i < 4; i++)
        {
            var crate = Box($"Crate {i}", new Vector3(-2.5f + (i % 2) * 0.9f, 0.4f + (i / 2) * 0.8f, 3.5f), new Vector3(0.8f, 0.8f, 0.8f), new Color(0.55f, 0.4f, 0.25f));
            crate.transform.rotation = Quaternion.Euler(0, i * 17, 0);
        }
        var probe = new GameObject("Reflections").AddComponent<ReflectionProbe>();
        probe.mode = UnityEngine.Rendering.ReflectionProbeMode.Realtime;
        probe.refreshMode = UnityEngine.Rendering.ReflectionProbeRefreshMode.ViaScripting;
        probe.size = new Vector3(80, 30, 80);
        probe.transform.position = new Vector3(0, 2, 0);
        probe.RenderProbe();

        var player = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        player.name = "Player";
        player.tag = "Player";
        UnityEngine.Object.Destroy(player.GetComponent<Collider>());
        Paint(player, new Color(0.2f, 0.4f, 0.9f), null, Vector2.one, 0.6f);
        player.transform.position = new Vector3(0, 1, 0);
        body = player.AddComponent<CharacterController>();
        body.height = 2;
        body.radius = 0.4f;

        cam = new GameObject("Main Camera") { tag = "MainCamera" }.AddComponent<Camera>();
        cam.fieldOfView = 60;
        cam.nearClipPlane = 0.1f;
        cam.farClipPlane = 500;
        cam.allowHDR = true;
        cam.allowMSAA = true;
        PostProcessing(cam);
    }

    /// <summary>URP draws a camera's post-processing only when its camera data says so — by
    /// reflection, so the package stays pipeline-free (HDRP's cameras always do).</summary>
    static void PostProcessing(Camera c)
    {
        var type = Type.GetType("UnityEngine.Rendering.Universal.UniversalAdditionalCameraData, Unity.RenderPipelines.Universal.Runtime");
        if (type == null) return;
        var data = c.GetComponent(type) ?? c.gameObject.AddComponent(type);
        type.GetProperty("renderPostProcessing")?.SetValue(data, true);
    }

    void Ball(string name, Vector3 at, float size, Color c, float smooth, float metal)
    {
        var g = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        g.name = name;
        g.transform.position = at;
        g.transform.localScale = Vector3.one * size;
        Paint(g, c, null, Vector2.one, smooth, metal);
    }

    void BuildDarkRoom()
    {
        // A cave's light: almost none around, and nothing of the bright sky in its reflections.
        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.03f, 0.03f, 0.035f);
        RenderSettings.reflectionIntensity = 0.04f;
        var stone = new Color(0.45f, 0.45f, 0.42f);
        Box("Room roof", new Vector3(0, 3.3f, 2), new Vector3(10.4f, 0.2f, 12.4f), stone);
        Box("Room wall N", new Vector3(0, 1.6f, 8), new Vector3(10, 3.2f, 0.2f), stone);
        Box("Room wall S", new Vector3(0, 1.6f, -4), new Vector3(10, 3.2f, 0.2f), stone);
        Box("Room wall E", new Vector3(5, 1.6f, 2), new Vector3(0.2f, 3.2f, 12), stone);
        Box("Room wall W", new Vector3(-5, 1.6f, 2), new Vector3(0.2f, 3.2f, 12), stone);
        lamp = new GameObject("Ceiling lamp").AddComponent<Light>();
        lamp.type = LightType.Point;
        lamp.color = new Color(1f, 0.78f, 0.5f);
        lamp.range = 9;
        lamp.intensity = 3;
        lamp.shadows = LightShadows.Soft;
        lamp.transform.position = new Vector3(1.8f, 3f, 1.2f);
        lamp.enabled = false;
        torch = new GameObject("Flashlight").AddComponent<Light>();
        torch.type = LightType.Spot;
        torch.color = new Color(0.9f, 0.95f, 1f);
        torch.spotAngle = 40;
        torch.innerSpotAngle = 22;
        torch.range = 16;
        torch.intensity = 6;
        torch.shadows = LightShadows.Soft;
        torch.transform.SetParent(cam.transform, false);
        torch.enabled = false;
        // Three lamps in three places: warm on the left wall, blue on the right, pink behind her.
        party = new[]
        {
            Lamp("Warm lamp", new Vector3(-3.5f, 1.6f, -0.5f), new Color(1f, 0.6f, 0.25f), 3.5f),
            Lamp("Blue lamp", new Vector3(3.2f, 1.4f, -2.2f), new Color(0.3f, 0.5f, 1f), 3.5f),
            Lamp("Pink lamp", new Vector3(0.5f, 2.2f, 1.8f), new Color(1f, 0.35f, 0.7f), 3f),
        };
    }

    static Light Lamp(string name, Vector3 at, Color c, float intensity)
    {
        var l = new GameObject(name).AddComponent<Light>();
        l.type = LightType.Point;
        l.color = c;
        l.range = 7;
        l.intensity = intensity;
        l.shadows = LightShadows.Soft;
        l.transform.position = at;
        l.enabled = false;
        return l;
    }

    /// <summary>The light test: everyone stands still, the camera looks into the room, the lights take turns.</summary>
    void DarkStep()
    {
        cam.transform.position = new Vector3(2.6f, 1.7f, -3.2f);
        cam.transform.rotation = Quaternion.LookRotation(new Vector3(-0.2f, 1.0f, 1.2f) - cam.transform.position);
        lamp.enabled = t >= 4 && t < 8;
        torch.enabled = t >= 8 && t < 12;
        // The flashlight on her (she waits beside the player, about here).
        torch.transform.rotation = Quaternion.LookRotation(new Vector3(0f, 1.1f, -1.6f) - cam.transform.position);
        foreach (var l in party) l.enabled = t >= 12;
    }

    GameObject Box(string name, Vector3 at, Vector3 size, Color c)
    {
        var g = GameObject.CreatePrimitive(PrimitiveType.Cube);
        g.name = name;
        g.transform.position = at;
        g.transform.localScale = size;
        Paint(g, c);
        return g;
    }

    static Material lit;

    /// <summary>A URP Lit material of colour <paramref name="c"/> (a primitive's default material is the
    /// Built-in pipeline's, which URP draws magenta).</summary>
    static void Paint(GameObject g, Color c) => Paint(g, c, Concrete, new Vector2(2, 2), 0.3f);

    static void Paint(GameObject g, Color c, Texture2D tex, Vector2 tiling, float smooth, float metal = 0)
    {
        if (lit == null) lit = Resources.Load<Material>("Lit");
        var m = new Material(lit);
        m.SetColor("_BaseColor", c);
        if (tex != null)
        {
            m.SetTexture("_BaseMap", tex);
            m.SetTextureScale("_BaseMap", tiling);
        }
        m.SetFloat("_Smoothness", smooth);
        m.SetFloat("_Metallic", metal);
        g.GetComponent<Renderer>().sharedMaterial = m;
    }

    static Texture2D floor, concrete;

    /// <summary>Stone tiles: grout lines, each tile its own shade, fine noise.</summary>
    static Texture2D Floor => floor ??= MakeTexture((x, y) =>
    {
        int tx = x / 64, ty = y / 64;
        bool grout = x % 64 < 3 || y % 64 < 3;
        float tile = 0.82f + 0.18f * Hash(tx, ty);
        float n = Mathf.PerlinNoise(x * 0.08f, y * 0.08f) * 0.12f + Mathf.PerlinNoise(x * 0.5f, y * 0.5f) * 0.06f;
        return grout ? 0.45f : tile - n;
    });

    /// <summary>Concrete: two octaves of noise and a few dark flecks.</summary>
    static Texture2D Concrete => concrete ??= MakeTexture((x, y) =>
    {
        float n = Mathf.PerlinNoise(x * 0.05f, y * 0.05f) * 0.15f + Mathf.PerlinNoise(x * 0.3f, y * 0.3f) * 0.08f;
        return 0.9f - n - (Hash(x, y) > 0.985f ? 0.25f : 0f);
    });

    static Texture2D MakeTexture(Func<int, int, float> shade)
    {
        const int N = 256;
        var t = new Texture2D(N, N, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Repeat, anisoLevel = 8 };
        var px = new Color32[N * N];
        for (int y = 0; y < N; y++)
            for (int x = 0; x < N; x++)
            {
                byte v = (byte)(Mathf.Clamp01(shade(x, y)) * 255);
                px[y * N + x] = new Color32(v, v, v, 255);
            }
        t.SetPixels32(px);
        t.Apply(true);
        return t;
    }

    static float Hash(int x, int y)
    {
        uint h = (uint)(x * 73856093) ^ (uint)(y * 19349663);
        h ^= h >> 13;
        h *= 0x5bd1e995;
        h ^= h >> 15;
        return (h & 0xffff) / 65535f;
    }

    void Update()
    {
        float dt = Time.deltaTime;
        t += dt;
        if (dark)
        {
            DarkStep();
            Shoot();
            return;
        }
        // Walk the loop; run on the long legs; hop now and then.
        var at = body.transform.position;
        var goal = Route[leg];
        var to = new Vector3(goal.x - at.x, 0, goal.z - at.z);
        if (to.magnitude < 0.5f) leg = (leg + 1) % Route.Length;
        float speed = leg % 2 == 0 ? 4.5f : 1.6f;
        if (t % 9 > 7.5f) speed = 0; // stand still for a moment each loop
        var move = to.normalized * speed;
        if (body.isGrounded)
        {
            vy = -1;
            if (t % 7 < dt) vy = 5; // a jump
        }
        else vy -= 9.81f * dt;
        body.Move((move + Vector3.up * vy) * dt);
        if (move.sqrMagnitude > 0.01f)
            body.transform.rotation = Quaternion.RotateTowards(body.transform.rotation, Quaternion.LookRotation(move), 360 * dt);

        // Whip the camera round now and then: 120° in a third of a second, and back.
        if (t > nextWhip) { yawOffset = yawOffset == 0 ? 120 : 0; nextWhip = t + 4; }
        float yaw = Mathf.MoveTowardsAngle(cam.transform.eulerAngles.y, body.transform.eulerAngles.y + yawOffset, 360 * dt);
        cam.transform.position = body.transform.position + Quaternion.Euler(0, yaw, 0) * new Vector3(1.2f, 1.6f, -5f);
        cam.transform.rotation = Quaternion.Euler(12, yaw, 0);

        Shoot();
    }

    void Shoot()
    {
        if (shots != null && t > nextShot)
        {
            nextShot += shotEvery;
            StartCoroutine(Shot($"shot-{++shotN:00}.png"));
        }
        if (quitAt > 0 && t > quitAt) Application.Quit();
    }

    IEnumerator Shot(string name)
    {
        yield return new WaitForEndOfFrame();
        string path = Path.Combine(shots, name);
        if (offscreen == null)
        {
            ScreenCapture.CaptureScreenshot(path);
        }
        else
        {
            var prev = RenderTexture.active;
            RenderTexture.active = offscreen;
            var tex = new Texture2D(offscreen.width, offscreen.height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, offscreen.width, offscreen.height), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Destroy(tex);
        }
        Debug.Log($"[testbed] {name}: player {body.transform.position}");
    }
}
