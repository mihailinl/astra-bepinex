// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using Astra.Bridge;
using Astra.Sdk;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.Rendering;

namespace Astra.Unity
{
    /// <summary>
    /// The foundation's one MonoBehaviour. It owns the link to Astra, the frame ring and the
    /// systems, and runs them at the points of Unity's frame that every Unity game has:
    /// <list type="number">
    /// <item><c>LateUpdate</c> (the game has moved the player by now): the FRAME. The player is
    /// located, the integration's frame handlers set raw facts, and the brain places her. Each comes
    /// from the game's integration (<see cref="AstraSdk"/>) when it has one, else from the
    /// foundation's default;</item>
    /// <item>a camera BEGINS rendering: her light, the cues, her placement, then the camera. The
    /// <c>cam</c> goes LAST because it commits the frame and the engine starts drawing at once;</item>
    /// <item>inside or after that camera (post-processing included): her picture is composited.</item>
    /// </list>
    /// Every callback is fenced. A fault in an INTEGRATION's code drops the integration (the
    /// foundation's defaults take over); a fault in the foundation stands it down. Either way it is
    /// logged, and the game goes on.
    /// </summary>
    public sealed class Driver : MonoBehaviour
    {
#if IL2CPP
        public Driver(IntPtr ptr) : base(ptr) { }
#endif
        /// <summary>Her height at scale 1, in metres: what <see cref="IntegrationDefaults.MatchPlayerHeight"/>
        /// sizes against. Nominal (the built-in character); the engine will report the real one.</summary>
        const float HerHeight = 1.55f;

        static ManualLogSource log;
        static Settings settings;

        BridgeLink link;
        FrameRing ring;
        int ringSession;
        Compositor compositor;
        ShadowCaster shadow;
        readonly CameraFeed shots = new CameraFeed();
        readonly LightFeed light = new LightFeed();
        readonly ParamSet parameters = new ParamSet();
        readonly HashSet<int> ignore = new HashSet<int>();
        GameObject ignoredRoot;
        float nextIgnoreRefresh;

        // Field initialisers, never a constructor: an IL2CPP game creates this component through its
        // IntPtr constructor, which would skip a parameterless one.
        readonly FrameContext frame = new FrameContext(new Her(), new ParamSet(), new Cues());
        readonly FollowBrain follow = new FollowBrain();
        readonly DefaultPlayer defaultPlayer = new DefaultPlayer();
        GameIntegration integration;
        GameIntegration dropped; // an integration that faulted: not taken up again until it re-registers
        Vector3 lastPosition, velocity;
        Vector3 goodPosition, goodFacing = Vector3.forward;
        float scale = 1;
        // Her size from the player's height (MatchPlayerHeight): the size in force, a new one waiting
        // to prove itself steady, and since when.
        float matched = -1, pending, pendingSince;
        float lastSent; // when the last message went to the engine (unscaled time)
        float nextLightLog;
        string loggedLight;
        static readonly string KeepAlive = Messages.Param(new ParamSet()); // an empty set: changes nothing

        Camera main;
        float nextCameraSearch;
        KeyCode toggle = KeyCode.None;
        int mask = Physics.DefaultRaycastLayers;
        string maskLayers;
        bool srp, hdrp, faulted, visible = true;
        bool inGraph;      // this frame's composite was queued into the camera's render graph
        bool contextEnd;   // the SRP reports the end of a whole render context (draw there)
        bool mainRendered; // the main camera rendered in the current context
        bool wasLive;      // the link and the ring were up last frame
        readonly HashSet<string> logged = new HashSet<string>();

        Her her => frame.Her;

        /// <summary>Called by the plugin entry before the component is added.</summary>
        internal static void Configure(ManualLogSource source, Settings s)
        {
            log = source;
            settings = s;
        }

        /// <summary>Show or hide her (the hotkey).</summary>
        public bool Visible
        {
            get => visible;
            set => visible = value;
        }

