// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using Astra.Bridge;
using UnityEngine;

namespace Astra.Sdk
{
    /// <summary>
    /// The Astra foundation's public API: what a GAME INTEGRATION builds on. The foundation already
    /// makes Astra work in any Unity game — she follows the player through the game's colliders, is lit
    /// by its sun and hidden by its walls. An integration makes her better in ONE game: who the
    /// player really is, where she stands and how she gets there, her size in that world, and raw
    /// facts about her for her animation set.
    /// <code>
    /// [BepInPlugin("astra.peak", "Astra for PEAK", "1.0.0")]
    /// [BepInDependency(AstraSdk.Guid, AstraSdk.Dependency)]
    /// public sealed class Plugin : BaseUnityPlugin
    /// {
    ///     void Awake()
    ///     {
    ///         var astra = AstraSdk.Register("astra.peak", "PEAK");
    ///         astra.Defaults.Scale = 1.3f;
    ///         astra.UsePlayer(cam => PeakPlayer.Locate());
    ///         astra.OnFrame(f => f.Params.Set("climbing", PeakPlayer.Climbing));
    ///     }
    /// }
    /// </code>
    /// Everything here is called on Unity's main thread. Every extension point has a default that
    /// already works; an integration replaces only what it knows better.
    /// </summary>
    public static class AstraSdk
    {
        /// <summary>The foundation's BepInEx GUID — name it in <c>[BepInDependency]</c>.</summary>
        public const string Guid = Astra.Unity.MyPluginInfo.PLUGIN_GUID;

        /// <summary>This SDK's (and its foundation's) version.</summary>
        public const string Version = Astra.Unity.MyPluginInfo.PLUGIN_VERSION;

        /// <summary>
        /// What an integration passes to <c>[BepInDependency(AstraSdk.Guid, AstraSdk.Dependency)]</c>:
        /// "this foundation or a newer one of the same major". BepInEx 5 reads a plain version as a
        /// MINIMUM; BepInEx 6 reads it as an EXACT version and wants a range — so the Mono build of
        /// this SDK carries the plain version and the IL2CPP build a range. Never pass
        /// <see cref="Version"/> there: on BepInEx 6 every foundation update would unload the
        /// integration.
        /// </summary>
#if IL2CPP
        public const string Dependency = ">=" + Version + " <" + NextMajor;
#else
        public const string Dependency = Version;
#endif

        /// <summary>The first version that may break integrations built against this one.</summary>
        const string NextMajor = "1.0.0";

        static GameIntegration current;

        /// <summary>The integration this game runs, or null (the foundation's defaults).</summary>
        public static GameIntegration Current => current;

        /// <summary>
        /// Register this game's integration. One per game: a second registration replaces the first
        /// (and the foundation logs it). Call it from your plugin's <c>Awake</c>/<c>Load</c>.
        /// </summary>
        /// <param name="id">Your integration's id (the marketplace id, e.g. <c>astra.peak</c>).</param>
        /// <param name="game">The game's name, for the logs.</param>
        public static GameIntegration Register(string id, string game)
        {
            var previous = current;
            current = new GameIntegration(id, game);
            Registered?.Invoke(previous, current);
            return current;
        }

        /// <summary>Is Astra's engine connected (her pictures arriving)?</summary>
        public static bool Connected => Astra.Unity.Runtime.Connected;

        /// <summary>For the foundation: told when an integration registers (old, new).</summary>
        internal static event Action<GameIntegration, GameIntegration> Registered;
    }

    /// <summary>One game's integration: what it changes about Astra in that game.</summary>
    public sealed class GameIntegration
    {
        internal Func<Camera, PlayerInfo?> PlayerLocator;
        internal Func<Camera> CameraLocator;
        internal Func<Camera, bool> ExtraCamera;
        internal IBrain Brain;
        internal Func<Light> Sun;
        internal float? LookFloor, LookCeiling;
        internal readonly List<Action<FrameContext>> FrameHandlers = new List<Action<FrameContext>>();

        internal GameIntegration(string id, string game)
        {
            Id = id;
            Game = game;
        }

        public string Id { get; }
        public string Game { get; }

        /// <summary>Defaults for the player's settings in this game. A value the player changed in the
        /// config always wins; a default can be changed at any time (e.g. once the game has loaded).</summary>
        public IntegrationDefaults Defaults { get; } = new IntegrationDefaults();

