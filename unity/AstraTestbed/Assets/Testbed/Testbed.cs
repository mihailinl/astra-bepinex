// SPDX-License-Identifier: MIT
using System;
using System.Collections;
using System.IO;
using UnityEngine;

/// <summary>
/// A tiny "game" to test the plugins on, built entirely in code (no assets to keep in sync): ground,
/// walls, a ramp, stairs, a sun, and a player — a capsule tagged <c>Player</c> with a
/// <see cref="CharacterController"/> — that walks a loop, runs, jumps, while a third-person camera
/// follows and now and then whips round (the turn that shows ghosting).
/// <para>
/// Command line: <c>-astraShots &lt;dir&gt;</c> saves a PNG every 4 s; <c>-astraQuit &lt;s&gt;</c> quits
/// then; <c>-astraOffscreen</c> renders into a texture (batch mode has no window).
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

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot() => new GameObject("Testbed").AddComponent<Testbed>();

    void Start()
    {
        var args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "-astraShots") shots = args[i + 1];
            if (args[i] == "-astraQuit") quitAt = float.Parse(args[i + 1], System.Globalization.CultureInfo.InvariantCulture);
        }
        if (shots != null) Directory.CreateDirectory(shots);
        Application.targetFrameRate = 60;
        BuildWorld();
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
        Paint(ground, new Color(0.35f, 0.45f, 0.3f));

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
        sun.transform.rotation = Quaternion.Euler(45, -30, 0);
        RenderSettings.sun = sun;
        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
        RenderSettings.ambientSkyColor = new Color(0.45f, 0.55f, 0.7f);
        RenderSettings.ambientEquatorColor = new Color(0.4f, 0.4f, 0.4f);
        RenderSettings.ambientGroundColor = new Color(0.2f, 0.18f, 0.15f);

        var player = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        player.name = "Player";
        player.tag = "Player";
        UnityEngine.Object.Destroy(player.GetComponent<Collider>());
        Paint(player, new Color(0.2f, 0.4f, 0.9f));
        player.transform.position = new Vector3(0, 1, 0);
        body = player.AddComponent<CharacterController>();
        body.height = 2;
        body.radius = 0.4f;

        cam = new GameObject("Main Camera") { tag = "MainCamera" }.AddComponent<Camera>();
        cam.fieldOfView = 60;
        cam.nearClipPlane = 0.1f;
        cam.farClipPlane = 500;
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
    static void Paint(GameObject g, Color c)
    {
        if (lit == null) lit = Resources.Load<Material>("Lit");
        var m = new Material(lit);
        m.SetColor("_BaseColor", c);
        g.GetComponent<Renderer>().sharedMaterial = m;
    }

    void Update()
    {
        float dt = Time.deltaTime;
        t += dt;
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
