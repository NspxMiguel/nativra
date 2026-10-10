using System;
using System.IO;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.Storage;

namespace Kiosk
{
    public static class GameProfileStore
    {
        public static GameProfile Current { get; private set; } = new GameProfile();

        public static async Task<GameProfile> LoadAsync(uint appId)
        {
            var profile = new GameProfile();
            try
            {
                var folder = await ApplicationData.Current.LocalFolder.TryGetItemAsync("profiles") as StorageFolder;
                var file = folder == null ? null : await folder.TryGetItemAsync(appId + ".json") as StorageFile;
                if (file == null) return profile;
                var json = JsonObject.Parse(await FileIO.ReadTextAsync(file));
                var frameLimit = json.GetNamedNumber("frameLimit", 60);
                profile.FrameLimit = frameLimit == 0 || frameLimit == 30 || frameLimit == 60 ? (int)frameLimit : 60;
                profile.ResolutionScale = json.GetNamedNumber("resolutionScale", 1);
                profile.ForceInterpreter = json.GetNamedBoolean("forceInterpreter", false);
                profile.ControllerLayout = json.GetNamedString("controllerLayout", "default");
                profile.Normalize();
            }
            catch
            {
                // Missing, malformed or unreadable profiles use defaults as a whole.
                profile = new GameProfile();
            }
            return profile;
        }

        public static async Task ApplyAsync(uint appId)
        {
            Current = await LoadAsync(appId);
            Native.ControllerMode.Desktop = Current.ControllerLayout == "default"
                ? Settings.DesktopInput : Current.ControllerLayout == "desktop";
            Native.ControllerMode.Changes++;
            Native.PointerBridge.Warp(Current.ScreenWidth / 2, Current.ScreenHeight / 2);
        }

        public static async Task SaveAsync(uint appId, GameProfile profile)
        {
            profile.Normalize();
            var json = new JsonObject
            {
                { "frameLimit", JsonValue.CreateNumberValue(profile.FrameLimit) },
                { "resolutionScale", JsonValue.CreateNumberValue(profile.ResolutionScale) },
                { "forceInterpreter", JsonValue.CreateBooleanValue(profile.ForceInterpreter) },
                { "controllerLayout", JsonValue.CreateStringValue(profile.ControllerLayout) },
            };
            var folder = await ApplicationData.Current.LocalFolder.CreateFolderAsync("profiles", CreationCollisionOption.OpenIfExists);
            // Write completely before replacing the previous profile.
            var file = await folder.CreateFileAsync(appId + ".tmp", CreationCollisionOption.ReplaceExisting);
            await FileIO.WriteTextAsync(file, json.Stringify());
            await file.MoveAsync(folder, appId + ".json", NameCollisionOption.ReplaceExisting);
        }
    }
}
