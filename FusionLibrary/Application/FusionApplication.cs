// FusionApplication.cs
// Implements ICADApplication against the DWM Fusion add-in HTTP API.
// This is the top-level entry point for FusionLibrary — construct one,
// call PingAsync() to verify the add-in is running, then create or open
// a document to start working.
//
// Usage:
//   await using var app = new FusionApplication();
//   if (!await app.PingAsync()) throw new Exception("Add-in not running.");
//   var doc = await app.GetActiveDocumentAsync();
//   await doc.Parameters.SetAsync("width", "120 mm");

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CAD;
using Newtonsoft.Json;

namespace Fusion.Application
{
    public sealed class FusionApplication : ICADApplication, IAsyncDisposable, IDisposable
    {
        private readonly HttpClient _http;
        private readonly Uri _baseUri;
        private readonly bool _ownsClient;

        // ------------------------------------------------------------------
        // Construction
        // ------------------------------------------------------------------

        /// <summary>
        /// Creates a FusionApplication that talks to the DWM add-in on the
        /// default local port (18750). Pass a custom baseUrl or an existing
        /// HttpClient for testing / non-default port scenarios.
        /// </summary>
        public FusionApplication(
            string baseUrl   = "http://127.0.0.1:18750",
            HttpClient? http = null)
        {
            _baseUri    = new Uri(baseUrl, UriKind.Absolute);
            _ownsClient = http is null;
            _http       = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        }

        // ------------------------------------------------------------------
        // ICADApplication
        // ------------------------------------------------------------------

        public string Version         { get; private set; } = "(not connected)";
        public string ActiveDocumentName { get; private set; } = string.Empty;

        public async Task<bool> PingAsync(CancellationToken ct = default)
        {
            try
            {
                using var resp = await _http.GetAsync(
                    new Uri(_baseUri, "/ping"), ct);
                if (!resp.IsSuccessStatusCode) return false;

                var body = await resp.Content.ReadAsStringAsync(ct);
                var result = JsonConvert.DeserializeAnonymousType(body,
                    new { success = false, fusionVersion = "", addinVersion = "" });

                if (result?.success == true)
                {
                    Version = $"Fusion {result.fusionVersion} / add-in {result.addinVersion}";
                    return true;
                }
                return false;
            }
            catch (HttpRequestException)
            {
                // Add-in not running or port not listening
                return false;
            }
        }

        public async Task<ICADDocument> CreateDocumentAsync(
            string name, CancellationToken ct = default)
        {
            var payload = JsonConvert.SerializeObject(new { name });
            using var resp = await _http.PostAsync(
                new Uri(_baseUri, "/documents"),
                new StringContent(payload, Encoding.UTF8, "application/json"),
                ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"CreateDocument failed: HTTP {(int)resp.StatusCode} — {body}");

            ActiveDocumentName = name;
            return await BuildActiveDocumentAsync(ct);
        }

        public async Task<ICADDocument> OpenDocumentAsync(
            string path, CancellationToken ct = default)
        {
            var payload = JsonConvert.SerializeObject(new { path });
            using var resp = await _http.PostAsync(
                new Uri(_baseUri, "/documents/open"),
                new StringContent(payload, Encoding.UTF8, "application/json"),
                ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"OpenDocument failed: HTTP {(int)resp.StatusCode} — {body}");

            ActiveDocumentName = System.IO.Path.GetFileNameWithoutExtension(path);
            return await BuildActiveDocumentAsync(ct);
        }

        public Task<ICADDocument> GetActiveDocumentAsync(CancellationToken ct = default)
            => BuildActiveDocumentAsync(ct);

        // ------------------------------------------------------------------
        // Internal helpers
        // ------------------------------------------------------------------

        private async Task<ICADDocument> BuildActiveDocumentAsync(CancellationToken ct)
        {
            // Fetch parameter list for the active document
            using var resp = await _http.GetAsync(
                new Uri(_baseUri, "/documents/active/parameters"), ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"Failed to fetch parameters: HTTP {(int)resp.StatusCode} — {body}");

            var result = JsonConvert.DeserializeAnonymousType(body,
                new { parameters = Array.Empty<ParameterDto>() })
                ?? throw new InvalidOperationException("Empty parameter response.");

            var fusionParams = new List<FusionParameter>(result.parameters.Length);
            foreach (var dto in result.parameters)
                fusionParams.Add(new FusionParameter
                {
                    Name       = dto.name,
                    Expression = dto.expression,
                    Value      = dto.value,
                    Unit       = dto.unit,
                    Comment    = dto.comment
                });

            var collection = new FusionParameterCollection(_http, _baseUri, fusionParams);

            return new FusionDocument(
                name       : ActiveDocumentName,
                id         : "active",   // v1 always operates on the active doc
                http       : _http,
                baseUri    : _baseUri,
                parameters : collection);
        }

        // DTO used only for deserialization
        private sealed class ParameterDto
        {
            public string name       { get; set; } = string.Empty;
            public string expression { get; set; } = string.Empty;
            public double value      { get; set; }
            public string unit       { get; set; } = string.Empty;
            public string comment    { get; set; } = string.Empty;
        }

        // ------------------------------------------------------------------
        // Disposal
        // ------------------------------------------------------------------

        public void Dispose()
        {
            if (_ownsClient) _http.Dispose();
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
