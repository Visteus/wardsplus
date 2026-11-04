using System;
using System.Collections.Generic;
using UnityEngine;
using WardsPlus.Config;

namespace WardsPlus.Components
{
    /// <summary>
    /// Which creatures each trophy can summon, for the Defender Ward. Built from the loaded prefabs, so
    /// modded creatures and trophies work without a hand-written table:
    /// - every ZNetScene prefab with a Character, a MonsterAI (it can fight) and a CharacterDrop is checked;
    /// - each drop whose item type is Trophy maps that trophy to the creature (by item type, not by name,
    ///   so irregular names like TrophySGolem → StoneGolem just work);
    /// - bosses, water-only creatures (serpents, leeches) and the config's Denied Creatures are left out;
    /// - the config's Trophy Overrides come first, then the creature whose name best matches the trophy.
    /// Built when a world finishes loading (Game.Start) and dropped on logout; rebuilt on demand if the
    /// Defender config lists change. Everything after that is a dictionary lookup.
    /// </summary>
    internal static class TrophyMap
    {
        /// <summary>One summonable creature.</summary>
        internal sealed class Creature
        {
            public string Prefab;
            /// <summary>Localization token of the creature's name (Character.m_name).</summary>
            public string NameToken;
            /// <summary>Whether the prefab starts asleep (MonsterAI.m_sleeping). Summons are woken anyway.</summary>
            public bool StartsAsleep;
            /// <summary>
            /// Set when another creature for the same trophy has the same name: what tells it apart, from its
            /// prefab name (GoblinArcher → "Archer"). Empty otherwise.
            /// </summary>
            public string Variant = "";

            /// <summary>Name for hover text and the panel, e.g. "$enemy_goblin (Archer)". Localize before showing.</summary>
            public string Label => Variant.Length == 0 ? NameToken : $"{NameToken} ({Variant})";

            public Creature Copy() => (Creature)MemberwiseClone();
        }

        /// <summary>Keyed by the trophy item's shared name token (what inventory items carry).</summary>
        private static readonly Dictionary<string, List<Creature>> s_byTrophy = new Dictionary<string, List<Creature>>();
        private static ZNetScene s_builtFor;
        private static string s_builtDenied;
        private static string s_builtOverrides;

        private static readonly List<Creature> s_none = new List<Creature>();

        /// <summary>Drops the map (logout); the next lookup rebuilds it for the new world.</summary>
        public static void Clear()
        {
            s_byTrophy.Clear();
            s_builtFor = null;
        }

        /// <summary>Builds the map now if it is missing or out of date.</summary>
        public static void EnsureBuilt()
        {
            ZNetScene scene = ZNetScene.instance;
            if (!scene || !ObjectDB.instance)
                return;
            string denied = PluginConfig.DefenderDeniedCreatures.Value ?? "";
            string overrides = PluginConfig.DefenderTrophyOverrides.Value ?? "";
            if (s_builtFor == scene && denied == s_builtDenied && overrides == s_builtOverrides)
                return;

            s_builtFor = scene;
            s_builtDenied = denied;
            s_builtOverrides = overrides;
            try
            {
                Build(scene, denied, overrides);
            }
            catch (Exception e)
            {
                s_byTrophy.Clear();
                Plugin.Log.LogError($"[Defender Ward] Could not build the trophy list: {e}");
            }
        }

        /// <summary>Creatures this trophy can summon, best fit first. Empty if it isn't a usable trophy.</summary>
        public static List<Creature> GetOptions(ItemDrop.ItemData item)
        {
            if (item == null || item.m_shared.m_itemType != ItemDrop.ItemData.ItemType.Trophy)
                return s_none;
            EnsureBuilt();
            return s_byTrophy.TryGetValue(item.m_shared.m_name, out List<Creature> list) ? list : s_none;
        }

        public static bool IsUsable(ItemDrop.ItemData item) => GetOptions(item).Count > 0;