        void Start()
        {
            Fenced("start", () =>
            {
                if (!string.IsNullOrEmpty(settings.Toggle.Value))
                {
                    try { toggle = (KeyCode)Enum.Parse(typeof(KeyCode), settings.Toggle.Value.Trim(), true); }
                    catch (ArgumentException) { log.LogWarning($"Toggle: no key named '{settings.Toggle.Value}'"); }
                }
                compositor = Compositor.Create();
                if (compositor == null) log.LogError($"Astra cannot be drawn in this game: {Compositor.Failure}");
                else
                {
                    compositor.Bind(shots, settings, ViewOf);
                    compositor.Note = m => log.LogInfo("compositor: " + m);
                }
                shadow = new ShadowCaster { Note = m => log.LogInfo(m) };
                srp = GraphicsSettings.currentRenderPipeline != null;
                string pipeline = Compat.PipelineClass();
                hdrp = srp && pipeline != null && pipeline.EndsWith(".HDRenderPipelineAsset", StringComparison.Ordinal);
                UrpHook.TryLoad();
                HdrpHook.TryLoad();
                if (hdrp && !HdrpHook.Active && compositor != null)
                {
                    // Without its adapter, HDRP's depth (a mip-pyramid atlas array) cannot be read.
                    compositor.DepthTest = false;
                    log.LogWarning($"HDRP without its adapter ({HdrpHook.Missing}): she is drawn over everything");
                }
                if (srp)
                {
                    Compat.HookSrp(OnBeginCamera, OnEndCamera);
                    contextEnd = Compat.HookSrpContextEnd(OnEndContext);
                }
                else Compat.HookBuiltIn(OnBuiltInPreCull);
                log.LogInfo(UrpHook.Active ? $"compositing inside the camera's URP 17 render ({(UrpHook.RenderGraph ? "render graph" : "Compatibility Mode")}), after post-processing"
                    : HdrpHook.Active ? "compositing in an HDRP custom pass, against the camera's depth buffer"
                    : $"compositing after {(contextEnd ? "the frame's cameras" : "the camera")} ({(hdrp ? HdrpHook.Missing : UrpHook.Missing)})");
                log.LogInfo($"{Application.productName} (Unity {Application.unityVersion}, {SystemInfo.graphicsDeviceType}, " +
                            $"{(srp ? Compat.PipelineClass() : "Built-in pipeline")})");
                // The link itself is started lazily, from LateUpdate: never while there is no way to
                // draw her (B1 — "the foundation must never hold her when it cannot draw her").
            });
        }

        void Update()
        {
            if (toggle == KeyCode.None) return;
            try
            {
                if (Input.GetKeyDown(toggle)) visible = !visible;
            }
            catch (Exception)
            {
                // The game uses only the new Input System: no legacy input to read the hotkey from
                // (an InvalidOperationException; under IL2CPP an Il2CppException).
                log.LogWarning("this game has no legacy input: the toggle key is off (use General.Enabled)");
                toggle = KeyCode.None;
            }
        }

        void LateUpdate()
        {
            if (faulted) return;
            Fenced("frame", () =>
            {
                AdoptIntegration();
                UpdateLink();

                if (link != null && link.Session != ringSession) OpenRing();
                bool live = link != null && link.Ready && ring != null;
                // The engine went away: forget her last picture, or it would stay composited — a
                // frozen cut-out at her old place — until it comes back.
                if (wasLive && !live)
                {
                    compositor?.Reset();
                    shadow?.Reset(0);
                }
                wasLive = live;
                Runtime.Connected = live;
                if (compositor != null) compositor.Show = live && visible && settings.Enabled.Value;
                // No player (a menu, a loading screen) means nothing to send — and the engine hands her
                // back to the desktop after 10 s of silence, then the game takes her again: a loop.
                // While the game runs, it keeps her.
                if (link != null && link.Ready && Time.unscaledTime - lastSent > 2f)
                {
                    link.Send(KeepAlive);
                    lastSent = Time.unscaledTime;
                }
                var pick = integration?.CameraLocator;
                if (pick != null)
                {
                    main = Guarded("camera", pick, () => Camera.main);
                }
                else if (Time.unscaledTime > nextCameraSearch || main == null || !main.isActiveAndEnabled)
                {
                    main = Camera.main;
                    nextCameraSearch = Time.unscaledTime + 1f;
                }
                // Skip her placement and follow logic while there is no picture to draw her into
                // anyway (M6): the engine disconnected, or the ring is not open yet.
                if (main != null && live) RunFrame(main);
            });
        }

        /// <summary>
        /// B1: the link's lifetime follows whether she can be drawn at all right now — never held
        /// while she cannot be. Starts it (lazily — never in <see cref="Start"/>) the first time she
        /// could be shown, tells it every frame that the main thread is alive
        /// (<see cref="BridgeLink.MarkWanted"/>), and closes it AT ONCE (not after the heartbeat's own
        /// timeout, which exists for a STALLED main thread, not this one) the moment she stops being
        /// wanted: the toggle key, <c>General.Enabled</c>, or no compositor at all (an unsupported
        /// Unity line, platform or pipeline).
        /// </summary>
        void UpdateLink()
        {
            bool wanted = compositor != null && visible && settings.Enabled.Value;
            if (wanted)
            {
                if (link == null)
                {
                    link = new BridgeLink("astra-unity/" + Application.productName, settings.Port.Value,
                        string.IsNullOrEmpty(settings.Token.Value) ? null : settings.Token.Value);
                    link.Log = m => log.LogInfo(m);
                    // Her shadow here is the game's own, cast by her caster: the engine need not make
                    // her view from the sun (and makes neither when shadows are off).
                    link.Shadow = settings.Shadows.Value ? "caster" : "none";
                    ringSession = 0;
                    link.Start();
                }
                link.MarkWanted();
            }
            else if (link != null)
            {
                link.Dispose();
                link = null;
                ringSession = 0;
                ring?.Dispose();
                ring = null;
                if (compositor != null) compositor.Ring = null;
            }
        }

        /// <summary>One frame: locate the player, let the integration set facts, let the brain place her.</summary>
        void RunFrame(Camera cam)
        {
            var d = integration?.Defaults;
            bool playerSetLayers = settings.GroundLayers.Value != (string)settings.GroundLayers.DefaultValue;
            if (!playerSetLayers && d?.GroundMask != null)
            {
                mask = d.GroundMask.Value;
                maskLayers = null;
            }
            else
            {
                string layers = Settings.Pick(settings.GroundLayers, d?.GroundLayers);
                if (layers != maskLayers || maskLayers == null)
                {
                    maskLayers = layers ?? "";
                    mask = Mask(layers);
                }
            }
            frame.DeltaTime = Time.deltaTime;
            frame.Camera = cam;
            frame.GroundMask = mask;
            frame.Follow = new FollowSettings
            {
                KeepDistance = Settings.Pick(settings.KeepDistance, d?.KeepDistance),
                FollowStart = Settings.Pick(settings.FollowStart, d?.FollowStart),
                TeleportDistance = Settings.Pick(settings.TeleportDistance, d?.TeleportDistance),
                MaxSpeed = Settings.Pick(settings.MaxSpeed, d?.MaxSpeed),
            };
            frame.Default = follow;
            frame.Ignore = ignore;
            frame.Params.Clear();

            // The player the config names wins over the integration's; the default last.
            string configured = settings.PlayerObject.Value;
            frame.Player = string.IsNullOrEmpty(configured) && integration?.PlayerLocator != null
                ? Guarded("player", () => Checked(integration.PlayerLocator(cam)), () => defaultPlayer.Locate(cam, configured, mask))
                : defaultPlayer.Locate(cam, configured, mask);
            IgnorePlayer(frame.Player?.Root);
            frame.HoldImpl = follow.Hold;

            if (integration != null)
                foreach (var handler in integration.FrameHandlers.ToArray())
                    if (!Guarded("frame handler", () => { handler(frame); return true; }, () => false)) break;

            var brain = integration?.Brain;
            if (brain == null || !Guarded("brain", () => { brain.Step(frame); Checked(her); return true; }, () => false)) follow.Step(frame);
            if (!Finite(her.Position) || !Finite(her.Facing) || (her.Up.HasValue && !Finite(her.Up.Value)))
            {
                // Only reachable from the foundation's own follower now (an integration's NaN is
                // caught above): put her back where she last was whole, and let her be placed anew.
                Once("nan", "her placement came out non-finite; she is placed again");
                her.Position = goodPosition;
                her.Facing = goodFacing;
                her.Up = null;
                her.Placed = false;
            }
            else
            {
                goodPosition = her.Position;
                goodFacing = her.Facing;
            }

            // Her motion, measured: a jump of metres in a frame is a teleport, not speed.
            float dt = Mathf.Max(frame.DeltaTime, 1e-4f);
            var moved = her.Position - lastPosition;
            lastPosition = her.Position;
            velocity = moved.magnitude > 3f ? Vector3.zero : Vector3.Lerp(velocity, moved / dt, 1 - Mathf.Exp(-dt * 12));
            if (!Finite(velocity)) velocity = Vector3.zero;

            scale = Size(d, dt);
        }

        /// <summary>
        /// Her size: the player's own Scale wins; else the integration's "this tall next to the
        /// player" (<see cref="IntegrationDefaults.MatchPlayerHeight"/>); else its fixed Scale; else 1.
        /// A player's height is their STANDING height, but a game may report the height of the pose
        /// of the moment (PEAK's head height drops in a crouch): so a new height is taken up only
        /// once it has held for a second, an unknown one (0) keeps the size she has, and she grows or
        /// shrinks smoothly — a crouch, a jump or one odd frame never resizes her.
        /// </summary>
        float Size(IntegrationDefaults d, float dt)
        {
            float fixedScale = Settings.Pick(settings.Scale, d?.Scale);
            bool playerSetScale = settings.Scale.Value != (float)settings.Scale.DefaultValue;
            float? match = d?.MatchPlayerHeight;
            if (playerSetScale || !match.HasValue)
            {
                matched = -1;
                return fixedScale;
            }
            float height = frame.Player?.Height ?? 0;
            if (height > 0 && !float.IsInfinity(height))
            {
                float wanted = Mathf.Clamp(height * match.Value / HerHeight, 0.05f, 20f);
                if (matched < 0)
                {
                    matched = pending = wanted; // the first height: at once, before she is seen
                    pendingSince = Time.unscaledTime;
                    log.LogInfo($"sized to the player: {height:0.00} units tall -> her scale {matched:0.00}");
                    return matched;
                }
                if (Mathf.Abs(wanted - pending) > 0.03f * pending)
                {
                    pending = wanted;
                    pendingSince = Time.unscaledTime;
                }
                else if (Time.unscaledTime - pendingSince > 1f && Mathf.Abs(pending - matched) > 0.03f * matched)
                {
                    matched = pending;
                    log.LogInfo($"sized to the player: {height:0.00} units tall -> her scale {matched:0.00}");
                }
            }
            if (matched < 0) return fixedScale;
            return Mathf.MoveTowards(scale, matched, Mathf.Max(scale, matched) * 0.5f * dt);
        }

        /// <summary>Take up the integration registered most recently (and say so) — unless it is the
        /// one that faulted: that stays off until it registers again (a new registration is a new
        /// object).</summary>
        void AdoptIntegration()
        {
            var current = AstraSdk.Current;
            if (current == integration || ReferenceEquals(current, dropped)) return;
            integration = current;
            extraCameras.Clear(); // the cameras it wanted her in are its own answer
            views.Clear();
            if (current != null) log.LogInfo($"integration '{current.Id}' for {current.Game} is in charge");
        }

        static bool Finite(Vector3 v) =>
            !(float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z) || float.IsInfinity(v.x) || float.IsInfinity(v.y) || float.IsInfinity(v.z));

