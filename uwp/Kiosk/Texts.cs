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
            { "hint.back", "Voltar" },
            { "hint.search", "Buscar" },
            { "hint.filter", "Filtrar" },
            { "steam.filter.all", "todos" },
            { "steam.filter.played", "jogados" },
            { "steam.filter.never", "nunca jogados" },
            { "steam.filter.tested", "testados aqui" },
            { "steam.showing", "mostrando {0} de {1}" },
            { "hint.signout", "Sair da conta" },
            { "tile.steam", "Steam" },
            { "tile.steam.sub", "Seus jogos de PC" },
            { "steam.header", "Steam" },
            { "steam.loading", "carregando sua biblioteca..." },
            { "steam.count", "{0} jogos" },
            { "steam.empty", "nenhum jogo nesta conta" },
            { "steam.failed", "não consegui ler a biblioteca: {0}" },
            { "steam.notyet", "{0} ainda não roda aqui — falta a camada de tradução" },
            { "steam.hours", "{0} h jogadas" },
            { "steam.minutes", "{0} min jogados" },
            { "steam.neverplayed", "nunca jogado" },
            { "signin.title", "Entrar na Steam" },
            { "signin.how", "Abra o app Steam no celular, toque no ícone de escanear e aponte para o código ao lado." },
            { "signin.asking", "pedindo o código à Steam..." },
            { "signin.waiting", "esperando você aprovar no celular" },
            { "signin.retry", "tentando de novo ({0})" },
            { "signin.failed", "a Steam não respondeu: {0}" },
            { "signin.qrfailed", "não consegui desenhar o código: {0}" },
            { "signin.expired", "o código expirou — aperte Y para gerar outro" },
            { "signed.title", "Conectado como {0}" },
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
            { "hint.back", "Back" },
            { "hint.search", "Search" },
            { "hint.filter", "Filter" },
            { "steam.filter.all", "all" },
            { "steam.filter.played", "played" },
            { "steam.filter.never", "never played" },
            { "steam.filter.tested", "tested here" },
            { "steam.showing", "showing {0} of {1}" },
            { "hint.signout", "Sign out" },
            { "tile.steam", "Steam" },
            { "tile.steam.sub", "Your PC games" },
            { "steam.header", "Steam" },
            { "steam.loading", "loading your library..." },
            { "steam.count", "{0} games" },
            { "steam.empty", "no games on this account" },
            { "steam.failed", "could not read the library: {0}" },
            { "steam.notyet", "{0} does not run here yet — the translation layer is missing" },
            { "steam.hours", "{0} h played" },
            { "steam.minutes", "{0} min played" },
            { "steam.neverplayed", "never played" },
            { "signin.title", "Sign in to Steam" },
            { "signin.how", "Open the Steam app on your phone, tap the scan icon and point it at the code." },
            { "signin.asking", "asking Steam for a code..." },
            { "signin.waiting", "waiting for you to approve on your phone" },
            { "signin.retry", "retrying ({0})" },
            { "signin.failed", "Steam did not answer: {0}" },
            { "signin.qrfailed", "could not draw the code: {0}" },
            { "signin.expired", "the code expired — press Y for a new one" },
            { "signed.title", "Signed in as {0}" },
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