        private static void Build(ZNetScene scene, string denied, string overrides)
        {
            s_byTrophy.Clear();
            HashSet<string> deniedSet = ParseNameSet(denied);

            // trophy prefab name → candidates, plus each trophy's shared name.
            var byTrophyPrefab = new Dictionary<string, List<Creature>>();
            var sharedNameOf = new Dictionary<string, string>();
            var creatures = new Dictionary<string, Creature>();

            foreach (GameObject prefab in scene.m_prefabs)
            {
                if (!prefab || deniedSet.Contains(prefab.name))
                    continue;
                Creature creature = ToCreature(prefab);
                if (creature == null)
                    continue;
                creatures[prefab.name] = creature;

                var drops = prefab.GetComponent<CharacterDrop>();
                if (!drops)
                    continue;
                foreach (CharacterDrop.Drop drop in drops.m_drops)
                {
                    ItemDrop item = drop?.m_prefab ? drop.m_prefab.GetComponent<ItemDrop>() : null;
                    if (!item || item.m_itemData.m_shared.m_itemType != ItemDrop.ItemData.ItemType.Trophy)
                        continue;
                    string trophy = drop.m_prefab.name;
                    if (!byTrophyPrefab.TryGetValue(trophy, out List<Creature> list))
                        byTrophyPrefab[trophy] = list = new List<Creature>();
                    if (!list.Contains(creature))
                        list.Add(creature);
                    sharedNameOf[trophy] = item.m_itemData.m_shared.m_name;
                }
            }

            foreach (KeyValuePair<string, List<Creature>> pair in byTrophyPrefab)
            {
                pair.Value.Sort((a, b) => CompareFit(pair.Key, a.Prefab, b.Prefab));
                RemoveSleepingCopies(pair.Value);
            }

            ApplyOverrides(overrides, byTrophyPrefab, sharedNameOf, creatures, deniedSet);

            foreach (KeyValuePair<string, List<Creature>> pair in byTrophyPrefab)
            {
                if (pair.Value.Count > 0 && sharedNameOf.TryGetValue(pair.Key, out string shared))
                    s_byTrophy[shared] = LabelVariants(pair.Value);
            }
            Plugin.Log.LogInfo($"[Defender Ward] {s_byTrophy.Count} trophies can summon a defender.");
        }

        /// <summary>A creature the ward may summon, or null: it must be able to fight on land and not be a boss.</summary>
        private static Creature ToCreature(GameObject prefab)
        {
            var character = prefab.GetComponent<Character>();
            var ai = prefab.GetComponent<MonsterAI>();
            if (!character || !ai || character is Player || !prefab.GetComponent<ZNetView>())
                return null;
            if (character.m_boss || ai.m_avoidLand)
                return null;
            return new Creature { Prefab = prefab.name, NameToken = character.m_name, StartsAsleep = ai.m_sleeping };
        }

        /// <summary>
        /// Drops copies of a creature that only differ by starting asleep, like Troll_sleeping next to Troll
        /// (placed in the world asleep; a summon is woken anyway, so both would be the same defender). A copy
        /// is a same-named creature whose prefab name is the other's plus one more part ("Troll" + "_sleeping",
        /// but not "Draugr" + "_Ranged_sleeping", which is the ranged draugr's copy) and whose sleep state
        /// differs; the longer name goes. An override can still pick it explicitly.
        /// </summary>
        private static void RemoveSleepingCopies(List<Creature> list)
        {
            list.RemoveAll(copy => list.Exists(other => IsSleepingCopyOf(copy, other)));
        }

