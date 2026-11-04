using System;
using System.Collections.Generic;

namespace WardsPlus.Util
{
    /// <summary>Comma-separated name lists, as used in config entries and ZDO strings.</summary>
    internal static class NameLists
    {
        /// <summary>The trimmed, non-empty names in <paramref name="source"/>, in order.</summary>
        public static IEnumerable<string> Parse(string source)
        {
            if (string.IsNullOrEmpty(source))
                yield break;
            foreach (string raw in source.Split(','))
            {
                string name = raw.Trim();
                if (name.Length > 0)
                    yield return name;
            }
        }

        /// <summary>Sorted (ordinal) and comma-joined, so the same set always gives the same string.</summary>
        public static string Join(IEnumerable<string> names)
        {
            var sorted = new List<string>(names);
            sorted.Sort(StringComparer.Ordinal);
            return string.Join(",", sorted);
        }
    }
}
