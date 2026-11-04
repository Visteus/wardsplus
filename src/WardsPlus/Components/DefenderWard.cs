using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UI;
using WardsPlus.Config;
using WardsPlus.UI;
using WardsPlus.Util;

namespace WardsPlus.Components
{
    /// <summary>
    /// Holds a trophy in its 1×1 slot and, when enemies come into range, summons one tamed copy of that
    /// trophy's creature to fight them.
    /// - Which creatures a trophy can summon comes from <see cref="TrophyMap"/> (vanilla and modded). When
    ///   several drop the same trophy (e.g. the Dvergr types), the panel lets players pick one per ward.
    /// - One summon at a time, at most one per Summon Cooldown. The ward stores the summon's id in its ZDO;
    ///   the summon (<see cref="DefenderSummon"/>) leaves once it has been idle for a while, or as soon as the
    ///   ward is disabled, loses its trophy or switches creature (the ward clears the id).
    /// - The trophy is not used up. Only trophies that resolve to a creature fit in the slot.
    /// - Enemies are counted by horizontal distance, matching the ring.
    /// </summary>
    public class DefenderWard : WardBase
    {
        public override WardType Type => WardType.Defender;

        protected override float IdleInterval => PluginConfig.DefenderScanInterval.Value;

        private const float TrophyChangedTickDelay = 0.5f;
        private const float SpawnDistance = 2f;
        private const int PanelListMaxRows = 5;

        /// <summary>Inventories of loaded Defender Wards; the inventory patches only let usable trophies in.</summary>
        private static readonly HashSet<Inventory> s_inventories = new HashSet<Inventory>();

        // Panel state while this ward's panel is open (controls are destroyed when it closes).
        private Text m_panelStatus;
        private ScrollGroup m_choiceGroup;
        private readonly Dictionary<string, Toggle> m_choiceToggles = new Dictionary<string, Toggle>();
        private string m_panelSignature;

        public static bool IsDefenderInventory(Inventory inventory) => inventory != null && s_inventories.Contains(inventory);

        // ---------------------------------------------------------------- lifecycle

        protected override void Awake()
        {
            base.Awake();
            if (!IsValid)
                return;
            m_nview.Register<string>(WardRpcs.SetCreature, RPC_SetCreature);
            m_nview.Register<Vector3>(WardRpcs.SummonFx, RPC_SummonFx);
        }

        private void Start()
        {
            // The container lives on a child whose Awake may run after ours, so hook it up here.
            Inventory inventory = GetInventory();
            if (inventory == null)
                return;
            s_inventories.Add(inventory);
            inventory.m_onChanged += OnInventoryChanged;
        }

        protected override void OnDestroy()
        {
            Inventory inventory = GetInventory();
            if (inventory != null)
                s_inventories.Remove(inventory);
            base.OnDestroy();
        }

        private Inventory GetInventory() => m_container ? m_container.GetInventory() : null;

        /// <summary>Trophy added, removed or swapped: react soon (summon, or send the old summon away).</summary>
        private void OnInventoryChanged()
        {
            if (IsValid && m_nview.IsOwner() && IsEnabled())
                RequestTickSoon(TrophyChangedTickDelay);
        }

        // ---------------------------------------------------------------- trophy + creature choice

        /// <summary>The trophy in the slot, if it can summon anything.</summary>
        private ItemDrop.ItemData GetTrophy()
        {
            Inventory inventory = GetInventory();
            if (inventory == null)
                return null;
            foreach (ItemDrop.ItemData item in inventory.GetAllItems())
            {
                if (TrophyMap.IsUsable(item))
                    return item;
            }
            return null;
        }