        /// <summary>Animation by name: the same cues Astra's own brain sends.</summary>
        public Cues Cues { get; } = new Cues();

        /// <summary>
        /// Who she accompanies. Called every frame; return null when there is no player right now (a
        /// menu, a cutscene) and she stays where she is. The default: the nearest object tagged
        /// <c>Player</c>, else the ground under the camera.
        /// </summary>
        public GameIntegration UsePlayer(Func<Camera, PlayerInfo?> locate)
        {
            PlayerLocator = locate;
            return this;
        }

        /// <summary>
        /// The camera she is composited into — the one the player looks through. Called every frame;
        /// return null for "none now" (she is not drawn). The default: <c>Camera.main</c> (the
        /// camera tagged MainCamera), which many games do not tag, or switch away from (a spectator
        /// camera, a vehicle camera).
        /// </summary>
        public GameIntegration UseCamera(Func<Camera> camera)
        {
            CameraLocator = camera;
            return this;
        }

        /// <summary>
        /// Draw her into OTHER cameras of the game too — an in-game video camera, a mirror, a security
        /// monitor: <paramref name="pick"/> is asked for each camera that renders (keep it cheap) and
        /// says whether she belongs in it. She is the main camera's picture reprojected into that
        /// camera's view and tested against ITS depth, so a camera looking roughly where the player
        /// looks shows her right; one pointed far away shows her only where the main picture covers.
        /// (URP; the Built-in pipeline and HDRP draw her into the main camera only, for now.)
        /// </summary>
        public GameIntegration AlsoDrawInto(Func<Camera, bool> pick)
        {
            ExtraCamera = pick;
            return this;
        }

        /// <summary>Where she stands and how she gets there, every frame. The default is the
        /// follower; your brain can call it (<see cref="FrameContext.Default"/>) for the frames it has
        /// nothing better to do.</summary>
        public GameIntegration UseBrain(IBrain brain)
        {
            Brain = brain;
            return this;
        }

        /// <summary>The light that is this game's sun (the default: the scene's sun, else its
        /// brightest directional light). Return null for "no sun right now" (night, indoors).</summary>
        public GameIntegration UseSun(Func<Light> sun)
        {
            Sun = sun;
            return this;
        }

        /// <summary>
        /// This game's taste for her light: the band the engine holds it in — how dim she gets where
        /// the game's light says darkness (<paramref name="floor"/>, the engine's default 0.20) and
        /// the brightest she approaches (<paramref name="ceiling"/>, default 1.40), as multiples of
        /// her albedo (the engine bounds them to 0.05..0.40 and 0.8..2.0). Set it only where a look
        /// at the game asks: a game whose dark is darker than most wants a lower floor.
        /// </summary>
        public GameIntegration UseLook(float floor, float ceiling = 1.40f)
        {
            LookFloor = floor;
            LookCeiling = ceiling;
            return this;
        }

        /// <summary>Called every frame before the brain: set raw facts in <see cref="FrameContext.Params"/>,
        /// send cues. Several handlers run in the order they were added.</summary>
        public GameIntegration OnFrame(Action<FrameContext> handler)
        {
            FrameHandlers.Add(handler);
            return this;
        }
    }

    /// <summary>The player she accompanies, as an integration (or the default) sees them this frame.</summary>
    public struct PlayerInfo
    {
        /// <summary>The player's FEET: the ground point under them (the point she keeps near).</summary>
        public Vector3 Feet;
        /// <summary>Which way the player faces or moves, horizontal (zero = use the camera's).</summary>
        public Vector3 Forward;
        /// <summary>The player's root object: its colliders never count as ground or walls for her.
        /// Optional.</summary>
        public GameObject Root;
        /// <summary>Is the player on the ground (false while jumping, climbing, flying)?</summary>
        public bool Grounded;
        /// <summary>
        /// The player's STANDING height in game units (feet to the top of the head). Report 0 when
        /// the player is not standing (crouching, prone, mid-jump, climbing) or you do not know it:
        /// the foundation sizes her to it (<see cref="IntegrationDefaults.MatchPlayerHeight"/>) and
        /// keeps her size while it is 0. It also takes up a new height only once it has held for a
        /// second, so the height of a pose cannot resize her — but do not count on that.
        /// </summary>
        public float Height;
    }

