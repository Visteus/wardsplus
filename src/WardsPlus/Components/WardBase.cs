using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using WardsPlus.Config;
using WardsPlus.UI;
using WardsPlus.Util;

namespace WardsPlus.Components
{
    /// <summary>
    /// Shared behaviour for every WardsPlus ward: networked settings, glow/area-marker visuals,
    /// hover text, interaction (opens the inventory screen with the ward's settings panel) and a
    /// jittered owner-only simulation tick that subclasses hook into.
    /// </summary>
    public abstract class WardBase : MonoBehaviour, Hoverable, Interactable
    {
        // ---- Set on the prefab by WardPieces. Public fields so Unity copies them on Instantiate. ----
        public string m_name = "";
        public MeshRenderer m_model;
        public GameObject m_enabledEffect;
        public CircleProjector m_areaMarker;
        /// <summary>
        /// Optional second ring in its own color (e.g. the Gathering Ward's deposit range). Sized from
        /// <see cref="GetSecondaryRange"/> and shown with the main ring; hidden while that range is 0.
        /// </summary>
        public CircleProjector m_secondaryMarker;
        public Container m_container;
        public float m_hoverOffset;
        /// <summary>Area-marker segment density taken from the vanilla ward, so large radii stay dense.</summary>
        public float m_markerSegmentsPerMeter = 0.5f;

        private const float AreaMarkerLinger = 0.5f;
        private const float JitterFraction = 0.2f;
        /// <summary>After a setting changes (enabled, range...), re-evaluate soon rather than after a full idle wait.</summary>
        private const float SettingChangedTickDelay = 1f;

        private static readonly List<WardBase> s_all = new List<WardBase>();

        /// <summary>Every loaded WardsPlus ward on this client.</summary>
        public static IReadOnlyList<WardBase> All => s_all;

        protected ZNetView m_nview;
        protected Piece m_piece;
        private bool m_lastVisualEnabled;
        private bool m_visualsInitialized;
        private Coroutine m_tickRoutine;
        private float m_nextTickTime;
        private bool m_inTick;

        public abstract WardType Type { get; }

        /// <summary>Base idle wait between ticks, before jitter. Subclasses override per ward type.</summary>
        protected virtual float IdleInterval => 30f;

        internal WardConfig Config => PluginConfig.For(Type);

        public bool IsValid => m_nview != null && m_nview.IsValid();

        // ---------------------------------------------------------------- lifecycle

        protected virtual void Awake()
        {
            m_nview = GetComponent<ZNetView>();
            if (m_nview == null || m_nview.GetZDO() == null)
                return; // Ghost/placement preview or prefab: no network object, nothing to do.

            m_piece = GetComponent<Piece>();
            if (m_areaMarker)
                m_areaMarker.gameObject.SetActive(false);
            if (m_secondaryMarker)
                m_secondaryMarker.gameObject.SetActive(false);

            m_nview.Register<int, bool>(WardRpcs.SetBool, RPC_SetBool);
            m_nview.Register<int, float>(WardRpcs.SetFloat, RPC_SetFloat);

            // First time this ward exists: stamp the configured defaults so later config changes
            // don't silently change already-placed wards.
            if (m_nview.IsOwner())
                InitDefaults(m_nview.GetZDO());

            s_all.Add(this);
            InvokeRepeating(nameof(UpdateVisuals), 0f, 1f);
            // Random first offset spreads out wards that all load at the same moment.
            ScheduleTick(Random.Range(0f, IdleInterval));
        }

        /// <summary>
        /// Owner only, on every load: write any setting the ZDO doesn't have yet. Overrides must call base
        /// and only write keys that are missing.
        /// </summary>
        protected virtual void InitDefaults(ZDO zdo)
        {
            if (!zdo.GetFloat(ZdoKeys.Range, out _))
                zdo.Set(ZdoKeys.Range, Config.DefaultRange.Value);
        }

        protected virtual void OnDestroy()
        {
            s_all.Remove(this);
        }

        // ---------------------------------------------------------------- settings (ZDO-backed)

        public bool IsEnabled() => IsValid && m_nview.GetZDO().GetBool(ZdoKeys.Enabled, true);

        public float GetRange() =>
            IsValid ? Config.ClampRange(m_nview.GetZDO().GetFloat(ZdoKeys.Range, Config.DefaultRange.Value)) : 0f;