        private static bool IsSleepingCopyOf(Creature copy, Creature other)
        {
            if (other == copy || other.StartsAsleep == copy.StartsAsleep)
                return false;
            string prefix = other.Prefab + "_";
            if (copy.Prefab.Length <= prefix.Length || !copy.Prefab.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return false;
            if (copy.Prefab.IndexOf('_', prefix.Length) >= 0)
                return false;
            return DisplayName(other) == DisplayName(copy);
        }

        /// <summary>
        /// Gives same-named creatures in one trophy's list a variant label from their prefab names: the part
        /// after the shortest one's name (Goblin / GoblinArcher → "Archer", Skeleton_DeepNorth → "Deep North"),
        /// or the whole prefab name when they don't share a start. Works on copies, because the same creature
        /// can be in another trophy's list next to different names.
        /// </summary>
        private static List<Creature> LabelVariants(List<Creature> list)
        {
            var result = list.ConvertAll(c => c.Copy());
            var groups = new Dictionary<string, List<Creature>>();
            foreach (Creature creature in result)
            {
                string name = DisplayName(creature);
                if (!groups.TryGetValue(name, out List<Creature> group))
                    groups[name] = group = new List<Creature>();
                group.Add(creature);
            }

            foreach (List<Creature> group in groups.Values)
            {
                if (group.Count < 2)
                    continue;
                string shortest = group[0].Prefab;
                foreach (Creature creature in group)
                {
                    if (creature.Prefab.Length < shortest.Length)
                        shortest = creature.Prefab;
                }
                foreach (Creature creature in group)
                {
                    if (creature.Prefab == shortest)
                        continue;
                    string rest = creature.Prefab.StartsWith(shortest, StringComparison.OrdinalIgnoreCase)
                        ? creature.Prefab.Substring(shortest.Length)
                        : creature.Prefab;
                    creature.Variant = Prettify(rest.Length > 0 ? rest : creature.Prefab);
                }
            }
            return result;
        }

        /// <summary>The creature's name in the player's language (what the player would see twice).</summary>
        private static string DisplayName(Creature creature) =>
            Localization.instance != null ? Localization.instance.Localize(creature.NameToken) : creature.NameToken;

        /// <summary>"_Ranged_sleeping" → "Ranged sleeping", "DeepNorth" → "Deep North".</summary>
        private static string Prettify(string prefabPart)
        {
            var sb = new System.Text.StringBuilder(prefabPart.Length + 4);
            for (int i = 0; i < prefabPart.Length; i++)
            {
                char c = prefabPart[i];
                if (c == '_' || c == '-' || c == ' ')
                {
                    sb.Append(' ');
                    continue;
                }
                bool wordStart = i > 0 && char.IsUpper(c) && char.IsLower(prefabPart[i - 1]);
                if (wordStart)
                    sb.Append(' ');
                sb.Append(c);
            }
            string text = System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
            return text.Length > 0 ? char.ToUpperInvariant(text[0]) + text.Substring(1) : prefabPart;
        }

        /// <summary>"TrophyX:CreatureY" entries put that creature first for that trophy (adding it if needed).</summary>
        private static void ApplyOverrides(string source, Dictionary<string, List<Creature>> byTrophyPrefab,
            Dictionary<string, string> sharedNameOf, Dictionary<string, Creature> creatures, HashSet<string> denied)
        {
            if (string.IsNullOrEmpty(source))
                return;

            foreach (string raw in source.Split(','))
            {
                string entry = raw.Trim();
                if (entry.Length == 0)
                    continue;

                string[] parts = entry.Split(':');
                string trophy = parts.Length == 2 ? parts[0].Trim() : "";
                string creatureName = parts.Length == 2 ? parts[1].Trim() : "";
                if (trophy.Length == 0 || creatureName.Length == 0)
                {
                    Plugin.Log.LogWarning($"[Defender Ward] Trophy Overrides: ignoring invalid entry '{entry}'. Expected Trophy:Creature.");
                    continue;
                }

                GameObject trophyPrefab = ObjectDB.instance.GetItemPrefab(trophy);
                ItemDrop item = trophyPrefab ? trophyPrefab.GetComponent<ItemDrop>() : null;
                if (!item || item.m_itemData.m_shared.m_itemType != ItemDrop.ItemData.ItemType.Trophy)
                {
                    Plugin.Log.LogWarning($"[Defender Ward] Trophy Overrides: '{trophy}' is not a trophy item, skipping '{entry}'.");
                    continue;
                }
                if (denied.Contains(creatureName) || !creatures.TryGetValue(creatureName, out Creature creature))
                {
                    Plugin.Log.LogWarning($"[Defender Ward] Trophy Overrides: '{creatureName}' is not a creature that can be summoned, skipping '{entry}'.");
                    continue;
                }

                if (!byTrophyPrefab.TryGetValue(trophy, out List<Creature> list))
                    byTrophyPrefab[trophy] = list = new List<Creature>();
                list.Remove(creature);
                list.Insert(0, creature);
                sharedNameOf[trophy] = item.m_itemData.m_shared.m_name;
            }
        }

        /// <summary>
        /// Best fit first: the creature whose prefab name matches the trophy name (without "Trophy") exactly,
        /// then the longest shared prefix, then the shorter name (the base creature before its variants).
        /// </summary>
        private static int CompareFit(string trophy, string a, string b)
        {
            string key = trophy.StartsWith("Trophy", StringComparison.OrdinalIgnoreCase) ? trophy.Substring(6) : trophy;
            int scoreA = FitScore(key, a);
            int scoreB = FitScore(key, b);
            if (scoreA != scoreB)
                return scoreB.CompareTo(scoreA);
            if (a.Length != b.Length)
                return a.Length.CompareTo(b.Length);
            return string.CompareOrdinal(a, b);
        }

        private static int FitScore(string key, string creature)
        {
            if (string.Equals(key, creature, StringComparison.OrdinalIgnoreCase))
                return 1000;
            int n = Mathf.Min(key.Length, creature.Length);
            int common = 0;
            while (common < n && char.ToLowerInvariant(key[common]) == char.ToLowerInvariant(creature[common]))
                common++;
            return common;
        }

        private static HashSet<string> ParseNameSet(string source) => new HashSet<string>(Util.NameLists.Parse(source));
    }
}