    /// <summary>Where she is: what a brain reads and writes. Positions are the game's world.</summary>
    public sealed class Her
    {
        /// <summary>Her FEET (or the point <see cref="Anchor"/> names).</summary>
        public Vector3 Position;
        /// <summary>Her facing, horizontal for an upright stance.</summary>
        public Vector3 Facing = Vector3.forward;
        /// <summary>Optional: tilt her (a seat in a vehicle). Null keeps her upright.</summary>
        public Vector3? Up;
        /// <summary>Optional: which of her points <see cref="Position"/> names — a VRM humanoid bone
        /// (<c>"hips"</c>) or <c>"feet"</c> (null = feet).</summary>
        public string Anchor;
        /// <summary>Off the ground (a jump or a fall): a raw fact her animation set reads.</summary>
        public bool Airborne;
        /// <summary>Has she been placed in the world yet? Until then nothing is drawn.</summary>
        public bool Placed;
    }

    /// <summary>A brain: decides, every frame, where she stands (<see cref="FrameContext.Her"/>).</summary>
    public interface IBrain
    {
        void Step(FrameContext frame);
    }

    /// <summary>
    /// One frame of the game, as an integration sees it: the camera, the player, her, the raw facts
    /// for her animation set, and the game's physics queries the foundation makes safe to use
    /// (her player's own colliders and triggers are never hit, and the same calls work in a Mono
    /// and an IL2CPP game).
    /// </summary>
    public sealed class FrameContext
    {
        internal FrameContext(Her her, ParamSet extra, Cues cues)
        {
            Her = her;
            Params = extra;
            Cues = cues;
        }

        public float DeltaTime { get; internal set; }
        public Camera Camera { get; internal set; }
        /// <summary>The player this frame (null: none — a menu, a loading screen).</summary>
        public PlayerInfo? Player { get; internal set; }
        /// <summary>The camera is the player's own eyes (a first-person game): it stands within
        /// 0.75 units across of the player's feet, 0.3–2.5 above them. A raw fact a brain uses to keep
        /// her where such a player can see her — ahead and to the side, never behind.</summary>
        public bool FirstPerson { get; internal set; }
        /// <summary>Where she is; a brain writes it.</summary>
        public Her Her { get; }
        /// <summary>Her animation set's parameters beyond the two the foundation measures itself
        /// (<c>speed</c> from her motion, <c>airborne</c> from <see cref="Her.Airborne"/>): RAW FACTS
        /// only — <c>climbing = true</c>, never <c>animation = "climb"</c>. Cleared every frame.</summary>
        public ParamSet Params { get; }
        public Cues Cues { get; }
        /// <summary>The stock follower, for a brain that delegates the ordinary frames to it.</summary>
        public IBrain Default { get; internal set; }
        /// <summary>The layers she walks on and bumps into.</summary>
        public int GroundMask { get; internal set; }
        /// <summary>The effective settings (the player's, else the integration's defaults).</summary>
        public FollowSettings Follow { get; internal set; }

        internal HashSet<int> Ignore;

        /// <summary>The nearest hit along a ray (her player's colliders and triggers skipped).</summary>
        public bool Raycast(Vector3 origin, Vector3 direction, float distance, out RaycastHit hit) =>
            Astra.Unity.Compat.Raycast(origin, direction, distance, GroundMask, Ignore, out hit);

        /// <summary>The nearest hit of a capsule swept along a direction (the same skips).</summary>
        public bool CapsuleCast(Vector3 p1, Vector3 p2, float radius, Vector3 direction, float distance, out RaycastHit hit) =>
            Astra.Unity.Compat.CapsuleCast(p1, p2, radius, direction, distance, GroundMask, Ignore, out hit);

        /// <summary>Walkable ground below a point (a surface facing up, within <paramref name="reach"/>).</summary>
        public bool GroundBelow(Vector3 point, float reach, out RaycastHit hit) =>
            Raycast(point, Vector3.down, reach, out hit) && hit.normal.y > 0.5f;