        /// <summary>A float setting from the ZDO, clamped the same way the owner clamps writes.</summary>
        public float GetFloatSetting(int key, float defaultValue) =>
            ClampSetting(key, IsValid ? m_nview.GetZDO().GetFloat(key, defaultValue) : defaultValue);

        /// <summary>Limits for float settings. Applied by the owner on every write and when reading.</summary>
        protected virtual float ClampSetting(int key, float value) =>
            key == ZdoKeys.Range ? Config.ClampRange(value) : value;

        /// <summary>ZDO data revision; changes whenever any synced setting changes.</summary>
        public uint DataRevision => IsValid ? m_nview.GetZDO().DataRevision : 0u;

        /// <summary>Asks the ZDO owner to change a bool setting. Safe to call from any client.</summary>
        public void RequestSetBool(int key, bool value)
        {
            if (IsValid)
                m_nview.InvokeRPC(WardRpcs.SetBool, key, value);
        }

        /// <summary>Asks the ZDO owner to change a float setting. Safe to call from any client.</summary>
        public void RequestSetFloat(int key, float value)
        {
            if (IsValid)
                m_nview.InvokeRPC(WardRpcs.SetFloat, key, value);
        }

        private void RPC_SetBool(long sender, int key, bool value)
        {
            if (!m_nview.IsOwner())
                return;
            m_nview.GetZDO().Set(key, value);
            UpdateVisuals();
            RequestTickSoon(SettingChangedTickDelay);
        }

        private void RPC_SetFloat(long sender, int key, float value)
        {
            if (!m_nview.IsOwner())
                return;
            m_nview.GetZDO().Set(key, ClampSetting(key, value));
            UpdateVisuals();
            RequestTickSoon(SettingChangedTickDelay);
        }

        // ---------------------------------------------------------------- simulation tick

        /// <summary>
        /// Returns <paramref name="interval"/> ±20%, re-rolled on every call, so wards loaded together
        /// drift apart instead of all scanning on the same frame forever.
        /// </summary>
        public static float NextIdleDelay(float interval) =>
            Mathf.Max(0.1f, interval * Random.Range(1f - JitterFraction, 1f + JitterFraction));

        /// <summary>(Re)starts the tick loop, with the first tick after <paramref name="delay"/> seconds.</summary>
        private void ScheduleTick(float delay)
        {
            if (m_tickRoutine != null)
                StopCoroutine(m_tickRoutine);
            m_nextTickTime = Time.time + delay;
            m_tickRoutine = StartCoroutine(TickLoop(delay));
        }

        /// <summary>
        /// Brings the next tick forward to at most <paramref name="delay"/> seconds from now, e.g. when fuel
        /// is added to an idle ward. Ignored during a tick and when a tick is already due sooner.
        /// </summary>
        protected void RequestTickSoon(float delay)
        {
            if (m_inTick || !isActiveAndEnabled || Time.time + delay >= m_nextTickTime)
                return;
            ScheduleTick(delay);
        }

        private IEnumerator TickLoop(float firstDelay)
        {
            yield return new WaitForSeconds(firstDelay);

            while (true)
            {
                float delay = NextIdleDelay(IdleInterval);
                // Only the ZDO owner (one nearby peer) simulates, so effects never run twice.
                if (IsValid && m_nview.IsOwner() && IsEnabled())
                {
                    m_inTick = true;
                    try
                    {
                        delay = OnTick();
                    }
                    catch (System.Exception e)
                    {
                        Plugin.Log.LogError($"{GetType().Name} tick failed: {e}");
                    }
                    finally
                    {
                        m_inTick = false;
                    }
                }
                m_nextTickTime = Time.time + delay;
                yield return new WaitForSeconds(delay);
            }
        }

        /// <summary>
        /// Owner-only work for this ward. Returns how long to wait before the next tick; the default
        /// is a jittered idle interval. Active wards (e.g. Repair while repairing) can return a short delay.
        /// </summary>
        protected virtual float OnTick() => NextIdleDelay(IdleInterval);

        // ---------------------------------------------------------------- visuals

        /// <summary>Glow on/off (replaces what vanilla PrivateArea.UpdateStatus did) and area-marker size.</summary>
        private void UpdateVisuals()
        {
            if (!IsValid)
                return;

            bool enabled = IsEnabled();
            if (!m_visualsInitialized || enabled != m_lastVisualEnabled)
            {
                m_visualsInitialized = true;
                m_lastVisualEnabled = enabled;
                if (m_enabledEffect)
                    m_enabledEffect.SetActive(enabled);
                if (m_model)
                {
                    foreach (Material material in m_model.materials)
                    {
                        if (enabled)
                            material.EnableKeyword("_EMISSION");
                        else
                            material.DisableKeyword("_EMISSION");
                    }
                }
            }

            UpdateAreaMarkerShape();
        }