        /// <summary>The creature this ward summons: the stored choice if the trophy allows it, else the best fit.</summary>
        private TrophyMap.Creature GetCreature(ItemDrop.ItemData trophy, out List<TrophyMap.Creature> options)
        {
            options = TrophyMap.GetOptions(trophy);
            if (options.Count == 0)
                return null;
            string chosen = IsValid ? m_nview.GetZDO().GetString(ZdoKeys.Creature) : "";
            foreach (TrophyMap.Creature creature in options)
            {
                if (creature.Prefab == chosen)
                    return creature;
            }
            return options[0];
        }

        /// <summary>Asks the owner to summon a different creature for this trophy. Safe to call from any client.</summary>
        public void RequestSetCreature(string prefab)
        {
            if (IsValid)
                m_nview.InvokeRPC(WardRpcs.SetCreature, prefab ?? "");
        }

        private void RPC_SetCreature(long sender, string prefab)
        {
            if (!m_nview.IsOwner())
                return;
            m_nview.GetZDO().Set(ZdoKeys.Creature, prefab ?? "");
            DebugLog.Write($"{LogName}: creature set to {(string.IsNullOrEmpty(prefab) ? "best fit" : prefab)} (by peer {sender}).");
            RequestTickSoon(TrophyChangedTickDelay);
        }

        // ---------------------------------------------------------------- simulation (owner only)

        protected override float OnTick()
        {
            float delay = NextIdleDelay(IdleInterval);
            ZDO zdo = m_nview.GetZDO();

            ItemDrop.ItemData trophy = GetTrophy();
            TrophyMap.Creature creature = trophy != null ? GetCreature(trophy, out _) : null;
            GameObject prefab = creature != null ? ZNetScene.instance.GetPrefab(creature.Prefab) : null;

            // Keep the current summon only while it's alive and still the creature this ward wants.
            ZDOID summonId = zdo.GetZDOID(ZdoKeys.Summon);
            if (!summonId.IsNone())
            {
                ZDO summon = ZDOMan.instance.GetZDO(summonId);
                bool keep = summon != null && prefab && summon.GetPrefab() == prefab.name.GetStableHashCode();
                if (keep)
                    return delay;
                DebugLog.Write($"{LogName}: dropping its summon {summonId} ({(summon == null ? "gone" : "no longer the chosen creature")}).");
                zdo.Set(ZdoKeys.Summon, ZDOID.None); // The old summon sees this and leaves.
            }

            if (!prefab || !CooldownOver(zdo))
                return delay;

            Character enemy = FindEnemy();
            if (enemy)
            {
                if (DebugLog.Enabled)
                    DebugLog.Write($"{LogName}: {Utils.GetPrefabName(enemy.gameObject)} {DebugLog.Distance(enemy.transform.position, transform.position)} away, summoning {prefab.name} (level {Mathf.Clamp(PluginConfig.DefenderSummonLevel.Value, 1, 3)}).");
                Summon(prefab, enemy);
            }
            return delay;
        }

        private static bool CooldownOver(ZDO zdo)
        {
            long last = zdo.GetLong(ZdoKeys.SummonTime, 0L);
            if (last == 0L)
                return true;
            double elapsed = (ZNet.instance.GetTime() - new System.DateTime(last)).TotalSeconds;
            // A negative gap means the clock was moved back (e.g. a restored save); don't lock the ward out.
            return elapsed < 0 || elapsed >= PluginConfig.DefenderSummonCooldown.Value;
        }

        /// <summary>The nearest enemy of players in range, or null.</summary>
        private Character FindEnemy()
        {
            Vector3 center = transform.position;
            float range = GetRange();
            float bestSqr = range * range;
            Character best = null;
            foreach (Character character in Character.GetAllCharacters())
            {
                if (!IsThreat(character))
                    continue;
                Vector3 offset = character.transform.position - center;
                offset.y = 0f;
                float sqr = offset.sqrMagnitude;
                if (sqr > bestSqr)
                    continue;
                best = character;
                bestSqr = sqr;
            }
            return best;
        }

