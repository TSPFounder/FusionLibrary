// FusionPythonGenerator.cs
// The Python track of FusionLibrary's script-generation subsystem.
//
//  - FusionPythonGenerator : ICADScriptGenerator
//      Renders a CadOperationSequence into Fusion 360 Python. ScriptKind.Script
//      produces a single in-memory source suitable for live execution through
//      the DWM add-in's POST /scripts/execute. ScriptKind.AddIn additionally
//      produces the standard Fusion add-in folder contents (<Name>.py +
//      <Name>.manifest) for installation into the AddIns directory.
//
//  - FusionPythonHttpRunner : ICADScriptRunner
//      Executes generated Python through the DWM-Fusion-AddIn local HTTP API.

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CAD.Scripting;
using Newtonsoft.Json;

namespace Fusion.Scripting
{

    /// <summary>
    /// Executes generated Python live, in-process, via the DWM Fusion add-in's
    /// POST /scripts/execute endpoint. Requires the add-in to be running.
    /// </summary>
    public sealed class FusionPythonHttpRunner : ICADScriptRunner, IDisposable
    {
        private readonly HttpClient _http;
        private readonly Uri _baseUri;

        public ScriptLanguage Language => ScriptLanguage.Python;

        public FusionPythonHttpRunner(string baseUrl = "http://127.0.0.1:18750", HttpClient? http = null)
        {
            _baseUri = new Uri(baseUrl, UriKind.Absolute);
            _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        }

        public async Task<ScriptResult> ExecuteAsync(GeneratedPackage package, CancellationToken ct = default)
        {
            if (package.Language != ScriptLanguage.Python)
                throw new ArgumentException("FusionPythonHttpRunner only executes Python packages.");
            if (package.Kind != ScriptKind.Script)
                throw new ArgumentException(
                    "Live execution is for ScriptKind.Script. Add-ins must be installed via " +
                    "GeneratedPackage.WriteTo(<Fusion AddIns directory>) and loaded in Fusion.");

            var payload = JsonConvert.SerializeObject(new { source = package.EntrySource });
            var started = DateTime.UtcNow;

            using var resp = await _http.PostAsync(
                new Uri(_baseUri, "/scripts/execute"),
                new StringContent(payload, Encoding.UTF8, "application/json"),
                ct);

            var bodyText = await resp.Content.ReadAsStringAsync(ct);
            var elapsed = DateTime.UtcNow - started;

            if (!resp.IsSuccessStatusCode)
                return new ScriptResult { Success = false, Error = bodyText, Elapsed = elapsed };

            var body = JsonConvert.DeserializeAnonymousType(bodyText,
                new { success = false, output = (string?)null, error = (string?)null });

            return new ScriptResult
            {
                Success = body?.success ?? false,
                Output = body?.output,
                Error = body?.error,
                Elapsed = elapsed
            };
        }

        public void Dispose() => _http.Dispose();
    }
}