        /// <summary>An integration's player, refused (as a fault of the integration) if it is not whole.</summary>
        static PlayerInfo? Checked(PlayerInfo? p)
        {
            if (p.HasValue && (!Finite(p.Value.Feet) || !Finite(p.Value.Forward) || float.IsNaN(p.Value.Height) || float.IsInfinity(p.Value.Height)))
                throw new ArithmeticException("the player's feet, facing or height are not finite numbers");
            return p;
        }

        /// <summary>Her placement after an integration's brain, refused (as its fault) if it is not whole.</summary>
        static void Checked(Her h)
        {
            if (!Finite(h.Position) || !Finite(h.Facing) || (h.Up.HasValue && !Finite(h.Up.Value)))
                throw new ArithmeticException("her position, facing or up are not finite numbers");
        }

        /// <summary>Run an INTEGRATION's code; on a fault, log it, drop the integration (until it
        /// registers again) and fall back to the foundation's default for this frame.</summary>
        T Guarded<T>(string what, Func<T> body, Func<T> fallback)
        {
            try
            {
                return body();
            }
            catch (Exception e)
            {
                log.LogError($"the integration '{integration?.Id}' failed ({what}) and is switched off; Astra goes on with her defaults: {e}");
                dropped = integration;
                integration = null;
                extraCameras.Clear(); // the cameras it wanted her in were its answer, gone with it
                views.Clear();
                return fallback();
            }
        }

