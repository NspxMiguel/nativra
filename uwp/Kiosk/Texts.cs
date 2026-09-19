using System.Collections.Generic;
using System.Globalization;

namespace Kiosk
{
    /// <summary>
    /// Screen text, never written inline in a view. Portuguese and English from
    /// the first screen; the console's own language picks the default and
    /// KIOSK_LANG overrides it when checking a translation.
    /// </summary>
    internal static class Texts
    {
        private static readonly Dictionary<string, string> Pt = new Dictionary<string, string>
        {
            { "app.eyebrow", "KIOSK" },
            { "app.title", "Tudo que está instalado neste console" },
            { "status.reading", "lendo o console..." },
            { "status.count", "{0} instalados" },
            { "status.empty", "nada instalado ainda — rode xbdev kit no Mac" },
            { "status.opening", "abrindo {0}..." },
            { "status.refused", "o console recusou abrir {0}" },
            { "status.failed", "não abriu {0}: {1}" },
            { "status.unreadable", "não consegui ler a lista de apps: {0}" },
            { "hint.open", "Abrir" },
            { "hint.refresh", "Atualizar" },
            { "empty.title", "Nada instalado ainda" },
            { "empty.next", "Rode xbdev sync no Mac e aperte Y aqui" },
            { "status.noprotocol", "este abre pelo Dev Home — ele não aceita ser aberto de fora" },
        };

        private static readonly Dictionary<string, string> En = new Dictionary<string, string>
        {
            { "app.eyebrow", "KIOSK" },
            { "app.title", "Everything installed on this console" },
            { "status.reading", "reading the console..." },
            { "status.count", "{0} installed" },
            { "status.empty", "nothing installed yet — run xbdev kit from the Mac" },
            { "status.opening", "opening {0}..." },
            { "status.refused", "the console refused to open {0}" },
            { "status.failed", "could not open {0}: {1}" },
            { "status.unreadable", "could not read the app list: {0}" },
            { "hint.open", "Open" },
            { "hint.refresh", "Refresh" },
            { "empty.title", "Nothing installed yet" },
            { "empty.next", "Run xbdev sync on the Mac, then press Y here" },
            { "status.noprotocol", "this one opens from Dev Home — it accepts no outside launch" },
        };

        private static readonly Dictionary<string, string> Active = Pick();

        private static Dictionary<string, string> Pick()
        {
            var forced = System.Environment.GetEnvironmentVariable("KIOSK_LANG");
            if (!string.IsNullOrEmpty(forced))
            {
                return forced.StartsWith("pt") ? Pt : En;
            }
            var language = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            return language == "pt" ? Pt : En;
        }

        public static string Get(string key, params object[] args)
        {
            if (!Active.TryGetValue(key, out var template) &&
                !En.TryGetValue(key, out template))
            {
                return key;
            }
            return args.Length == 0 ? template : string.Format(template, args);
        }
    }
}
