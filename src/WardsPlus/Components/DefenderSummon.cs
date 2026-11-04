using UnityEngine;
using WardsPlus.Config;
using WardsPlus.Util;

namespace WardsPlus.Components
{
    /// <summary>
    /// Marks a creature as a Defender Ward's summon and sends it away when it's no longer needed.
    /// Added on every client: by the ward right after summoning, and by a Character.Awake patch for any
    /// creature whose ZDO says it was summoned (other clients, and summons reloaded with the world), so a
    /// summon left over from a logout or crash is always cleaned up.
    ///
    /// The checks run on the creature's owner (whoever simulates it), once a second. It despawns when:
    /// - it has had nothing to fight for Idle Despawn seconds;
    /// - its ward is gone, disabled, or no longer counts it as its summon (trophy removed or creature changed);
    /// - it strays far outside the ward's range.
    /// Patches use <see cref="IsSummon"/> to stop its drops, breeding and follow commands.
    /// </summary>
    public class DefenderSummon : MonoBehaviour
    {
        private const float CheckInterval = 1f;
        /// <summary>
        /// How long the ward may be missing before the summon leaves. A short grace, because on a world load
        /// the summon can appear a moment before its ward's data arrives.
        /// </summary>
        private const float MissingWardGrace = 5f;
        /// <summary>Strays farther than this past the ward's range (horizontally): sent away.</summary>
        private const float LeashSlack = 15f;

        private ZNetView m_nview;
        private BaseAI m_ai;
        private ZDOID m_ward;
        private float m_lastBusyTime;
        private float m_wardMissingSince = -1f;

        /// <summary>Whether this creature is a Defender Ward summon.</summary>
        public static bool IsSummon(Component component) => component && component.GetComponent<DefenderSummon>();

        /// <summary>Attaches the component if the creature's ZDO marks it as a summon. Safe to call more than once.</summary>
        public static void AttachIfSummon(Character character)
        {
            ZNetView nview = character ? character.m_nview : null;
            ZDO zdo = nview && nview.IsValid() ? nview.GetZDO() : null;
            if (zdo == null || zdo.GetZDOID(ZdoKeys.SummonOf).IsNone() || character.GetComponent<DefenderSummon>())
                return;
            character.gameObject.AddComponent<DefenderSummon>();
        }

        private void Awake()
        {
            m_nview = GetComponent<ZNetView>();
            m_ai = GetComponent<BaseAI>();
            if (!m_nview || !m_nview.IsValid())
            {
                enabled = false;
                return;
            }
            m_ward = m_nview.GetZDO().GetZDOID(ZdoKeys.SummonOf);
            m_lastBusyTime = Time.time;

            // Tamed creatures eat food lying around by default, but we don't want summons to do that. 
            if (m_ai is MonsterAI monster)
                monster.m_consumeItems = new System.Collections.Generic.List<ItemDrop>();
            InvokeRepeating(nameof(Check), CheckInterval, CheckInterval);
        }

        /// <summary>
        /// Some creatures start asleep (abominations, some draugr) and a sleeping creature skips its AI, so it
        /// would never fight. Done in Start, not Awake: this component can be added from Character.Awake,
        /// before the creature's MonsterAI has set itself up.
        /// </summary>
        private void Start()
        {
            if (!(m_ai is MonsterAI monster) || !m_nview || !m_nview.IsValid())
                return;
            // Never doze off again when no player is near (instance value, every client).
            monster.m_fallAsleepDistance = 0f;
            if (m_nview.IsOwner() && monster.IsSleeping())
                monster.Wakeup(); // Plays the creature's own wake-up effect and syncs it to every client.
        }

        private void Check()
        {
            if (!m_nview || !m_nview.IsValid() || !m_nview.IsOwner())
                return;

            if (m_ai && (m_ai.HaveTarget() || m_ai.IsAlerted()))
                m_lastBusyTime = Time.time;

            if (ShouldLeave())
                Despawn();
        }

        private bool ShouldLeave()
        {
            if (Time.time - m_lastBusyTime > PluginConfig.DefenderIdleDespawn.Value)
                return true;

            ZDO ward = ZDOMan.instance != null ? ZDOMan.instance.GetZDO(m_ward) : null;
            if (ward == null)
            {
                if (m_wardMissingSince < 0f)
                    m_wardMissingSince = Time.time;
                return Time.time - m_wardMissingSince > MissingWardGrace;
            }
            m_wardMissingSince = -1f;

            // The ward hands out one summon at a time and forgets it when it no longer wants it.
            if (!ward.GetBool(ZdoKeys.Enabled, true) || ward.GetZDOID(ZdoKeys.Summon) != m_nview.GetZDO().m_uid)
                return true;

            WardConfig config = PluginConfig.For(WardType.Defender);
            float range = config.ClampRange(ward.GetFloat(ZdoKeys.Range, config.DefaultRange.Value));
            Vector3 offset = transform.position - ward.GetPosition();
            offset.y = 0f;
            float leash = range + LeashSlack;
            return offset.sqrMagnitude > leash * leash;
        }

        private void Despawn()
        {
            CancelInvoke(nameof(Check));
            DefenderWard.PlaySummonEffect(m_ward, transform.position);
            ZNetScene.instance.Destroy(gameObject);
        }
    }
}
