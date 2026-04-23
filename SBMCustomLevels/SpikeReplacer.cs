using HarmonyLib;
using UnityEngine;
using System;
using SBM_CustomLevels.Editor;

namespace SBM_CustomLevels
{
    /// <summary>
    /// Marker component attached to each black-snowball visual that replaces a spike in the
    /// original game levels.  The underlying spike collider + kill logic is still active; this
    /// component exists mainly for tracking and ensures the transformation is idempotent.
    /// </summary>
    internal class BlackSnowballHazard : MonoBehaviour { }

    /// <summary>
    /// Harmony patches that:
    /// <list type="number">
    ///   <item>Replace spike hazard visuals with black snowball visuals in original game levels.</item>
    ///   <item>Suppress blood-effect particle systems in original game levels (family mode).</item>
    /// </list>
    /// </summary>
    [HarmonyPatch]
    internal static class SpikeReplacer
    {
        // Shared black material reused for every snowball replacement to avoid per-spike allocation.
        private static Material _sharedBlackMaterial;

        private static Material GetBlackMaterial(Material sourceMaterial = null)
        {
            if (_sharedBlackMaterial == null)
            {
                _sharedBlackMaterial = sourceMaterial != null
                    ? new Material(sourceMaterial) { color = Color.black }
                    : new Material(Shader.Find("Standard")) { color = Color.black };
            }
            return _sharedBlackMaterial;
        }

        // ── Trigger: original story-mode level starts ──────────────────────────────────

        /// <summary>
        /// Runs after <see cref="SBM.Objects.GameModes.Story.GameManagerStory.Start"/>.
        /// The existing <c>FixStoryStartPatch</c> prefix skips the original Start for custom
        /// levels; HarmonyLib still invokes this postfix in all cases, so we guard explicitly.
        /// </summary>
        [HarmonyPatch(typeof(SBM.Objects.GameModes.Story.GameManagerStory), "Start")]
        [HarmonyPostfix]
        private static void OnStoryLevelStart()
        {
            // Only run for original game levels – not custom JSON levels and not the editor.
            if (LevelManager.InLevel || EditorManager.InEditor)
                return;

            ReplaceSpikesInActiveScene();
        }

        // ── Core replacement logic ─────────────────────────────────────────────────────

        /// <summary>
        /// Finds every active spike hazard in the current scene and swaps its visual for a
        /// black snowball.  The original spike collider and kill logic are kept active so that
        /// no private game-engine kill API needs to be reproduced.
        /// <para>
        /// Safe to call multiple times: spikes that have already been processed carry a
        /// <see cref="BlackSnowballHazard"/> component on their child snowball, so a second
        /// pass skips them.
        /// </para>
        /// </summary>
        private static void ReplaceSpikesInActiveScene()
        {
            int replaced = 0;

            // Iterate every active Transform; this is the idiomatic Unity way to walk the
            // full scene graph without the deprecated FindObjectsOfType<GameObject>.
            foreach (var t in UnityEngine.Object.FindObjectsOfType<Transform>())
            {
                if (t == null) continue;

                string objName = t.gameObject.name;

                // The spike prefab is named exactly "Spikes" by the game's Resources system.
                // Unity may append " (Clone)" or a numeric suffix; match those as well.
                if (objName != "Spikes"
                    && !objName.StartsWith("Spikes ", StringComparison.Ordinal)
                    && !objName.StartsWith("Spikes(", StringComparison.Ordinal))
                    continue;

                // Spikes[] children of FlipBlock objects are part of the flip mechanic and
                // must not be treated as standalone spike hazards.
                if (t.GetComponentInParent<SBM.Objects.World5.FlipBlock>() != null)
                    continue;

                // Idempotency: if this spike already has a BlackSnowballHazard child it was
                // processed in a previous call; skip it.
                if (t.GetComponentInChildren<BlackSnowballHazard>(true) != null)
                    continue;

                // Hide every mesh renderer on the spike so the spike visually disappears.
                HideSpikeRenderers(t.gameObject);

                // Attach a black snowball visual as a child of the spike transform so it
                // inherits position/rotation/scale automatically.
                AddSnowballVisual(t);

                replaced++;
            }

            if (replaced > 0)
                Debug.Log($"[SBM-FamilyMode] Replaced {replaced} spike hazard(s) with black snowball(s).");
        }

