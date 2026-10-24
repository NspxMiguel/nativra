// Only the platform services used by the linked Steam sources are substituted.
// ABI implementations under test are the production files.
using System.Collections.Generic;

namespace Windows.Storage
{
    internal sealed class ApplicationData
    {
        internal static ApplicationData Current { get; } = new ApplicationData();
        internal ApplicationData LocalFolder => this;
        internal string Path => System.IO.Path.GetTempPath();
    }
}

namespace Kiosk.Steam
{
    internal static class SteamStats
    {
        internal sealed class Friend
        {
            internal ulong Id { get; set; }
            internal uint AppId { get; set; }
            internal string Name { get; set; }
            internal int State { get; set; }
        }
        internal static string PersonaName => null;
        internal static List<Friend> Friends() => new List<Friend>();
        internal static Friend FindFriend(ulong id) => null;
    }
}

namespace Kiosk.Native
{
    internal static class LoaderStubs
    {
        internal static string GameFolder => null;
    }
}
