using System;
using System.Collections.Generic;
using Jotunn.Configs;
using Jotunn.Entities;
using Jotunn.Managers;
using UnityEngine;
using WardsPlus.Components;
using WardsPlus.Config;
using Object = UnityEngine.Object;

namespace WardsPlus.Pieces
{
    /// <summary>
    /// Builds the four ward pieces by cloning the vanilla Ward (guard_stone), stripping its protection
    /// (PrivateArea), tinting its magic lines and glow, and registering them on a "WardsPlus" hammer tab.
    /// </summary>
    internal static class WardPieces
    {
        public const string CategoryName = "WardsPlus";

        private const string BasePrefab = "guard_stone";
        /// <summary>Vanilla chest whose inventory background sprite the ward containers borrow.</summary>
        private const string BackgroundSourcePrefab = "piece_chest_wood";

        private sealed class WardDefinition
        {
            public WardType Type;
            public string PrefabName;
            public string Token;
            public Type Component;
            public int ContainerWidth;
            public int ContainerHeight;
            /// <summary>
            /// Make it a "distant" network object, synced and loaded two zones further out than normal pieces,
            /// so clients deciding spawns near it always know about it.
            /// </summary>
            public bool Distant;
        }

        private static readonly WardDefinition[] Definitions =
        {
            new WardDefinition { Type = WardType.Repair, PrefabName = "WP_Ward_Repair", Token = "wp_ward_repair",
                Component = typeof(RepairWard), ContainerWidth = 4, ContainerHeight = 2 },
            new WardDefinition { Type = WardType.Refuel, PrefabName = "WP_Ward_Refuel", Token = "wp_ward_refuel",
                Component = typeof(RefuelWard), ContainerWidth = 4, ContainerHeight = 2 },
            new WardDefinition { Type = WardType.Spawnproof, PrefabName = "WP_Ward_Spawnproof", Token = "wp_ward_spawnproof",
                Component = typeof(SpawnproofWard), Distant = true },
            new WardDefinition { Type = WardType.Defender, PrefabName = "WP_Ward_Defender", Token = "wp_ward_defender",
                Component = typeof(DefenderWard), ContainerWidth = 1, ContainerHeight = 1 },
        };

        public static void Init()
        {
            PrefabManager.OnVanillaPrefabsAvailable += RegisterAll;
        }

        private static void RegisterAll()
        {
            // Prefabs only need to be built once; Jotunn keeps them across logouts.
            PrefabManager.OnVanillaPrefabsAvailable -= RegisterAll;

            Sprite background = PrefabManager.Cache.GetPrefab<GameObject>(BackgroundSourcePrefab)
                ?.GetComponent<Container>()?.m_bkg;

            foreach (WardDefinition def in Definitions)
            {
                try
                {
                    Register(def, background);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"Failed to create {def.PrefabName}: {e}");
                }
            }
        }

        private static void Register(WardDefinition def, Sprite background)
        {
            WardConfig cfg = PluginConfig.For(def.Type);
            if (!cfg.Enabled.Value)
            {
                Plugin.Log.LogInfo($"{def.PrefabName} is disabled in the config.");
                return;
            }

            string station = cfg.CraftingStation.Value?.Trim();
            var pieceConfig = new PieceConfig
            {
                Name = "$" + def.Token,
                Description = "$" + def.Token + "_desc",
                PieceTable = PieceTables.Hammer,
                Category = CategoryName,
                CraftingStation = string.IsNullOrEmpty(station) ? null : station,
                Requirements = cfg.ParseRecipe(),
            };

            var customPiece = new CustomPiece(def.PrefabName, BasePrefab, pieceConfig);
            GameObject prefab = customPiece.PiecePrefab;
            if (prefab == null)
            {
                Plugin.Log.LogError($"Could not clone '{BasePrefab}' for {def.PrefabName}.");
                return;
            }

            if (!ConvertToWard(prefab, def, pieceConfig.Name, cfg.Color.Value, background))
                return;

            // Icon is rendered from the tinted prefab, so each ward's hammer icon shows its own color.
            Sprite icon = RenderManager.Instance.Render(prefab, RenderManager.IsometricRotation);
            if (icon)
                customPiece.Piece.m_icon = icon;
            else
                Plugin.Log.LogWarning($"Could not render an icon for {def.PrefabName}; using the vanilla ward icon.");

            PieceManager.Instance.AddPiece(customPiece);
        }

