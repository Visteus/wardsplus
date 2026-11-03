using System.IO;
using System.Reflection;
using Jotunn.Managers;

namespace WardsPlus.Util
{
    /// <summary>
    /// Loads the translation JSON files embedded in the DLL (Assets/Translations/&lt;Language&gt;.json)
    /// into Jotunn's localization, so the mod ships as a single file.
    /// </summary>
    internal static class Translations
    {
        private const string ResourcePrefix = "WardsPlus.Translations.";

        public static void Load()
        {
            var localization = LocalizationManager.Instance.GetLocalization();
            Assembly assembly = Assembly.GetExecutingAssembly();

            foreach (string resource in assembly.GetManifestResourceNames())
            {
                if (!resource.StartsWith(ResourcePrefix) || !resource.EndsWith(".json"))
                    continue;

                string language = resource.Substring(ResourcePrefix.Length, resource.Length - ResourcePrefix.Length - ".json".Length);
                using (Stream stream = assembly.GetManifestResourceStream(resource))
                using (var reader = new StreamReader(stream))
                {
                    localization.AddJsonFile(language, reader.ReadToEnd());
                }
            }
        }
    }
}