        /// <summary>Disables every renderer component on <paramref name="spike"/> and its descendants.</summary>
        private static void HideSpikeRenderers(GameObject spike)
        {
            foreach (var rend in spike.GetComponentsInChildren<Renderer>(true))
                rend.enabled = false;
        }

        /// <summary>
        /// Creates a black snowball visual as a child of the given spike transform.
        /// Tries to load the game's own Snowball prefab (painted black) so the shape fits the
        /// art style; falls back to a primitive sphere when the prefab is unavailable.
        /// </summary>
        private static void AddSnowballVisual(Transform spikeTransform)
        {
            GameObject snowball = null;

            // Attempt to use the game's Snowball asset for a visually consistent shape.
            var prefab = Resources.Load<GameObject>("prefabs/level/world2/Snowball");
            if (prefab != null)
            {
                snowball = UnityEngine.Object.Instantiate(prefab);
                snowball.name = "BlackSnowball_Visual";

                // Remove any colliders from the visual child so only the original spike's
                // collider handles physics/trigger interactions.
                foreach (var col in snowball.GetComponentsInChildren<Collider>(true))
                    UnityEngine.Object.Destroy(col);

                // Paint everything black.
                foreach (var rend in snowball.GetComponentsInChildren<MeshRenderer>(true))
                {
                    rend.material = GetBlackMaterial(rend.sharedMaterial);
                    rend.enabled = true;
                }
            }

            if (snowball == null)
            {
                // Fallback: black primitive sphere – no collider needed.
                snowball = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                snowball.name = "BlackSnowball_Visual";
                UnityEngine.Object.Destroy(snowball.GetComponent<Collider>());

                var rend = snowball.GetComponent<MeshRenderer>();
                if (rend != null)
                    rend.material = GetBlackMaterial();
            }

            // Parent under the spike so the visual follows the spike's transform exactly.
            snowball.transform.SetParent(spikeTransform, false);
            snowball.transform.localPosition = Vector3.zero;
            snowball.transform.localRotation = Quaternion.identity;
            snowball.transform.localScale = Vector3.one;

            // Attach the marker component; used for idempotency checks on the next scene pass.
            snowball.AddComponent<BlackSnowballHazard>();
        }

        // ── Blood suppression ──────────────────────────────────────────────────────────

        /// <summary>
        /// Returns <c>true</c> when the given particle system name matches a blood or gore effect.
        /// Comparison is done with <see cref="StringComparison.OrdinalIgnoreCase"/> to avoid
        /// per-call string allocation from <c>ToLowerInvariant()</c>.
        /// </summary>
        private static bool IsBloodEffect(string particleSystemName) =>
            particleSystemName.IndexOf("blood", StringComparison.OrdinalIgnoreCase) >= 0
            || particleSystemName.IndexOf("splat", StringComparison.OrdinalIgnoreCase) >= 0
            || particleSystemName.IndexOf("gore", StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>
        /// Intercepts <see cref="ParticleSystem.Play()"/> in original game levels and skips
        /// any particle system whose name suggests it is a blood or gore effect.  This is a
        /// "family mode" suppression: all blood effects are hidden in original levels so that
        /// deaths from replaced spike hazards (and any other hazard) remain blood-free.
        /// </summary>
        [HarmonyPatch(typeof(ParticleSystem), "Play", new Type[] { })]
        [HarmonyPrefix]
        private static bool SuppressBloodParticles(ParticleSystem __instance)
        {
            // Only suppress during original game levels.
            if (LevelManager.InLevel || EditorManager.InEditor)
                return true;

            return !IsBloodEffect(__instance.gameObject.name);
        }

        /// <summary>
        /// Intercepts <see cref="ParticleSystem.Play(bool)"/> (the overload with
        /// <c>withChildren</c>) for the same blood-suppression purpose.
        /// </summary>
        [HarmonyPatch(typeof(ParticleSystem), "Play", new Type[] { typeof(bool) })]
        [HarmonyPrefix]
        private static bool SuppressBloodParticlesWithChildren(ParticleSystem __instance)
        {
            if (LevelManager.InLevel || EditorManager.InEditor)
                return true;

            return !IsBloodEffect(__instance.gameObject.name);
        }
    }
}