        /// <summary>Swaps PrivateArea for the ward component while keeping the model, glow and range ring.</summary>
        private static bool ConvertToWard(GameObject prefab, WardDefinition def, string nameToken, Color color, Sprite background)
        {
            var privateArea = prefab.GetComponent<PrivateArea>();
            if (!privateArea)
            {
                Plugin.Log.LogError($"'{BasePrefab}' has no PrivateArea; cannot build {def.PrefabName}.");
                return false;
            }

            var ward = (WardBase)prefab.AddComponent(def.Component);
            ward.m_name = nameToken;
            ward.m_model = privateArea.m_model;
            ward.m_enabledEffect = privateArea.m_enabledEffect;
            ward.m_areaMarker = privateArea.m_areaMarker;
            ward.m_hoverOffset = privateArea.m_hoverOffset;
            if (privateArea.m_areaMarker && privateArea.m_radius > 0f)
            {
                ward.m_markerSegmentsPerMeter =
                    privateArea.m_areaMarker.m_nrOfSegments / (2f * Mathf.PI * privateArea.m_radius);
            }

            // The "in range" pulse belongs to the protection bubble, which these wards don't have.
            DestroyIfChild(prefab, privateArea.m_inRangeEffect);
            Object.DestroyImmediate(privateArea);

            ApplyColor(prefab, ward, color);

            if (def.ContainerWidth > 0 && def.ContainerHeight > 0)
                ward.m_container = AddContainer(prefab, def, nameToken, background);

            if (def.Distant)
            {
                ZNetView view = prefab.GetComponent<ZNetView>();
                if (view)
                    view.m_distant = true;
            }

            return true;
        }

        private static void DestroyIfChild(GameObject prefab, GameObject child)
        {
            if (child && child.transform.IsChildOf(prefab.transform) && child != prefab)
                Object.DestroyImmediate(child);
        }

        /// <summary>
        /// Recolors the magic lines (emission of the stone's materials) and the enabled glow (lights and
        /// particles). Only m_model's materials are tinted, so the rest of the mesh does not start glowing.
        /// </summary>
        private static void ApplyColor(GameObject prefab, WardBase ward, Color color)
        {
            if (ward.m_model)
            {
                var tinted = new Dictionary<Material, Material>();
                foreach (Material source in ward.m_model.sharedMaterials)
                {
                    if (!source || tinted.ContainsKey(source) || !source.HasProperty("_EmissionColor"))
                        continue;

                    // Only recolor materials that actually glow in vanilla. A black emission color means
                    // "no glow"; giving it our color would light up the whole stone.
                    float intensity = source.GetColor("_EmissionColor").maxColorComponent;
                    if (intensity < 0.01f)
                        continue;

                    var copy = new Material(source) { name = $"{source.name}_{prefab.name}" };
                    // Keep vanilla's HDR brightness, only change the hue.
                    copy.SetColor("_EmissionColor", color * intensity);
                    // On for the placement ghost and icon; WardBase toggles it at runtime from the Enabled setting.
                    copy.EnableKeyword("_EMISSION");
                    tinted[source] = copy;
                }

                // Swap the same materials on every renderer that uses them (e.g. LODs of the stone).
                foreach (Renderer renderer in prefab.GetComponentsInChildren<Renderer>(true))
                {
                    Material[] materials = renderer.sharedMaterials;
                    bool changed = false;
                    for (int i = 0; i < materials.Length; i++)
                    {
                        if (materials[i] && tinted.TryGetValue(materials[i], out Material copy))
                        {
                            materials[i] = copy;
                            changed = true;
                        }
                    }
                    if (changed)
                        renderer.sharedMaterials = materials;
                }
            }

            if (ward.m_enabledEffect)
            {
                foreach (Light light in ward.m_enabledEffect.GetComponentsInChildren<Light>(true))
                    light.color = color;

                foreach (ParticleSystem particles in ward.m_enabledEffect.GetComponentsInChildren<ParticleSystem>(true))
                {
                    ParticleSystem.MainModule main = particles.main;
                    float alpha = main.startColor.mode == ParticleSystemGradientMode.Color ? main.startColor.color.a : 1f;
                    main.startColor = new ParticleSystem.MinMaxGradient(new Color(color.r, color.g, color.b, alpha));
                }

                if (!PluginConfig.ShowGlowOrb.Value)
                    HideGlowOrb(ward.m_enabledEffect);
            }
        }

        /// <summary>
        /// Children of the vanilla ward's "WayEffect" that draw the spherical glow orb. The other children
        /// (Point light, sparcs, pulse, sfx) are kept, and so are the magic lines on the stone's material.
        /// </summary>
        private static readonly HashSet<string> GlowOrbParts = new HashSet<string> { "flare", "glow" };

        private static void HideGlowOrb(GameObject enabledEffect)
        {
            foreach (Transform child in enabledEffect.GetComponentsInChildren<Transform>(true))
            {
                if (child && GlowOrbParts.Contains(child.name))
                    Object.DestroyImmediate(child.gameObject);
            }
        }

        /// <summary>
        /// Adds the ward's inventory on a collider-less child, so WardBase on the root stays the only
        /// Interactable the player hits. m_rootObjectOverride makes it save into the ward's own ZDO.
        /// </summary>
        private static Container AddContainer(GameObject prefab, WardDefinition def, string nameToken, Sprite background)
        {
            var holder = new GameObject("WP_Inventory");
            holder.transform.SetParent(prefab.transform, false);

            var container = holder.AddComponent<Container>();
            container.m_name = nameToken;
            container.m_width = def.ContainerWidth;
            container.m_height = def.ContainerHeight;
            container.m_bkg = background;
            container.m_privacy = Container.PrivacySetting.Public;
            container.m_checkGuardStone = true;
            container.m_rootObjectOverride = prefab.GetComponent<ZNetView>();
            return container;
        }
    }
}
