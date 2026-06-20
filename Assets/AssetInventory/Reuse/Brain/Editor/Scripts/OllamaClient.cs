using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityEngine;

namespace Brain
{
    /// <summary>
    /// Lightweight HTTP client for the Ollama REST API.
    /// Replaces OllamaSharp to eliminate third-party DLL dependencies.
    /// Thread-safe: the underlying HttpClient supports concurrent requests.
    /// </summary>
    internal sealed class OllamaClient : IDisposable
    {
        private readonly HttpClient _http;
        private readonly bool _ownsClient;

        internal HttpClient Http => _http;

        public OllamaClient(string serviceUrl) : this(CreateHttpClient(serviceUrl), true) { }

        public OllamaClient(HttpClient httpClient, bool ownsClient = false)
        {
            _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _ownsClient = ownsClient;
        }

        /// <summary>
        /// Creates an optimized HttpClient for Ollama communication.
        /// Applies IPv4 forcing, Nagle disable, ServicePoint tuning, and infinite timeout.
        /// </summary>
        public static HttpClient CreateHttpClient(string serviceUrl, bool debugLogging = false)
        {
            string url = serviceUrl ?? string.Empty;
            Uri baseUri = new Uri(url);

            // Force IPv4: on Windows, "localhost" resolves to both ::1 (IPv6) and 127.0.0.1.
            // Ollama only binds IPv4, so the .NET stack wastes time on the IPv6 attempt.
            if (string.Equals(baseUri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
            {
                UriBuilder ub = new UriBuilder(baseUri) { Host = "127.0.0.1" };
                baseUri = ub.Uri;
            }

            // Configure ServicePoint: disable Nagle (biggest perf win for large base64 bodies),
            // disable Expect100Continue, bump connection limit.
            try
            {
                ServicePoint sp = ServicePointManager.FindServicePoint(baseUri);
                sp.UseNagleAlgorithm = false;
                sp.Expect100Continue = false;
                sp.ConnectionLimit = 16;
            }
            catch { /* ServicePointManager may be unavailable on some runtimes; non-fatal. */ }

            HttpClientHandler inner = new HttpClientHandler
            {
                UseProxy = false,
                AutomaticDecompression = DecompressionMethods.None,
                AllowAutoRedirect = false
            };

            HttpClient http = new HttpClient(debugLogging ? (HttpMessageHandler)new TrafficLogger(inner) : inner)
            {
                BaseAddress = baseUri,
                Timeout = System.Threading.Timeout.InfiniteTimeSpan
            };
            http.DefaultRequestHeaders.ConnectionClose = false;
            http.DefaultRequestHeaders.ExpectContinue = false;
            return http;
        }

        public async Task<bool> IsRunningAsync(CancellationToken ct = default)
        {
            try
            {
                using (HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, string.Empty))
                using (HttpResponseMessage resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false))
                {
                    string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    return !string.IsNullOrWhiteSpace(body);
                }
            }
            catch
            {
                return false;
            }
        }

        public async Task<string> GetVersionAsync(CancellationToken ct = default)
        {
            using (HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, "api/version"))
            using (HttpResponseMessage resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                string json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                OllamaVersionResponse ver = JsonConvert.DeserializeObject<OllamaVersionResponse>(json);
                return ver?.Version ?? string.Empty;
            }
        }

        public async Task<OllamaModelListResponse> ListModelsAsync(CancellationToken ct = default)
        {
            using (HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, "api/tags"))
            using (HttpResponseMessage resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                string json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                return JsonConvert.DeserializeObject<OllamaModelListResponse>(json);
            }
        }

        /// <summary>
        /// Pulls (downloads) a model. Streams NDJSON progress lines and invokes the callback for each.
        /// </summary>
        public async Task PullModelAsync(string name, Action<OllamaPullStatus> statusCallback, CancellationToken ct = default)
        {
            string body = JsonConvert.SerializeObject(new { model = name, stream = true });
            using (HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, "api/pull"))
            {
                req.Content = new StringContent(body, Encoding.UTF8, "application/json");
                using (HttpResponseMessage resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                {
                    resp.EnsureSuccessStatusCode();
                    using (Stream stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (StreamReader reader = new StreamReader(stream))
                    {
                        string line;
                        while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
                        {
                            if (ct.IsCancellationRequested) break;
                            if (string.IsNullOrWhiteSpace(line)) continue;

                            OllamaPullStatus status = JsonConvert.DeserializeObject<OllamaPullStatus>(line);
                            if (!string.IsNullOrEmpty(status?.Error))
                                throw new HttpRequestException($"Ollama pull error: {status.Error}");

                            statusCallback?.Invoke(status);
                        }
                    }
                }
            }
        }

        public async Task DeleteModelAsync(string name, CancellationToken ct = default)
        {
            string body = JsonConvert.SerializeObject(new OllamaDeleteRequest { Model = name });
            using (HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Delete, "api/delete"))
            {
                req.Content = new StringContent(body, Encoding.UTF8, "application/json");
                using (HttpResponseMessage resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false))
                {
                    resp.EnsureSuccessStatusCode();
                }
            }
        }

        public async Task<OllamaGenerateResponse> GenerateAsync(OllamaGenerateRequest request, CancellationToken ct = default)
        {
            request.Stream = false;
            string body = JsonConvert.SerializeObject(request);
            using (StringContent content = new StringContent(body, Encoding.UTF8, "application/json"))
            using (HttpResponseMessage resp = await _http.PostAsync("api/generate", content, ct).ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                string json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                return JsonConvert.DeserializeObject<OllamaGenerateResponse>(json);
            }
        }

        public async Task<OllamaChatResponse> ChatAsync(OllamaChatRequest request, CancellationToken ct = default)
        {
            request.Stream = false;
            string body = JsonConvert.SerializeObject(request);
            using (StringContent content = new StringContent(body, Encoding.UTF8, "application/json"))
            using (HttpResponseMessage resp = await _http.PostAsync("api/chat", content, ct).ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                string json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                return JsonConvert.DeserializeObject<OllamaChatResponse>(json);
            }
        }

        public void Dispose()
        {
            if (_ownsClient) _http.Dispose();
        }

        /// <summary>
        /// Optional HTTP traffic logger for diagnostics.
        /// </summary>
        internal sealed class TrafficLogger : DelegatingHandler
        {
            public TrafficLogger(HttpMessageHandler inner) : base(inner) { }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
                long contentLen = request.Content?.Headers?.ContentLength ?? -1;
                Debug.Log($"[Ollama HTTP] -> {request.Method} {request.RequestUri} ({contentLen} bytes)");
                HttpResponseMessage response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
                Debug.Log($"[Ollama HTTP] <- {(int)response.StatusCode} {response.StatusCode} (headers in {sw.ElapsedMilliseconds} ms)");
                return response;
            }
        }
    }
}
