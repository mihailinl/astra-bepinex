// SPDX-License-Identifier: MIT
// A readability shard of Compat: shadow/dimmer reflection and the physics-query family.
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
#if IL2CPP
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
#endif

namespace Astra.Unity
{
    static partial class Compat
    {
        static class PhysicsQuery
        {
#if IL2CPP
            public static readonly Il2CppStructArray<RaycastHit> Hits = new Il2CppStructArray<RaycastHit>(32);
#else
            public static readonly RaycastHit[] Hits = new RaycastHit[32];
#endif
        }

        static readonly Dictionary<int, bool> castsShadow = new Dictionary<int, bool>();
        // When a NEGATIVE verdict was judged (unscaled time); a POSITIVE one is kept forever and
        // never appears here.
        static readonly Dictionary<int, float> castsShadowNegativeAt = new Dictionary<int, float>();

        /// <summary>
        /// Does something that casts a SHADOW lie along the ray? Only a collider that belongs to a
        /// renderer the game draws shadows from — on its object, under it, on an ancestor up to 3
        /// levels up, or on that ancestor's OTHER direct children (the collider's siblings, and its
        /// parent's siblings, and so on — never a whole subtree past this object's own, and never
        /// past a scene root) — counts, and terrain: a game's invisible colliders — map bounds,
        /// zones, blockers — stop no light. (Lethal Company is full of them: her sun read "hidden"
        /// in open daylight.) A verdict is cached; a NEGATIVE one expires after 5 s (a renderer can
        /// start casting shadows after the game first judges it), a positive one never does.
        /// </summary>
        public static bool Shadowed(Vector3 origin, Vector3 dir, float distance, HashSet<int> ignore)
        {
            int n = Physics.RaycastNonAlloc(origin, dir, PhysicsQuery.Hits, distance, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
            {
                var h = PhysicsQuery.Hits[i];
                var c = h.collider;
                if (c == null || h.distance <= 0) continue;
                int id = c.GetInstanceID();
                if (ignore.Contains(id)) continue;
                if (!castsShadow.TryGetValue(id, out bool casts) ||
                    (!casts && Time.unscaledTime - castsShadowNegativeAt[id] > 5f))
                {
                    if (castsShadow.Count > 4096) { castsShadow.Clear(); castsShadowNegativeAt.Clear(); }
                    castsShadow[id] = casts = CastsShadow(c);
                    if (!casts) castsShadowNegativeAt[id] = Time.unscaledTime;
                }
                if (casts) return true;
            }
            return false;
        }

        /// <summary>Forgets every shadow-caster verdict, positive and negative: a new session may
        /// find different casters where the same collider ids sat before.</summary>
        public static void ClearShadowCache()
        {
            castsShadow.Clear();
            castsShadowNegativeAt.Clear();
        }

        static bool CastsShadow(Collider c)
        {
            if (As<TerrainCollider>(c) != null) return true;
            var go = c.gameObject;
            if (Casts(Get<Renderer>(go))) return true;
#if IL2CPP
            if (Exists<Renderer>())
                foreach (var o in go.GetComponentsInChildren(Il2CppType.Of<Renderer>(), false))
                    if (Casts(o.TryCast<Renderer>())) return true;
#else
            foreach (var r in go.GetComponentsInChildren<Renderer>(false))
                if (Casts(r)) return true;
#endif
            // Up to 3 ancestors (parent, grandparent, great-grandparent): each one's own renderer,
            // and each one's OTHER direct children (this object's siblings at that level) — never
            // past a scene root (an ancestor with no parent of its own ends the walk).
            var at = c.transform.parent;
            for (int level = 0; level < 3 && at != null; level++)
            {
                if (Casts(Get<Renderer>(at.gameObject))) return true;
                for (int i = 0; i < at.childCount; i++)
                {
                    var sibling = at.GetChild(i);
                    if (sibling != c.transform && Casts(Get<Renderer>(sibling.gameObject))) return true;
                }
                at = at.parent;
            }
            return false;
        }

        static bool Casts(Renderer r) => r != null && r.enabled && r.shadowCastingMode != UnityEngine.Rendering.ShadowCastingMode.Off;

#if !IL2CPP
        static readonly Dictionary<int, Component> dimmerComponent = new Dictionary<int, Component>();
        static readonly Dictionary<int, PropertyInfo> dimmerProperty = new Dictionary<int, PropertyInfo>();
#endif

        /// <summary>HDRP's own per-light multiplier (<c>HDAdditionalLightData.lightDimmer</c>), read
        /// by reflection so this assembly never references the HDRP package; 1 where there is none (no
        /// HDRP, or the component is absent). Mono only: under IL2CPP a component's native type is
        /// never reachable as this C# property by reflection, so it is always 1 there.</summary>
        public static float LightDimmer(Light l)
        {
#if IL2CPP
            return 1f;
#else
            if (l == null) return 1f;
            int id = l.GetInstanceID();
            if (!dimmerComponent.TryGetValue(id, out var comp))
            {
                foreach (var c in l.GetComponents<Component>())
                {
                    if (c == null || c.GetType().Name != "HDAdditionalLightData") continue;
                    comp = c;
                    break;
                }
                if (dimmerComponent.Count > 4096) { dimmerComponent.Clear(); dimmerProperty.Clear(); }
                dimmerComponent[id] = comp; // null cached too: no HDAdditionalLightData on this light
            }
            if (comp == null) return 1f;
            if (!dimmerProperty.TryGetValue(id, out var prop))
            {
                prop = comp.GetType().GetProperty("lightDimmer");
                dimmerProperty[id] = prop;
            }
            return prop != null && prop.PropertyType == typeof(float) ? (float)prop.GetValue(comp, null) : 1f;
#endif
        }

        /// <summary>The nearest hit along a ray, skipping colliders in <paramref name="ignore"/> and triggers.</summary>
        public static bool Raycast(Vector3 origin, Vector3 dir, float distance, int mask, HashSet<int> ignore, out RaycastHit nearest)
        {
            int n = Physics.RaycastNonAlloc(origin, dir, PhysicsQuery.Hits, distance, mask, QueryTriggerInteraction.Ignore);
            return Nearest(n, ignore, out nearest);
        }

        /// <summary>The nearest hit of a capsule swept along <paramref name="dir"/>, skipping <paramref name="ignore"/>.</summary>
        public static bool CapsuleCast(Vector3 p1, Vector3 p2, float radius, Vector3 dir, float distance, int mask, HashSet<int> ignore, out RaycastHit nearest)
        {
            int n = Physics.CapsuleCastNonAlloc(p1, p2, radius, dir, PhysicsQuery.Hits, distance, mask, QueryTriggerInteraction.Ignore);
            return Nearest(n, ignore, out nearest);
        }

        static bool Nearest(int n, HashSet<int> ignore, out RaycastHit nearest)
        {
            nearest = default;
            bool found = false;
            for (int i = 0; i < n; i++)
            {
                var h = PhysicsQuery.Hits[i];
                var c = h.collider;
                if (c == null || ignore.Contains(c.GetInstanceID())) continue;
                // A cast that starts inside a collider reports it at distance 0 with no point: that is
                // her standing in the geometry, not something ahead of her.
                if (h.distance <= 0f && h.point == Vector3.zero) continue;
                if (found && h.distance >= nearest.distance) continue;
                nearest = h;
                found = true;
            }
            return found;
        }
    }
}
