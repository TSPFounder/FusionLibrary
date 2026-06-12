// FusionScriptFactory.cs
// Implements ICADScriptFactory for Fusion 360 (Python track).
// Wraps FusionPythonGenerator to produce GeneratedPackages from a
// CadOperationSequence, following the same ICADScriptFactory abstraction
// defined in CAD_Library — mirrors IMatlabBackend / MatlabDesktopBackend.
//
// TypeScript and C++ tracks will each get their own factory; this one
// is the first and the one used by FusionPythonHttpRunner.

using CAD;
using CAD.Scripting;
using Fusion.Scripting;

namespace Fusion.Application
{
    public sealed class FusionPythonScriptFactory : ICADScriptFactory
    {
        private readonly FusionPythonGenerator _generator = new();

        public ScriptLanguage Language => ScriptLanguage.Python;

        /// <summary>
        /// Renders a CadOperationSequence into a Fusion Python script package
        /// (ScriptKind.Script) suitable for live execution via
        /// FusionDocument.ExecuteScriptAsync or FusionPythonHttpRunner.
        /// </summary>
        public GeneratedPackage CreateScript(CadOperationSequence ops, ScriptMetadata meta)
            => _generator.Generate(ops, ScriptKind.Script, meta);

        /// <summary>
        /// Renders a CadOperationSequence into a Fusion Python add-in package
        /// (ScriptKind.AddIn) — a folder containing &lt;Name&gt;.py and
        /// &lt;Name&gt;.manifest, ready to install via GeneratedPackage.WriteTo().
        /// </summary>
        public GeneratedPackage CreateAddIn(CadOperationSequence ops, ScriptMetadata meta)
            => _generator.Generate(ops, ScriptKind.AddIn, meta);
    }
}
