// SPDX-License-Identifier: MIT
using BepInEx.Configuration;

namespace Astra.Unity
{
    /// <summary>
    /// Every knob, bound to the foundation's BepInEx config file
    /// (<c>BepInEx/config/astra.unity-foundation.cfg</c>). A game integration sets DEFAULTS
    /// (<see cref="Sdk.IntegrationDefaults"/>); <see cref="Pick{T}"/> applies the rule: a value the PLAYER changed
    /// wins, else the integration's default, else the foundation's.
    /// </summary>
    sealed class Settings
    {
        public ConfigEntry<bool> Enabled;
        public ConfigEntry<string> Toggle;
        public ConfigEntry<int> Port;
        public ConfigEntry<string> Token;
        public ConfigEntry<string> RingPath;

        public ConfigEntry<float> Scale;
        public ConfigEntry<string> PlayerObject;
        public ConfigEntry<float> KeepDistance;
        public ConfigEntry<float> FollowStart;
        public ConfigEntry<float> TeleportDistance;
        public ConfigEntry<float> MaxSpeed;
        public ConfigEntry<string> GroundLayers;

        public ConfigEntry<float> DepthBias;
        public ConfigEntry<float> DepthSoftness;
        public ConfigEntry<string> ColourSpace;
        public ConfigEntry<int> MaxPictureHeight;
        public ConfigEntry<bool> SendLight;

        /// <summary>The player's value if they changed it from the foundation's default, else the
        /// integration's default when it set one, else the foundation's.</summary>
        public static T Pick<T>(ConfigEntry<T> entry, T? integration) where T : struct =>
            !Equals(entry.Value, (T)entry.DefaultValue) || !integration.HasValue ? entry.Value : integration.Value;

        public static string Pick(ConfigEntry<string> entry, string integration) =>
            entry.Value != (string)entry.DefaultValue || integration == null ? entry.Value : integration;

        public static Settings Bind(ConfigFile c)
        {
            return new Settings
            {
                Enabled = c.Bind("General", "Enabled", true, "Show Astra in this game."),
                Toggle = c.Bind("General", "Toggle", "F8", "A key (Unity KeyCode name) that shows or hides her. Empty = none."),
                Port = c.Bind("Connection", "Port", Bridge.BridgeLink.DefaultPort, "Astra's game-bridge port (WGPU_BRIDGE_PORT)."),
                Token = c.Bind("Connection", "Token", "", "Astra's bridge token, if you set WGPU_BRIDGE_TOKEN. Empty = none."),
                RingPath = c.Bind("Connection", "RingPath", "",
                    "Map the frame ring from here instead of where Astra says (a path, or a Windows mapping name). Empty = automatic."),

                Scale = c.Bind("Astra", "Scale", 1f, "Her size in this game's world (1 = her own height in metres)."),
                PlayerObject = c.Bind("Follow", "PlayerObject", "",
                    "Name of the GameObject she follows. Empty = automatic (this game's integration, else the nearest object tagged Player, else the camera)."),
                KeepDistance = c.Bind("Follow", "KeepDistance", 1.6f, "How close to you she comes (game units)."),
                FollowStart = c.Bind("Follow", "FollowStart", 3.5f, "How far you may walk off before she follows."),
                TeleportDistance = c.Bind("Follow", "TeleportDistance", 30f, "Beyond this (or stuck for 2 s) she appears near you instead of running."),
                MaxSpeed = c.Bind("Follow", "MaxSpeed", 7f, "Her top speed (game units per second)."),
                GroundLayers = c.Bind("Follow", "GroundLayers", "",
                    "Comma-separated layer names she walks on and bumps into. Empty = this game's integration, else every layer that raycasts."),

                DepthBias = c.Bind("Picture", "DepthBias", 0.03f,
                    "Metres she may be behind a surface and still show (hides z-fighting where she touches the ground)."),
                DepthSoftness = c.Bind("Picture", "DepthSoftness", 0.02f, "Metres over which she fades behind a surface."),
                ColourSpace = c.Bind("Picture", "ColourSpace", "auto",
                    "auto / linear / gamma: how her colours are written into the game's picture. Try the other if she looks too dark or washed out."),
                MaxPictureHeight = c.Bind("Picture", "MaxPictureHeight", 1080, "Her picture's height at most (the game's aspect is kept). Lower = cheaper."),
                SendLight = c.Bind("Picture", "SendLight", true, "Light her with this game's sun and sky."),
            };
        }
    }
}