        /// <summary>
        /// A spot about <paramref name="distance"/> from <paramref name="feet"/> where she can stand:
        /// walkable ground within a metre of the feet's height, nothing solid between it and the
        /// player's chest. Tried behind first, then the sides, then in front. False when there is none
        /// (a pinnacle, a narrow ledge) — never "the player's own feet".
        /// </summary>
        public bool TrySpotNear(Vector3 feet, Vector3 forward, float distance, out Vector3 spot) =>
            TrySpotNear(feet, forward, distance, false, out spot);

        /// <summary>
        /// <see cref="TrySpotNear(Vector3, Vector3, float, out Vector3)"/>, or with
        /// <paramref name="ahead"/> AHEAD of the player first: off to either side of where they look
        /// (never in their line of fire), then wider, then behind — where a first-person player
        /// sees her.
        /// </summary>
        public bool TrySpotNear(Vector3 feet, Vector3 forward, float distance, bool ahead, out Vector3 spot)
        {
            forward.y = 0;
            forward = forward.sqrMagnitude > 1e-6f ? forward.normalized : Vector3.forward;
            var chest = feet + Vector3.up * 1.2f;
            foreach (float angle in ahead ? AheadAngles : SpotAngles)
            {
                var dir = Quaternion.AngleAxis(angle, Vector3.up) * (ahead ? forward : -forward);
                var near = feet + dir * distance;
                if (Raycast(chest, dir, distance, out _)) continue;
                if (GroundBelow(near + Vector3.up * 1.5f, 3f, out var hit) && Mathf.Abs(hit.point.y - feet.y) < 1f)
                {
                    spot = hit.point;
                    return true;
                }
            }
            spot = feet;
            return false;
        }

        static readonly float[] SpotAngles = { 0, 40, -40, 80, -80, 120, -120, 180 };
        static readonly float[] AheadAngles = { 35, -35, 60, -60, 90, -90, 150, -150, 180 };

        /// <summary>
        /// Keep her where she stands for this frame, but obey gravity: on the ground she stays on it,
        /// with nothing under her she falls (and is <see cref="Her.Airborne"/>). Turns her toward
        /// <paramref name="lookAt"/>. For a brain that wants her to WAIT (the player is climbing, in a
        /// cutscene…) without the follower walking her off.
        /// </summary>
        public void Hold(Vector3 lookAt) => HoldImpl?.Invoke(this, lookAt);

        internal Action<FrameContext, Vector3> HoldImpl;
    }

    /// <summary>The follow settings in effect this frame.</summary>
    public struct FollowSettings
    {
        public float KeepDistance;
        public float FollowStart;
        public float TeleportDistance;
        public float MaxSpeed;
    }

    /// <summary>Defaults an integration sets for its game (null = the foundation's own default).</summary>
    public sealed class IntegrationDefaults
    {
        /// <summary>Her size in this world (1 = her own height in metres).</summary>
        public float? Scale;
        /// <summary>Size her so she is this fraction of the player's height (<see cref="PlayerInfo.Height"/>),
        /// when the player's height is known; wins over <see cref="Scale"/>.</summary>
        public float? MatchPlayerHeight;
        public float? KeepDistance;
        public float? FollowStart;
        public float? TeleportDistance;
        public float? MaxSpeed;
        /// <summary>Comma-separated layer names she walks on and bumps into.</summary>
        public string GroundLayers;
        /// <summary>The same as a layer MASK, for a game that keeps its own (wins over
        /// <see cref="GroundLayers"/>; the player's GroundLayers in the config wins over both).</summary>
        public int? GroundMask;
        public float? DepthBias;
        public float? DepthSoftness;
    }

    /// <summary>Animation by name, sent with the next frame.</summary>
    public sealed class Cues
    {
        internal readonly List<string> Pending = new List<string>();

        /// <summary>Enter a state of her animation set by name and hold it until the graph leaves it.</summary>
        public void Play(string state, string expectedSet = null) => Add(Messages.Play(state, expectedSet));

        /// <summary>Raise a token into her graph (the current state's edge for it, or nothing).</summary>
        public void Trigger(string name, string expectedSet = null) => Add(Messages.Trigger(name, expectedSet));

        /// <summary>Hand her back to her graph's default state.</summary>
        public void Resume() => Add(Messages.Resume());

        void Add(string json)
        {
            if (json != null && Pending.Count < 32) Pending.Add(json);
        }
    }
}
