using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using WardsPlus.Config;

namespace WardsPlus.Util
{
    /// <summary>
    /// Diagnostic event lines, written only while the "Debug Logging" config option is on. Lines go to the
    /// BepInEx log at Info level (Debug level isn't written to LogOutput.log by default), prefixed "[debug]".
    /// In loops, check <see cref="Enabled"/> before building the message so normal play pays nothing.
    /// </summary>
    internal static class DebugLog
    {
        public static bool Enabled => PluginConfig.DebugLogging != null && PluginConfig.DebugLogging.Value;

        public static void Write(string message)
        {
            if (Enabled)
                Plugin.Log.LogInfo("[debug] " + message);
        }

        private static readonly Dictionary<string, float> s_lastWrite = new Dictionary<string, float>();

        /// <summary>
        /// Like <see cref="Write"/>, but at most once per <paramref name="seconds"/> for the same
        /// <paramref name="key"/>, for events that repeat every few seconds (e.g. a blocked spawner).
        /// </summary>
        public static void WriteThrottled(string key, float seconds, string message)
        {
            if (!Enabled)
                return;
            float now = Time.realtimeSinceStartup;
            if (s_lastWrite.TryGetValue(key, out float last) && now - last < seconds)
                return;
            s_lastWrite[key] = now;
            Write(message);
        }

        /// <summary>"Gathering Ward 12345:67" style label for a ward or other network object.</summary>
        public static string Name(string kind, ZNetView view)
        {
            ZDO zdo = view && view.IsValid() ? view.GetZDO() : null;
            return zdo != null ? $"{kind} {zdo.m_uid.UserID}:{zdo.m_uid.ID}" : kind;
        }

        /// <summary>Horizontal distance, for "3.2m away".</summary>
        public static string Distance(Vector3 a, Vector3 b)
        {
            Vector3 offset = a - b;
            offset.y = 0f;
            return $"{offset.magnitude:0.0}m";
        }

        private static Dictionary<int, string> s_keyNames;

        /// <summary>Readable name of a <see cref="ZdoKeys"/> hash, e.g. "Range".</summary>
        public static string KeyName(int key)
        {
            if (s_keyNames == null)
            {
                s_keyNames = new Dictionary<int, string>();
                foreach (FieldInfo field in typeof(ZdoKeys).GetFields(BindingFlags.Public | BindingFlags.Static))
                {
                    if (field.FieldType == typeof(int))
                        s_keyNames[(int)field.GetValue(null)] = field.Name;
                }
            }
            return s_keyNames.TryGetValue(key, out string name) ? name : key.ToString();
        }
    }
}