        /// <summary>Her player's own colliders are never ground or walls for her.</summary>
        void IgnorePlayer(GameObject root)
        {
            if (root == ignoredRoot && Time.unscaledTime < nextIgnoreRefresh) return;
            if (root != ignoredRoot) matched = -1; // another player (or none): size her to them afresh
            ignoredRoot = root;
            nextIgnoreRefresh = Time.unscaledTime + 2f;
            ignore.Clear();
            if (root != null)
                foreach (var c in Compat.CollidersUnder(root))
                    ignore.Add(c.GetInstanceID());
        }

        void OpenRing()
        {
            ringSession = link.Session;
            ring?.Dispose();
            ring = null;
            if (compositor != null) compositor.Ring = null;
            compositor?.Reset();
            shadow?.Reset(0);
            light.Reset();
            views.Count = 0;
            try
            {
                ring = FrameRing.Open(link.Hello.Shm, settings.RingPath.Value);
                if (compositor != null) compositor.Ring = ring;
                // Only frames this session publishes; the ones still in the ring are the last engine's.
                compositor?.Reset(ring.Counter);
                shadow?.Reset(ring.Counter);
                // Views besides the main one: as many as the engine draws and its ring holds (none for an
                // engine from before views, which would take every camera for the main one).
                // Half the slots at most: a wake writes one frame per view, and the next wake must not
                // come round to a slot the game is still copying the last one's from.
                views.Count = Math.Min(link.Hello.Views, ring.Slots / 2) - 1;
                log.LogInfo($"frames from {ring.Source} ({ring.MaxWidth}x{ring.MaxHeight})");
            }
            catch (System.IO.IOException e)
            {
                log.LogError($"cannot read Astra's frames ({e.Message}). If this game runs under Wine/Proton, " +
                             "set Connection.RingPath to Z:\\dev\\shm\\astra-frame");
                // B1: without a ring there is nothing to draw her with — let go rather than hold a
                // claim she can never be shown through. UpdateLink() opens a fresh one next frame.
                link?.Dispose();
                link = null;
                ringSession = 0;
            }
        }

