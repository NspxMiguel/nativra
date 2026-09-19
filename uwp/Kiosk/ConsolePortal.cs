using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.Security.Cryptography;
using Windows.Security.Cryptography.Certificates;
using Windows.Storage;
using Windows.Web.Http;
using Windows.Web.Http.Filters;

namespace Kiosk
{
    /// <summary>
    /// The console's own Device Portal, spoken to from inside the app. A
    /// sideloaded package is not allowed to enumerate or launch its neighbours
    /// through the platform API, but the portal on this same console does both
    /// — if the app is allowed to reach it at all, which is what Probe answers.
    /// </summary>
    public sealed class ConsolePortal
    {
        private readonly string baseUrl;
        private readonly string auth;
        private HttpClient http;
        private string csrf;

        private ConsolePortal(string host, int port, string user, string pass)
        {
            baseUrl = $"https://{host}:{port}";
            auth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{pass}"));
        }

        /// <summary>Reads portal.json, which xbdev sync drops into LocalState.</summary>
        public static async Task<ConsolePortal> LoadAsync()
        {
            try
            {
                var file = await ApplicationData.Current.LocalFolder
                    .TryGetItemAsync("portal.json") as StorageFile;
                if (file == null) return null;

                var text = await FileIO.ReadTextAsync(file);
                if (!JsonObject.TryParse(text, out var root)) return null;

                var host = root.GetNamedString("host", string.Empty);
                if (string.IsNullOrEmpty(host)) return null;

                return new ConsolePortal(
                    host,
                    (int)root.GetNamedNumber("port", 11443),
                    root.GetNamedString("user", string.Empty),
                    root.GetNamedString("pass", string.Empty));
            }
            catch
            {
                return null;
            }
        }

        private HttpClient Client()
        {
            if (http != null) return http;
            var filter = new HttpBaseProtocolFilter();
            // Dev Mode always presents a certificate signed by nobody.
            filter.IgnorableServerCertificateErrors.Add(ChainValidationResult.Untrusted);
            filter.IgnorableServerCertificateErrors.Add(ChainValidationResult.InvalidName);
            filter.IgnorableServerCertificateErrors.Add(ChainValidationResult.Expired);
            filter.IgnorableServerCertificateErrors.Add(ChainValidationResult.IncompleteChain);
            filter.IgnorableServerCertificateErrors.Add(ChainValidationResult.WrongUsage);
            http = new HttpClient(filter);
            http.DefaultRequestHeaders.Authorization =
                new Windows.Web.Http.Headers.HttpCredentialsHeaderValue("Basic", auth);
            return http;
        }

        /// <summary>Returns the console's name, or null when it cannot be reached.</summary>
        public async Task<string> ProbeAsync()
        {
            try
            {
                var response = await Client().GetAsync(new Uri(baseUrl + "/api/os/machinename"));
                CaptureCsrf(response);
                if (!response.IsSuccessStatusCode) return null;
                var text = await response.Content.ReadAsStringAsync();
                return JsonObject.TryParse(text, out var root)
                    ? root.GetNamedString("ComputerName", "?")
                    : "?";
            }
            catch
            {
                return null;
            }
        }

        /// <summary>The portal hands out its CSRF token as a cookie on the first GET.</summary>
        private void CaptureCsrf(HttpResponseMessage response)
        {
            if (csrf != null) return;
            if (!response.Headers.TryGetValue("Set-Cookie", out var cookie)) return;
            var index = cookie.IndexOf("CSRF-Token=", StringComparison.OrdinalIgnoreCase);
            if (index < 0) return;
            var rest = cookie.Substring(index + "CSRF-Token=".Length);
            var end = rest.IndexOfAny(new[] { ';', ',', ' ' });
            csrf = end < 0 ? rest : rest.Substring(0, end);
        }

        public async Task<List<Tuple<string, string, string>>> PackagesAsync()
        {
            var list = new List<Tuple<string, string, string>>();
            try
            {
                var response = await Client().GetAsync(
                    new Uri(baseUrl + "/api/app/packagemanager/packages"));
                CaptureCsrf(response);
                if (!response.IsSuccessStatusCode) return list;

                var text = await response.Content.ReadAsStringAsync();
                if (!JsonObject.TryParse(text, out var root)) return list;

                foreach (var value in root.GetNamedArray("InstalledPackages"))
                {
                    var item = value.GetObject();
                    list.Add(Tuple.Create(
                        item.GetNamedString("Name", string.Empty),
                        item.GetNamedString("PackageFullName", string.Empty),
                        item.GetNamedString("PackageRelativeId", string.Empty)));
                }
            }
            catch
            {
                // An unreachable portal is the caller's cue to fall back.
            }
            return list;
        }

        /// <summary>Launches any installed package, protocol or not.</summary>
        public async Task<bool> LaunchAsync(string packageFullName, string appId)
        {
            try
            {
                if (csrf == null) await ProbeAsync();

                var appParam = Uri.EscapeDataString(
                    Convert.ToBase64String(Encoding.UTF8.GetBytes(appId)));
                var pkgParam = Uri.EscapeDataString(
                    Convert.ToBase64String(Encoding.UTF8.GetBytes(packageFullName)));

                var request = new HttpRequestMessage(
                    HttpMethod.Post,
                    new Uri($"{baseUrl}/api/taskmanager/app?appid={appParam}&package={pkgParam}"));
                if (csrf != null)
                {
                    request.Headers["X-CSRF-Token"] = csrf;
                    request.Headers["Cookie"] = "CSRF-Token=" + csrf;
                }

                var response = await Client().SendRequestAsync(request);
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }
    }
}
