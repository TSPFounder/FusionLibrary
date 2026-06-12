// FusionParameterCollection.cs
// Implements ICADParameterCollection against the DWM Fusion add-in HTTP API.
// Hydrated by FusionDocument; individual parameter mutations go through
// PATCH /documents/active/parameters/{name}.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CAD;
using Newtonsoft.Json;

namespace Fusion.Application
{
    public sealed class FusionParameterCollection : ICADParameterCollection
    {
        private readonly HttpClient _http;
        private readonly Uri _baseUri;
        private List<FusionParameter> _cache;

        internal FusionParameterCollection(
            HttpClient http,
            Uri baseUri,
            IEnumerable<FusionParameter> initialValues)
        {
            _http    = http;
            _baseUri = baseUri;
            _cache   = new List<FusionParameter>(initialValues);
        }

        // ------------------------------------------------------------------
        // ICADParameterCollection
        // ------------------------------------------------------------------

        public ICADParameter? FindByName(string name) =>
            _cache.Find(p => string.Equals(p.Name, name, StringComparison.Ordinal));

        public async Task<ICADParameter> SetAsync(
            string name, string expression, CancellationToken ct = default)
        {
            var payload = JsonConvert.SerializeObject(new { expression });
            using var resp = await _http.PatchAsync(
                new Uri(_baseUri, $"/documents/active/parameters/{Uri.EscapeDataString(name)}"),
                new StringContent(payload, Encoding.UTF8, "application/json"),
                ct);

            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"Failed to set parameter '{name}': HTTP {(int)resp.StatusCode} — {body}");

            var result = JsonConvert.DeserializeAnonymousType(body,
                new { name = "", expression = "", value = 0.0, unit = "" })
                ?? throw new InvalidOperationException("Empty response from add-in.");

            var updated = new FusionParameter
            {
                Name       = result.name,
                Expression = result.expression,
                Value      = result.value,
                Unit       = result.unit,
                Comment    = FindByName(name) is FusionParameter fp ? fp.Comment : string.Empty
            };

            // Update local cache
            int idx = _cache.FindIndex(
                p => string.Equals(p.Name, name, StringComparison.Ordinal));
            if (idx >= 0) _cache[idx] = updated;
            else          _cache.Add(updated);

            return updated;
        }

        public async Task<ICADParameter> AddAsync(
            string name, string expression, string unit,
            string comment = "", CancellationToken ct = default)
        {
            // Use SetAsync with CreateIfMissing semantics: send the expression
            // via the add-in's PATCH endpoint which creates when missing
            // (the add-in's SetParameterOp supports createIfMissing).
            // For an explicit create path, a future add-in endpoint can be
            // added; for now SetAsync covers the round-trip.
            return await SetAsync(name, expression, ct);
        }

        // ------------------------------------------------------------------
        // IEnumerable<ICADParameter>
        // ------------------------------------------------------------------

        public IEnumerator<ICADParameter> GetEnumerator() =>
            _cache.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        // ------------------------------------------------------------------
        // Internal refresh (called by FusionDocument after model regeneration)
        // ------------------------------------------------------------------

        internal async Task RefreshAsync(CancellationToken ct = default)
        {
            using var resp = await _http.GetAsync(
                new Uri(_baseUri, "/documents/active/parameters"), ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            resp.EnsureSuccessStatusCode();

            var result = JsonConvert.DeserializeAnonymousType(body,
                new { parameters = Array.Empty<ParameterDto>() })
                ?? throw new InvalidOperationException("Empty parameter list response.");

            _cache = new List<FusionParameter>(
                result.parameters.Length);
            foreach (var dto in result.parameters)
                _cache.Add(new FusionParameter
                {
                    Name       = dto.name,
                    Expression = dto.expression,
                    Value      = dto.value,
                    Unit       = dto.unit,
                    Comment    = dto.comment
                });
        }

        // DTO used only for JSON deserialization
        private sealed class ParameterDto
        {
            public string name       { get; set; } = string.Empty;
            public string expression { get; set; } = string.Empty;
            public double value      { get; set; }
            public string unit       { get; set; } = string.Empty;
            public string comment    { get; set; } = string.Empty;
        }
    }
}