        /// <summary>Cameras the integration asked her into (besides the main one), checked once each;
        /// the yes ones get a VIEW of their own while one is free — the engine draws her for each.</summary>
        readonly Dictionary<int, bool> extraCameras = new Dictionary<int, bool>();
        readonly ViewTable views = new ViewTable();

        /// <summary>The view <paramref name="cam"/> is: 0 for the main camera (and an extra camera that
        /// found no view free, which then gets the main picture reprojected).</summary>
        int ViewOf(Camera cam) => cam == main ? 0 : views.Of(cam);
        readonly HashSet<int> extraInGraph = new HashSet<int>();

        /// <summary>Is <paramref name="cam"/> one the integration wants her in too? (Its answer is kept
        /// per camera; its depth texture is switched on once.)</summary>
        bool IsExtra(Camera cam)
        {
            var pick = integration?.ExtraCamera;
            if (pick == null || cam == null) return false;
            int id = cam.GetInstanceID();
            if (!extraCameras.TryGetValue(id, out bool yes))
            {
                yes = Guarded("extra camera", () => pick(cam), () => false);
                extraCameras[id] = yes;
                if (yes)
                {
                    Compat.RequestDepthTexture(cam);
                    log.LogInfo($"also drawing her into the camera '{cam.name}'");
                }
            }
            return yes;
        }

