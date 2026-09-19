using System.Collections.Generic;
using System.Globalization;

namespace Vapor
{
    /// <summary>Screen text, Portuguese and English from the first screen.</summary>
    internal static class Texts
    {
        private static readonly Dictionary<string, string> Pt = new Dictionary<string, string>
        {
            { "signin.title", "Entrar na Steam" },
            { "signin.how", "Abra o app Steam no celular, toque no ícone de escanear e aponte para o código ao lado." },
            { "signin.asking", "pedindo o código à Steam..." },
            { "signin.waiting", "esperando você aprovar no celular" },
            { "signin.retry", "tentando de novo ({0})" },
            { "signin.failed", "a Steam não respondeu: {0}" },
            { "signin.qrfailed", "não consegui desenhar o código: {0}" },
            { "signin.expired", "o código expirou — feche e abra o app para gerar outro" },
            { "signed.title", "Conectado como {0}" },
            { "signed.next", "Sessão guardada neste console." },
        };

        private static readonly Dictionary<string, string> En = new Dictionary<string, string>
        {
            { "signin.title", "Sign in to Steam" },
            { "signin.how", "Open the Steam app on your phone, tap the scan icon and point it at the code." },
            { "signin.asking", "asking Steam for a code..." },
            { "signin.waiting", "waiting for you to approve on your phone" },
            { "signin.retry", "retrying ({0})" },
            { "signin.failed", "Steam did not answer: {0}" },
            { "signin.qrfailed", "could not draw the code: {0}" },
            { "signin.expired", "the code expired — reopen the app for a new one" },
            { "signed.title", "Signed in as {0}" },
            { "signed.next", "Session stored on this console." },
        };

        private static readonly Dictionary<string, string> Active =
            CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "pt" ? Pt : En;

        public static string Get(string key, params object[] args)
        {
            if (!Active.TryGetValue(key, out var t) && !En.TryGetValue(key, out t)) return key;
            return args.Length == 0 ? t : string.Format(t, args);
        }
    }
}