        /// <summary>Radius of <see cref="m_secondaryMarker"/>; 0 hides it. Wards with a second ring override this.</summary>
        protected virtual float GetSecondaryRange() => 0f;

        private void UpdateAreaMarkerShape()
        {
            ShapeMarker(m_areaMarker, GetRange());
            if (m_secondaryMarker)
                ShapeMarker(m_secondaryMarker, GetSecondaryRange());
        }

        private void ShapeMarker(CircleProjector marker, float range)
        {
            if (!marker || range <= 0f)
                return;
            marker.m_radius = range;
            // CircleProjector rebuilds its segments only when the count changes.
            marker.m_nrOfSegments = Mathf.Max(12, Mathf.RoundToInt(2f * Mathf.PI * range * m_markerSegmentsPerMeter));
        }

        /// <summary>Shows the dotted range ring(s) briefly; call repeatedly (hover, open panel) to keep them up.</summary>
        public void ShowAreaMarker()
        {
            if (!m_areaMarker && !m_secondaryMarker)
                return;
            UpdateAreaMarkerShape();
            if (m_areaMarker)
                m_areaMarker.gameObject.SetActive(true);
            if (m_secondaryMarker)
                m_secondaryMarker.gameObject.SetActive(GetSecondaryRange() > 0f);
            CancelInvoke(nameof(HideAreaMarker));
            Invoke(nameof(HideAreaMarker), AreaMarkerLinger);
        }

        private void HideAreaMarker()
        {
            if (m_areaMarker)
                m_areaMarker.gameObject.SetActive(false);
            if (m_secondaryMarker)
                m_secondaryMarker.gameObject.SetActive(false);
        }

        // ---------------------------------------------------------------- access + interaction

        /// <summary>Vanilla build permission at this spot (respects any active vanilla ward over it).</summary>
        public bool HasLocalAccess(bool flash) => PrivateArea.CheckAccess(transform.position, 0f, flash);

        public string GetHoverText()
        {
            if (!IsValid || Player.m_localPlayer == null)
                return "";

            ShowAreaMarker();

            var sb = new StringBuilder(256);
            sb.Append(m_name);
            sb.Append(IsEnabled() ? " ( $wp_status_active )" : " ( $wp_status_inactive )");
            sb.Append($"\n$wp_range: {GetRange():0}m");
            AppendHoverStatus(sb);

            if (HasLocalAccess(flash: false))
                sb.Append("\n[<color=yellow><b>$KEY_Use</b></color>] $wp_open");
            else
                sb.Append("\n$piece_noaccess");

            return Localization.instance.Localize(sb.ToString());
        }

        /// <summary>Subclasses append ward-specific status lines (charges, fuel, summon...).</summary>
        protected virtual void AppendHoverStatus(StringBuilder sb) { }

        public string GetHoverName() => m_name;

        public float GetHoverOffset() => m_hoverOffset;

        public bool Interact(Humanoid user, bool hold, bool alt)
        {
            if (hold || !IsValid || !(user is Player player) || player != Player.m_localPlayer)
                return false;

            if (!HasLocalAccess(flash: true))
                return true; // Flashes the blocking vanilla ward, like a locked chest does.

            if (m_container)
            {
                // Vanilla container flow opens InventoryGui; WardPanel notices the ward's container and shows itself.
                return m_container.Interact(user, false, false);
            }

            // No inventory (e.g. Spawnproof): open the inventory screen and show the panel in its place.
            WardPanel.OpenWithoutContainer(this);
            return true;
        }

        public bool UseItem(Humanoid user, ItemDrop.ItemData item) => false;

        // ---------------------------------------------------------------- panel hooks

        /// <summary>
        /// Lets a ward type add its own controls below the shared Enabled/Range rows.
        /// Called each time the panel opens for this ward; <paramref name="extras"/> starts empty.
        /// </summary>
        public virtual void PopulatePanel(RectTransform extras) { }

        /// <summary>Called every frame while the panel is open for this ward, to refresh custom controls.</summary>
        public virtual void RefreshPanel() { }
    }
}