        /// <summary>
        /// A wild monster that would attack players: not a player, tamed creature or summon; not a passive
        /// animal (deer, boars...); Dvergr only once angered. Same rules as vanilla's enemy check.
        /// </summary>
        private static bool IsThreat(Character character)
        {
            if (!character || character.IsPlayer() || character.IsDead() || character.IsTamed())
                return false;
            BaseAI ai = character.GetBaseAI();
            if (!(ai is MonsterAI))
                return false;
            switch (character.GetFaction())
            {
                case Character.Faction.Players:
                case Character.Faction.AnimalsVeg:
                case Character.Faction.PlayerSpawned:
                case Character.Faction.TrainingDummy:
                    return false;
                case Character.Faction.Dverger:
                    return ai.IsAggravated();
                default:
                    return true;
            }
        }

        private void Summon(GameObject prefab, Character enemy)
        {
            Vector3 position = FindSpawnPoint();
            GameObject go = Instantiate(prefab, position, Quaternion.Euler(0f, Random.Range(0f, 360f), 0f));
            var character = go.GetComponent<Character>();
            ZNetView view = go.GetComponent<ZNetView>();
            if (!character || !view || !view.IsValid())
            {
                Plugin.Log.LogWarning($"[Defender Ward] Could not summon '{prefab.name}'.");
                if (view && view.IsValid())
                    ZNetScene.instance.Destroy(go);
                return;
            }

            // Mark it as a summon first, so DefenderSummon (and the drop/breeding patches) see it from the start.
            ZDO summonZdo = view.GetZDO();
            summonZdo.Set(ZdoKeys.SummonOf, m_nview.GetZDO().m_uid);
            DefenderSummon.AttachIfSummon(character);

            character.SetTamed(true);
            int level = Mathf.Clamp(PluginConfig.DefenderSummonLevel.Value, 1, 3);
            if (level > 1)
                character.SetLevel(level);

            if (go.GetComponent<MonsterAI>() is MonsterAI ai)
            {
                ai.SetAlerted(true);
                ai.SetTarget(enemy);
            }

            ZDO zdo = m_nview.GetZDO();
            zdo.Set(ZdoKeys.Summon, summonZdo.m_uid);
            zdo.Set(ZdoKeys.SummonTime, ZNet.instance.GetTime().Ticks);
            m_nview.InvokeRPC(ZNetView.Everybody, WardRpcs.SummonFx, position);
        }

        // ---------------------------------------------------------------- summon effect

        /// <summary>Vanilla prefab whose spawn effect (small cloud and "poof") summons reuse.</summary>
        private const string SummonFxSource = "Spawner_GreydwarfNest";

        private static EffectList s_summonFx;
        private static ZNetScene s_summonFxScene;

        /// <summary>The Greydwarf nest's spawn effect, read from the loaded prefab. Null if it can't be found.</summary>
        private static EffectList SummonEffect
        {
            get
            {
                ZNetScene scene = ZNetScene.instance;
                if (scene != s_summonFxScene)
                {
                    s_summonFxScene = scene;
                    GameObject nest = scene ? scene.GetPrefab(SummonFxSource) : null;
                    var spawner = nest ? nest.GetComponent<SpawnArea>() : null;
                    s_summonFx = spawner ? spawner.m_spawnEffects : null;
                    if (scene && s_summonFx == null)
                        Plugin.Log.LogWarning($"[Defender Ward] '{SummonFxSource}' not found; summons will appear without an effect.");
                }
                return s_summonFx;
            }
        }

        private void RPC_SummonFx(long sender, Vector3 position) =>
            SummonEffect?.Create(position, Quaternion.identity);

