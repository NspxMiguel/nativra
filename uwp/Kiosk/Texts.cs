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
            { "app.eyebrow", "EM DESENVOLVIMENTO  \u00B7  POR NSPXMIGUEL" },
            { "tile.allgames", "Todos os jogos \u00B7 {0}" },
            { "status.notyet", "{0} ainda n\u00E3o foi constru\u00EDdo" },
            { "dock.library", "Biblioteca" },
            { "emu.add", "P\u00F4r na biblioteca" },
            { "emu.remove", "Tirar da biblioteca" },
            { "dock.shop", "Loja" },
            { "dock.emulators", "Emuladores" },
            { "dock.friends", "Amigos" },
            { "dock.mods", "Mods" },
            { "dock.downloads", "Downloads" },
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
            { "game.install", "Instalar" },
            { "game.play", "Jogar" },
            { "game.cancel", "Cancelar" },
            { "game.where", "INSTALAR EM" },
            { "game.place.console", "Console" },
            { "game.place.dev", "Pasta de desenvolvimento" },
            { "game.sizeunknown", "tamanho lido ao instalar" },
            { "game.playedlabel", "TEMPO DE JOGO" },
            { "game.statelabel", "ESTADO" },
            { "game.state.installed", "Instalado" },
            { "game.restart", "Feche e abra o Nativra antes de iniciar outro jogo." },
            { "game.state.notinstalled", "Não instalado" },
            { "game.cannotrun.title", "Ainda não dá para abrir" },
            { "game.cannotrun.body", "{0} está baixado no console, mas abrir um executável de Windows aqui depende da camada que ainda estou construindo." },
            { "game.cannotrun.hint", "B para voltar" },
            { "game.failed.title", "O download parou" },
            { "game.failed.hint", "B para voltar. O que já baixou fica, e continuar recomeça de onde parou." },
            { "hint.search", "Buscar" },
            { "hint.options", "Opções" },
            { "steam.shelf.all", "Todos" },
            { "steam.shelf.family", "Família" },
            { "hint.filter", "Filtrar" },
            { "steam.filter.all", "todos" },
            { "steam.filter.downloaded", "baixados" },
            { "steam.downloaded.empty", "Nenhum jogo baixado nesta seleção — volte a todos para instalar um jogo ou ajuste a busca." },
            { "steam.filter.played", "jogados" },
            { "steam.filter.never", "nunca jogados" },
            { "steam.filter.tested", "testados aqui" },
            { "steam.showing", "mostrando {0} de {1}" },
            { "hint.signout", "Sair da conta" },
            { "tile.steam", "Steam" },
            { "tile.steam.sub", "Seus jogos de PC" },
            { "tile.game.sub", "Baixado da Steam" },
            { "status.gamenotyet", "{0} está baixado — falta a camada que executa Windows aqui" },
            { "steam.header", "Steam" },
            { "steam.loading", "carregando sua biblioteca..." },
            { "steam.count", "{0} jogos" },
            { "steam.empty", "nenhum jogo nesta conta" },
            { "steam.failed", "não consegui ler a biblioteca: {0}" },
            { "steam.notyet", "{0} ainda não roda aqui — falta a camada de tradução" },
            { "steam.busy", "já tem um download em andamento" },
            { "steam.starting", "preparando {0}..." },
            { "steam.downloading", "{0} — {1}%  {2}" },
            { "steam.downloaded", "{0} baixado no console" },
            { "steam.downloadfailed", "{0} falhou: {1}" },
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
            { "app.eyebrow", "IN DEVELOPMENT  \u00B7  BY NSPXMIGUEL" },
            { "tile.allgames", "All games \u00B7 {0}" },
            { "status.notyet", "{0} is not built yet" },
            { "dock.library", "Library" },
            { "emu.add", "Add to library" },
            { "emu.remove", "Remove from library" },
            { "dock.shop", "Shop" },
            { "dock.emulators", "Emulators" },
            { "dock.friends", "Friends" },
            { "dock.mods", "Mods" },
            { "dock.downloads", "Downloads" },
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
            { "game.install", "Install" },
            { "game.play", "Play" },
            { "game.cancel", "Cancel" },
            { "game.where", "INSTALL TO" },
            { "game.place.console", "Console" },
            { "game.place.dev", "Developer folder" },
            { "game.sizeunknown", "size read on install" },
            { "game.playedlabel", "PLAY TIME" },
            { "game.statelabel", "STATE" },
            { "game.state.installed", "Installed" },
            { "game.restart", "Close and reopen Nativra before starting another game." },
            { "game.state.notinstalled", "Not installed" },
            { "game.cannotrun.title", "It cannot open yet" },
            { "game.cannotrun.body", "{0} is downloaded on the console, but opening a Windows executable here waits on the layer I am still building." },
            { "game.cannotrun.hint", "B to go back" },
            { "game.failed.title", "The download stopped" },
            { "game.failed.hint", "B to go back. What came down stays, and starting again picks up from there." },
            { "hint.search", "Search" },
            { "hint.options", "Options" },
            { "steam.shelf.all", "All" },
            { "steam.shelf.family", "Family" },
            { "hint.filter", "Filter" },
            { "steam.filter.all", "all" },
            { "steam.filter.downloaded", "downloaded" },
            { "steam.downloaded.empty", "No downloaded games in this selection — switch to all to install a game or adjust your search." },
            { "steam.filter.played", "played" },
            { "steam.filter.never", "never played" },
            { "steam.filter.tested", "tested here" },
            { "steam.showing", "showing {0} of {1}" },
            { "hint.signout", "Sign out" },
            { "tile.steam", "Steam" },
            { "tile.steam.sub", "Your PC games" },
            { "tile.game.sub", "Downloaded from Steam" },
            { "status.gamenotyet", "{0} is downloaded — the layer that runs Windows here is still missing" },
            { "steam.header", "Steam" },
            { "steam.loading", "loading your library..." },
            { "steam.count", "{0} games" },
            { "steam.empty", "no games on this account" },
            { "steam.failed", "could not read the library: {0}" },
            { "steam.notyet", "{0} does not run here yet — the translation layer is missing" },
            { "steam.busy", "a download is already running" },
            { "steam.starting", "preparing {0}..." },
            { "steam.downloading", "{0} — {1}%  {2}" },
            { "steam.downloaded", "{0} downloaded on the console" },
            { "steam.downloadfailed", "{0} failed: {1}" },
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
