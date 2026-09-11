// OptiScaler Client - A frontend for managing OptiScaler installations
// Copyright (C) 2026 Agustín Montaña (Agustinm28)
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program. If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using UpscalerManager.Core.Models;

namespace UpscalerManager.Core.Services
{
    /// <summary>
    /// Centralized HTTP client factory that respects proxy configuration.
    /// All services should obtain their HttpClient from here so that proxy
    /// settings are applied consistently across the application.
    /// </summary>
    public static class NetworkService
    {
        private static readonly object _lock = new();
        private static HttpClient _httpClient = BuildClient(null);
        private static HttpClient _largeFileClient = BuildClient(null, forLargeFiles: true);

        /// <summary>Returns the shared <see cref="HttpClient"/> configured with the current proxy settings.</summary>
        public static HttpClient GetHttpClient() => _httpClient;

        /// <summary>
        /// A client for transfers measured in tens of megabytes, such as a vendor's DLSS
        /// or XeSS library.
        ///
        /// The shared client caps every request at 30 seconds, which is right for an API
        /// call and fatal for a 60 MB download on an ordinary connection. This one has no
        /// overall deadline, so <em>every</em> caller must pass a cancellation token —
        /// usually one that fires when the transfer goes idle rather than when it has
        /// simply taken a while, since "slow" and "stalled" are different problems.
        /// </summary>
        public static HttpClient GetLargeFileClient() => _largeFileClient;

        /// <summary>
        /// Reconfigures the shared <see cref="HttpClient"/> with the provided <paramref name="config"/>.
        /// The old client is disposed after the swap. In-flight requests on the old client may fail.
        /// </summary>
        public static void Configure(NetworkConfig config)
        {
            var newClient = BuildClient(config);
            var newLargeFileClient = BuildClient(config, forLargeFiles: true);
            lock (_lock)
            {
                var old = _httpClient;
                var oldLarge = _largeFileClient;
                _httpClient = newClient;
                _largeFileClient = newLargeFileClient;
                old.Dispose();
                oldLarge.Dispose();
            }
        }

        private static HttpClient BuildClient(NetworkConfig? config, bool forLargeFiles = false)
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            };

            if (config != null
                && !config.UseSystemProxy
                && !string.IsNullOrWhiteSpace(config.ProxyHost)
                && config.ProxyPort.HasValue)
            {
                try
                {
                    var scheme = string.Equals(config.ProxyType, "SOCKS5", StringComparison.OrdinalIgnoreCase)
                        ? "socks5"
                        : "http";
                    var proxyUri = new Uri($"{scheme}://{config.ProxyHost}:{config.ProxyPort}");
                    var proxy = new WebProxy(proxyUri);
                    if (config.ProxyRequiresAuth && !string.IsNullOrEmpty(config.ProxyUsername))
                    {
                        proxy.Credentials = new NetworkCredential(
                            config.ProxyUsername,
                            config.ProxyPassword ?? string.Empty);
                    }
                    handler.Proxy = proxy;
                    handler.UseProxy = true;
                }
                catch
                {
                    // Invalid proxy settings — fall back to system proxy silently.
                    handler.UseProxy = true;
                }
            }
            else
            {
                // Honor system proxy or HTTP_PROXY / HTTPS_PROXY env vars.
                handler.UseProxy = true;
            }

            var client = new HttpClient(handler, disposeHandler: true);
            // GitHub's API rejects requests without one.
            client.DefaultRequestHeaders.UserAgent.ParseAdd("UpscalerManager/1.0");
            client.Timeout = forLargeFiles ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(30);
            return client;
        }
    }
}