        /// <summary>
        /// Plays the summon effect at <paramref name="position"/> for every nearby client, through the ward's
        /// network object (the creature itself is about to be destroyed). Local only if the ward isn't loaded.
        /// </summary>
        public static void PlaySummonEffect(ZDOID wardId, Vector3 position)
        {
            GameObject go = ZNetScene.instance ? ZNetScene.instance.FindInstance(wardId) : null;
            var ward = go ? go.GetComponent<DefenderWard>() : null;
            if (ward && ward.IsValid)
                ward.m_nview.InvokeRPC(ZNetView.Everybody, WardRpcs.SummonFx, position);
            else
                SummonEffect?.Create(position, Quaternion.identity);
        }

        /// <summary>A spot a couple of metres from the ward, on whatever floor or ground is just below it.</summary>
        private Vector3 FindSpawnPoint()
        {
            Vector2 dir = Random.insideUnitCircle.normalized;
            if (dir == Vector2.zero)
                dir = Vector2.up;
            Vector3 point = transform.position + new Vector3(dir.x, 0f, dir.y) * SpawnDistance;
            // Small margin: start the ray just above the ward, so a roof over it isn't picked.
            if (ZoneSystem.instance.GetSolidHeight(point, out float height, 2) && Mathf.Abs(height - transform.position.y) < 3f)
                point.y = height;
            else
                point = transform.position;
            return point + Vector3.up * 0.2f;
        }

        // ---------------------------------------------------------------- hover + panel

        protected override void AppendHoverStatus(StringBuilder sb)
        {
            ItemDrop.ItemData trophy = GetTrophy();
            TrophyMap.Creature creature = trophy != null ? GetCreature(trophy, out _) : null;
            sb.Append(creature != null ? $"\n$wp_defender_summons: {creature.Label}" : "\n$wp_defender_no_trophy");
        }

        public override void PopulatePanel(RectTransform extras)
        {
            m_panelSignature = null;
            m_choiceToggles.Clear();
            m_panelStatus = WardPanel.AddNote(extras, "");
            m_choiceGroup = WardPanel.AddScrollGroup(extras, PanelListMaxRows);
            RefreshPanel();
        }

        /// <summary>Rebuilds the creature choice only when the trophy, the choice or the options change.</summary>
        public override void RefreshPanel()
        {
            if (!m_panelStatus || !m_choiceGroup)
                return;

            ItemDrop.ItemData trophy = GetTrophy();
            List<TrophyMap.Creature> options = null;
            TrophyMap.Creature selected = trophy != null ? GetCreature(trophy, out options) : null;

            string signature = trophy == null ? "" : $"{trophy.m_shared.m_name}|{selected?.Prefab}|{options.Count}";
            if (signature == m_panelSignature)
                return;
            m_panelSignature = signature;

            Localization loc = Localization.instance;
            m_panelStatus.text = selected != null
                ? loc.Localize($"$wp_defender_summons: {selected.Label}")
                : loc.Localize("$wp_defender_insert_trophy");

            WardPanel.ClearChildren(m_choiceGroup.Content);
            m_choiceToggles.Clear();
            bool showChoice = options != null && options.Count > 1;
            m_choiceGroup.gameObject.SetActive(showChoice);
            if (!showChoice)
                return;

            foreach (TrophyMap.Creature option in options)
            {
                string prefab = option.Prefab;
                m_choiceToggles[prefab] = WardPanel.AddToggleRow(m_choiceGroup.Content, loc.Localize(option.Label),
                    option == selected, isOn => OnChoiceClicked(prefab, isOn));
            }
        }

        /// <summary>Radio-button behaviour: picking one unticks the rest; the picked one can't be unticked.</summary>
        private void OnChoiceClicked(string prefab, bool isOn)
        {
            if (!isOn)
            {
                if (m_choiceToggles.TryGetValue(prefab, out Toggle self) && self)
                    self.SetIsOnWithoutNotify(true);
                return;
            }
            foreach (KeyValuePair<string, Toggle> pair in m_choiceToggles)
            {
                if (pair.Key != prefab && pair.Value)
                    pair.Value.SetIsOnWithoutNotify(false);
            }
            RequestSetCreature(prefab);
        }
    }
}