        void OnBeginCamera(Camera cam)
        {
            // Only where she can be drawn INTO another camera (URP — Built-in and HDRP composite into
            // the main one only) and while she is shown: else a view would be drawn for nobody.
            if (!faulted && cam != main && compositor != null && srp && !hdrp && compositor.Show && her.Placed && link != null && link.Ready && IsExtra(cam))
            {
                // Another camera of the game (a video camera, a mirror): its own view — the engine draws
                // her for it (an engine without views leaves it the main picture, reprojected).
                int view = views.Assign(cam, out int taken);
                if (taken != 0)
                {
                    compositor.ForgetView(taken);
                    log.LogInfo($"the camera '{cam.name}' is her view {taken}");
                }
                if (view != 0) Fenced("send", () => Send(shots.Message(cam, settings.MaxPictureHeight.Value, view)));
                if (srp && !hdrp && UrpHook.Enqueue(cam, compositor)) extraInGraph.Add(cam.GetInstanceID());
                else extraInGraph.Remove(cam.GetInstanceID());
                return;
            }
            if (faulted || cam != main || link == null || !link.Ready) return;
            Fenced("send", () =>
            {
                if (!her.Placed) return;
                if (settings.SendLight.Value)
                {
                    // The integration's sun runs under ITS guard: a throw there drops the integration
                    // (the scene's sun takes over), never the foundation.
                    var choose = integration?.Sun;
                    Light sun = choose != null ? Guarded("sun", () => choose(), () => null) : null;
                    light.Physical = hdrp;
                    if (hdrp)
                    {
                        light.Exposure = HdrpHook.Exposure(cam);
                        light.PipelineAmbient ??= () => HdrpHook.Ambient(main);
                        light.PipelineFog ??= () => HdrpHook.Fog(main);
                    }
                    // URP 17's post-exposure (1 elsewhere, including HDRP: its own exposure already
                    // covers it): read once per light message, folded into every value the feed sends.
                    light.PostExposure = UrpHook.PostExposure();
                    Look? look = integration?.LookFloor is float floor
                        ? new Look { Floor = floor, Ceiling = integration.LookCeiling ?? 1.40f }
                        : (Look?)null;
                    Send(light.Message(her.Position, HerHeight * scale, ignore, choose != null && integration != null, sun,
                        cam, frame.Player?.Root, look));
                    // What she is lit with, every 10 s when it changed: the line a player pastes when she
                    // looks wrong.
                    if (Time.unscaledTime > nextLightLog && light.Summary != null && light.Summary != loggedLight)
                    {
                        log.LogInfo("her light here: " + light.Summary);
                        loggedLight = light.Summary;
                        nextLightLog = Time.unscaledTime + 10f;
                    }
                }
                SendCues(frame.Cues);
                if (integration != null) SendCues(integration.Cues);
                parameters.Clear();
                parameters.Set("speed", new Vector3(velocity.x, 0, velocity.z).magnitude).Set("airborne", her.Airborne);
                parameters.MergeFrom(frame.Params);
                Send(Messages.Avatar(new Placement
                {
                    Pos = Wire.Of(her.Position),
                    Fwd = Wire.Of(her.Facing),
                    Up = her.Up.HasValue ? Wire.Of(her.Up.Value) : (Vec3?)null,
                    Vel = Wire.Of(velocity),
                    Anchor = her.Anchor,
                    Scale = scale,
                }, parameters));
                Send(shots.Message(cam, settings.MaxPictureHeight.Value)); // LAST: it commits the frame
                lastSent = Time.unscaledTime;
            });
            // Her shadow, before this camera draws its shadow maps: the game's own lights cast it.
            Fenced("shadow", () => shadow?.Update(ring, cam, compositor != null && compositor.Show && settings.Shadows.Value));
            if (compositor == null || faulted) return;
            // URP 17: her pass goes INTO this camera's render graph (the only place its depth exists).
            // HDRP: her custom pass rides on this camera (attached once per main camera).
            inGraph = srp && (hdrp ? HdrpHook.Ensure(cam, compositor) : UrpHook.Enqueue(cam, compositor));
            string missing = hdrp ? HdrpHook.Missing : UrpHook.Missing;
            if (srp && !inGraph && missing != null) Once("adapter-fallback", $"compositing after the camera: {missing}");
        }

