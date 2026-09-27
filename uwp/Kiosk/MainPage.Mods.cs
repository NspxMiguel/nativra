using System.Collections.Generic;

namespace Kiosk
{
    /// <summary>
    /// One place mods could come from, and honestly, what stands between here
    /// and actually installing one. None of these work yet — see each note.
    /// </summary>
    public sealed class ModSource
    {
        public string Name { get; set; }
        public string Note { get; set; }
        public string Needs { get; set; }
    }

    public sealed partial class MainPage
    {
        /// <summary>
        /// The well-known mod sources, and what each one would actually take
        /// to bring in. Thunderstore and Nexus both assume a modded Windows
        /// install (BepInEx or a DLL dropped beside the game's own) that this
        /// project does not have yet — a mod is code that has to run inside
        /// the game the way the game's own code does, and nothing here loads
        /// extra code into a guest process on request today. Steam Workshop
        /// reuses the sign-in already built; GameBanana is a public API with
        /// no sign-in the same way Thunderstore's is.
        /// </summary>
        private static readonly List<ModSource> ModSources = new List<ModSource>
        {
            new ModSource
            {
                Name = "Thunderstore",
                Note = "Open API, no sign-in. The largest source for the games here that mod at all (Hades among them).",
                Needs = "A loader for BepInEx-style plugins — code that runs inside the game, not a file next to it. Nothing here injects extra code into a guest yet.",
            },
            new ModSource
            {
                Name = "Nexus Mods",
                Note = "The largest mod site overall, across far more games than Thunderstore covers.",
                Needs = "Your account and a Nexus API key (nothing here should ever hold one without you giving it), plus the same code-loading problem as Thunderstore for a mod that is not just replaced files.",
            },
            new ModSource
            {
                Name = "Steam Workshop",
                Note = "Reuses the Steam session already signed in here — no separate account.",
                Needs = "ISteamRemoteStorage's UGC calls served for real (today they answer honestly empty) and a place to put downloaded Workshop content the game's own code would look for.",
            },
            new ModSource
            {
                Name = "GameBanana",
                Note = "A public API, no sign-in, covers a long tail of games a modder might target that the other three do not.",
                Needs = "Same code-loading problem as Thunderstore/Nexus for anything beyond a straight file replacement.",
            },
        };

        private void ShowMods()
        {
            ModSourceList.ItemsSource = ModSources;
            ModsTitle.Text = Texts.Get("mods.title");
        }
    }
}
