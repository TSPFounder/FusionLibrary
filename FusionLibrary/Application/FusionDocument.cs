// FusionDocument.cs
// Implements ICADDocument against the DWM Fusion add-in HTTP API.
// Represents the currently active Fusion 360 design document.
// Vended by FusionApplication; constructed with a hydrated parameter list.

using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CAD;
using CAD.Scripting;
using Newtonsoft.Json;

namespace Fusion.Application
{
    public sealed class FusionDocument : ICADDocument
    {
        private readonly HttpClient _http;
        private readonly Uri _baseUri;

        // ------------------------------------------------------------------
        // ICADDocument
        // ------------------------------------------------------------------

        public string Name { get; private set; }
        public string Id   { get; private set; }

        public ICADParameterCollection Parameters { get; }

        internal FusionDocument(
            string name,
            string id,
            HttpClient http,
            Uri baseUri,
            FusionParameterCollection parameters)
        {
            Name      = name;
            Id        = id;
            _http     = http;
            _baseUri  = baseUri;
            Parameters = parameters;
        }

        public async Task SaveAsync(
            string? description = null, CancellationToken ct = default)
        {
            var payload = JsonConvert.SerializeObject(
                new { description = description ?? "DWM update" });
            using var resp = await _http.PostAsync(
                new Uri(_baseUri, "/documents/active/save"),
                new StringContent(payload, Encoding.UTF8, "application/json"),
                ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"Save failed: HTTP {(int)resp.StatusCode} — {body}");
        }

        public async Task<string> ExportAsync(
            ExportFormat format,
            string outputPath,
            CancellationToken ct = default)
        {
            var payload = JsonConvert.SerializeObject(new
            {
                format     = format.ToString().ToLowerInvariant(),
                outputPath
            });
            using var resp = await _http.PostAsync(
                new Uri(_baseUri, "/documents/active/export"),
                new StringContent(payload, Encoding.UTF8, "application/json"),
                ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"Export failed: HTTP {(int)resp.StatusCode} — {body}");

            var result = JsonConvert.DeserializeAnonymousType(body,
                new { success = false, outputPath = "" })
                ?? throw new InvalidOperationException("Empty export response.");

            if (!result.success)
                throw new InvalidOperationException(
                    $"Export reported failure: {body}");

            return result.outputPath ?? outputPath;
        }

        public async Task ExecuteScriptAsync(
            GeneratedPackage script, CancellationToken ct = default)
        {
            if (script.Language != ScriptLanguage.Python)
                throw new NotSupportedException(
                    $"FusionDocument.ExecuteScriptAsync only supports Python scripts. " +
                    $"Got {script.Language}.");

            if (script.Kind != ScriptKind.Script)
                throw new NotSupportedException(
                    "ExecuteScriptAsync runs live Scripts only. " +
                    "For AddIns, use GeneratedPackage.WriteTo() and load in Fusion.");

            var payload = JsonConvert.SerializeObject(
                new { source = script.EntrySource });
            using var resp = await _http.PostAsync(
                new Uri(_baseUri, "/scripts/execute"),
                new StringContent(payload, Encoding.UTF8, "application/json"),
                ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"Script execution transport error: HTTP {(int)resp.StatusCode} — {body}");

            var result = JsonConvert.DeserializeAnonymousType(body,
                new { success = false, output = (string?)null, error = (string?)null })
                ?? throw new InvalidOperationException("Empty script execution response.");

            if (!result.success)
                throw new InvalidOperationException(
                    $"Script execution failed in Fusion:\n{result.error}\n\nOutput:\n{result.output}");
        }

        // ------------------------------------------------------------------
        // Convenience: set a parameter and return the refreshed document
        // ------------------------------------------------------------------

        /// <summary>
        /// Sets a parameter by name, waits for Fusion to regenerate,
        /// refreshes the local parameter cache, and returns the updated
        /// parameter value. Shorthand for Parameters.SetAsync.
        /// </summary>
        public Task<ICADParameter> SetParameterAsync(
            string name, string expression, CancellationToken ct = default)
            => Parameters.SetAsync(name, expression, ct);
    }
}
