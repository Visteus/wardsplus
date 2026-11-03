using System;
using UnityEngine;
using UnityEngine.UI;
using WardsPlus.Components;

namespace WardsPlus.UI
{
    /// <summary>
    /// A panel slider bound to one float setting in the open ward's ZDO.
    /// - Only values the player dragged to are sent: anything the panel sets itself (binding a ward,
    ///   changing min/max, another player's change) never echoes back as a write.
    /// - Sends are rate-limited while dragging, and the last value is always flushed on close or rebind.
    /// - Incoming changes don't yank the handle out from under a drag that hasn't round-tripped yet.
    /// </summary>
    internal sealed class SettingSlider
    {
        private const float SendInterval = 0.2f;
        private const float LocalEditGrace = 0.5f;

        private readonly Slider _slider;
        private readonly Text _valueText;
        private readonly int _key;
        private readonly Func<WardBase, float> _read;
        private readonly Func<float, string> _format;

        private WardBase _ward;
        private bool _hasPending;
        private float _pending;
        private float _nextSend;
        private float _lastEdit = -100f;
        /// <summary>A sync was skipped during a local edit; retried once the edit settles.</summary>
        private bool _stale;
        private bool _applying;

        public SettingSlider(Slider slider, Text valueText, int key, Func<WardBase, float> read, Func<float, string> format)
        {
            _slider = slider;
            _valueText = valueText;
            _key = key;
            _read = read;
            _format = format;
            _slider.onValueChanged.AddListener(OnChanged);
        }

        public void Bind(WardBase ward, float min, float max)
        {
            Flush(force: true); // Finish an edit for the previous ward first.
            _ward = ward;
            _hasPending = false;
            _lastEdit = -100f;

            // Changing min/max makes Unity clamp the value and fire onValueChanged, which must not be
            // mistaken for the player dragging it (it would overwrite the ward's setting).
            _applying = true;
            try
            {
                _slider.minValue = Mathf.Min(min, max);
                _slider.maxValue = Mathf.Max(min, max);
            }
            finally
            {
                _applying = false;
            }
            Sync();
        }

        public void Unbind()
        {
            Flush(force: true);
            _ward = null;
            _hasPending = false;
        }

        /// <summary>Pulls the ward's current value into the slider, unless the player is mid-edit.</summary>
        public void Sync()
        {
            if (!_ward || !_slider)
                return;
            if (_hasPending || Time.time - _lastEdit <= LocalEditGrace)
            {
                _stale = true;
                return;
            }
            _stale = false;
            float value = _read(_ward);
            _slider.SetValueWithoutNotify(value);
            _valueText.text = _format(value);
        }

        /// <summary>Called every frame: sends a rate-limited pending edit and retries a skipped sync.</summary>
        public void Update()
        {
            Flush(force: false);
            if (_stale)
                Sync();
        }

        public void Flush(bool force)
        {
            if (!_hasPending || !_ward)
                return;
            if (!force && Time.time < _nextSend)
                return;
            _ward.RequestSetFloat(_key, _pending);
            _hasPending = false;
            _nextSend = Time.time + SendInterval;
        }

        private void OnChanged(float value)
        {
            if (_applying || !_ward)
                return;
            _valueText.text = _format(value);
            _pending = value;
            _hasPending = true;
            _lastEdit = Time.time;
        }
    }
}