        /// <summary>Send a built message; a builder that refused its input (null) is said once per reason.</summary>
        void Send(string json)
        {
            if (json != null) link.Send(json);
            else if (Messages.Rejection != null) Once("rejected: " + Messages.Rejection, "a message to Astra was not sent: " + Messages.Rejection);
        }

        void SendCues(Cues cues)
        {
            foreach (var json in cues.Pending) link.Send(json);
            cues.Pending.Clear();
        }

        void OnEndCamera(Camera cam)
        {
            if (!faulted && cam != main && compositor != null && !hdrp && !extraInGraph.Contains(cam.GetInstanceID()) && IsExtraKnown(cam))
            {
                // Right after the camera, against its own depth texture (no stack to blit over her).
                DrawAfterCamera(cam);
                return;
            }
            if (faulted || cam != main || compositor == null || inGraph) return;
            // Where the SRP reports the end of the whole context, draw THERE: a camera stack's final
            // blit (the last overlay camera's) would otherwise copy over her.
            if (contextEnd)
            {
                // …but test against THIS camera's depth: by then another camera's may be bound.
                Fenced("draw", () => compositor.KeepDepth());
                mainRendered = true;
                return;
            }
            DrawAfterCamera(cam);
        }

        bool IsExtraKnown(Camera cam) =>
            integration?.ExtraCamera != null && her.Placed && link != null && link.Ready
            && extraCameras.TryGetValue(cam.GetInstanceID(), out bool yes) && yes;

        void OnEndContext()
        {
            if (!mainRendered) return;
            mainRendered = false;
            if (faulted || main == null || compositor == null || inGraph) return;
            DrawAfterCamera(main, keptDepth: true);
        }

        void DrawAfterCamera(Camera cam, bool keptDepth = false)
        {
            Fenced("draw", () =>
            {
                compositor.DrawNow(cam, keptDepth);
                Once("depth", !compositor.DepthTest ? "no depth test in this pipeline: she is drawn over everything"
                    : Shader.GetGlobalTexture(Shader.PropertyToID("_CameraDepthTexture")) != null
                        ? "the game's depth texture is bound after the camera: walls hide her"
                        : "the game binds no depth texture: she is drawn over everything");
            });
        }

        /// <summary>The Built-in pipeline: send at pre-cull, then set up what the camera's own
        /// command buffer draws at the end of its image effects (the picture newest by then).</summary>
        void OnBuiltInPreCull(Camera cam)
        {
            OnBeginCamera(cam);
            if (faulted || cam != main || compositor == null) return;
            Fenced("draw", () => compositor.PrepareBuiltIn(cam));
        }

        void Once(string key, string message)
        {
            if (logged.Add(key)) log.LogInfo(message);
        }

        void Fenced(string what, Action body)
        {
            try
            {
                body();
            }
            catch (Exception e)
            {
                faulted = true;
                Runtime.Connected = false;
                log.LogError($"Astra stopped ({what}): {e}");
                // Take her out of the picture too: a Built-in camera keeps running our command buffer.
                try { compositor?.Dispose(); }
                catch (Exception) { /* already broken */ }
                compositor = null;
                try { shadow?.Dispose(); }
                catch (Exception) { /* already broken */ }
                shadow = null;
                // B1: a foundation that is broken must let go of her too — never hold her mute.
                try { link?.Dispose(); }
                catch (Exception) { /* already broken */ }
                link = null;
            }
        }

        static int Mask(string layers)
        {
            if (string.IsNullOrEmpty(layers)) return Physics.DefaultRaycastLayers;
            int mask = 0;
            foreach (var name in layers.Split(','))
            {
                int l = LayerMask.NameToLayer(name.Trim());
                if (l >= 0) mask |= 1 << l;
                else log.LogWarning($"no layer named '{name.Trim()}'");
            }
            return mask == 0 ? Physics.DefaultRaycastLayers : mask;
        }

        void OnDestroy()
        {
            Runtime.Connected = false;
            Compat.UnhookAll();
            link?.Dispose();
            ring?.Dispose();
            compositor?.Dispose();
            shadow?.Dispose();
        }
    }
}
