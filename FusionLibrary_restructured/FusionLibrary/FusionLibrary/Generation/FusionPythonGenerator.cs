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
    public sealed class FusionPythonGenerator : ICADScriptGenerator
    {
        public ScriptLanguage Language => ScriptLanguage.Python;

        public GeneratedPackage Generate(CadOperationSequence ops, ScriptKind kind, ScriptMetadata meta)
        {
            var body = EmitOperations(ops);
            var files = new Dictionary<string, string>();

            string entry;
            if (kind == ScriptKind.Script)
            {
                entry = $"{meta.Name}.py";
                files[entry] = WrapAsScript(body, meta);
            }
            else
            {
                // Fusion add-in package: folder named <Name> containing
                // <Name>.py and <Name>.manifest. The caller writes the package
                // into .../Autodesk/Autodesk Fusion 360/API/AddIns/.
                entry = $"{meta.Name}/{meta.Name}.py";
                files[entry] = WrapAsAddIn(body, meta);
                files[$"{meta.Name}/{meta.Name}.manifest"] = Manifest(kind, meta);
            }

            return new GeneratedPackage
            {
                Language = Language,
                Kind = kind,
                Metadata = meta,
                EntryFile = entry,
                Files = files
            };
        }

        // -----------------------------------------------------------------
        // Operation emission — one case per IR operation type
        // -----------------------------------------------------------------

        private static string EmitOperations(CadOperationSequence ops)
        {
            var sb = new StringBuilder();
            foreach (var op in ops.Operations)
            {
                if (!string.IsNullOrWhiteSpace(op.Comment))
                    sb.AppendLine($"    # {op.Comment}");

                switch (op)
                {
                    case CreateDocumentOp o:
                        sb.AppendLine("    doc = app.documents.add(adsk.core.DocumentTypes.FusionDesignDocumentType)");
                        if (!string.IsNullOrEmpty(o.Name))
                            sb.AppendLine($"    doc.name = {Py(o.Name)}");
                        sb.AppendLine("    design = adsk.fusion.Design.cast(app.activeProduct)");
                        sb.AppendLine("    root = design.rootComponent");
                        break;

                    case OpenDocumentOp o:
                        sb.AppendLine($"    _import_mgr = app.importManager");
                        sb.AppendLine($"    _opts = _import_mgr.createFusionArchiveImportOptions({Py(o.Path)})");
                        sb.AppendLine($"    doc = _import_mgr.importToNewDocument(_opts)");
                        sb.AppendLine("    design = adsk.fusion.Design.cast(app.activeProduct)");
                        sb.AppendLine("    root = design.rootComponent");
                        break;

                    case SaveDocumentOp o:
                        sb.AppendLine($"    app.activeDocument.save({Py(o.Description ?? "DWM update")})");
                        break;

                    case SetParameterOp o:
                        sb.AppendLine("    design = adsk.fusion.Design.cast(app.activeProduct)");
                        sb.AppendLine($"    _p = design.userParameters.itemByName({Py(o.ParameterName)})");
                        if (o.CreateIfMissing)
                        {
                            sb.AppendLine("    if _p is None:");
                            sb.AppendLine($"        _v = adsk.core.ValueInput.createByString({Py(o.Expression)})");
                            sb.AppendLine($"        _p = design.userParameters.add({Py(o.ParameterName)}, _v, {Py(o.Unit ?? "")}, '')");
                            sb.AppendLine("    else:");
                            sb.AppendLine($"        _p.expression = {Py(o.Expression)}");
                        }
                        else
                        {
                            sb.AppendLine($"    if _p is None: raise RuntimeError('Parameter not found: ' + {Py(o.ParameterName)})");
                            sb.AppendLine($"    _p.expression = {Py(o.Expression)}");
                        }
                        break;

                    case CreateSketchOp o:
                        sb.AppendLine($"    _plane = {PlaneRef(o.Plane)}");
                        sb.AppendLine($"    sketches[{Py(o.SketchId)}] = root.sketches.add(_plane)");
                        break;

                    case SketchRectangleOp o:
                        sb.AppendLine($"    _sk = sketches[{Py(o.SketchId)}]");
                        sb.AppendLine($"    _hw, _hh = {F(o.WidthCm)} / 2.0, {F(o.HeightCm)} / 2.0");
                        sb.AppendLine("    _sk.sketchCurves.sketchLines.addTwoPointRectangle(");
                        sb.AppendLine("        adsk.core.Point3D.create(-_hw, -_hh, 0),");
                        sb.AppendLine("        adsk.core.Point3D.create(_hw, _hh, 0))");
                        break;

                    case ExtrudeOp o:
                        sb.AppendLine($"    _sk = sketches[{Py(o.SketchId)}]");
                        sb.AppendLine("    _prof = _sk.profiles.item(0)");
                        sb.AppendLine("    _ext_in = root.features.extrudeFeatures.createInput(");
                        sb.AppendLine("        _prof, adsk.fusion.FeatureOperations.NewBodyFeatureOperation)");
                        sb.AppendLine($"    _dist = adsk.core.ValueInput.createByReal({F(o.DistanceCm)})");
                        sb.AppendLine(o.Symmetric
                            ? "    _ext_in.setSymmetricExtent(_dist, True)"
                            : "    _ext_in.setDistanceExtent(False, _dist)");
                        sb.AppendLine("    root.features.extrudeFeatures.add(_ext_in)");
                        break;

                    case ExportOp o:
                        sb.AppendLine("    design = adsk.fusion.Design.cast(app.activeProduct)");
                        sb.AppendLine("    _em = design.exportManager");
                        sb.AppendLine(o.Format switch
                        {
                            ExportFormat.Step => $"    _eo = _em.createSTEPExportOptions({Py(o.OutputPath)})",
                            ExportFormat.Fbx  => $"    _eo = _em.createFBXExportOptions({Py(o.OutputPath)})",
                            ExportFormat.Obj  => $"    _eo = _em.createOBJExportOptions({Py(o.OutputPath)})",
                            ExportFormat.Stl  => $"    _eo = _em.createSTLExportOptions(root, {Py(o.OutputPath)})",
                            _ => throw new NotSupportedException($"Export format {o.Format}")
                        });
                        sb.AppendLine("    _em.execute(_eo)");
                        sb.AppendLine($"    outputs.append({Py(o.OutputPath)})");
                        break;

                    case RawCodeOp o when o.Language == ScriptLanguage.Python:
                        foreach (var line in o.Source.Replace("\r\n", "\n").Split('\n'))
                            sb.AppendLine("    " + line);
                        break;

                    case RawCodeOp o:
                        throw new InvalidOperationException(
                            $"RawCodeOp targets {o.Language}, but this generator emits Python.");

                    default:
                        throw new NotSupportedException(
                            $"FusionPythonGenerator has no emitter for '{op.Kind}'.");
                }
                sb.AppendLine();
            }
            return sb.ToString();
        }

        // -----------------------------------------------------------------
        // Templates
        // -----------------------------------------------------------------

        private static string WrapAsScript(string body, ScriptMetadata meta) => $@"# {meta.Name} — {meta.Description}
# Generated by DWM FusionLibrary (Python track) v{meta.Version}
import adsk.core, adsk.fusion, traceback

def run(context):
    ui = None
    try:
        app = adsk.core.Application.get()
        ui = app.userInterface
        sketches = {{}}
        outputs = []

{body}
    except:
        if ui:
            ui.messageBox('DWM script failed:\n{{}}'.format(traceback.format_exc()))
        raise
";

        private static string WrapAsAddIn(string body, ScriptMetadata meta) => $@"# {meta.Name} — {meta.Description}
# Generated by DWM FusionLibrary (Python track) v{meta.Version}
import adsk.core, adsk.fusion, traceback

_app = None

def _dwm_main():
    app = adsk.core.Application.get()
    sketches = {{}}
    outputs = []

{body}

def run(context):
    global _app
    try:
        _app = adsk.core.Application.get()
        _dwm_main()
    except:
        ui = _app.userInterface if _app else None
        if ui:
            ui.messageBox('DWM add-in failed:\n{{}}'.format(traceback.format_exc()))
        raise

def stop(context):
    pass
";

        private static string Manifest(ScriptKind kind, ScriptMetadata meta) =>
            JsonConvert.SerializeObject(new
            {
                autodeskProduct = "Fusion360",
                type = kind == ScriptKind.AddIn ? "addin" : "script",
                author = meta.Author,
                description = new Dictionary<string, string> { [""] = meta.Description },
                supportedOS = "windows|mac",
                editEnabled = true,
                version = meta.Version
            }, Formatting.Indented);

        // -----------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------

        /// <summary>Python string literal with escaping.</summary>
        private static string Py(string s) =>
            "'" + s.Replace("\\", "\\\\").Replace("'", "\\'") + "'";

        /// <summary>Invariant-culture float literal (no locale commas).</summary>
        private static string F(double d) =>
            d.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

        private static string PlaneRef(string plane) => plane.ToUpperInvariant() switch
        {
            "XY" => "root.xYConstructionPlane",
            "XZ" => "root.xZConstructionPlane",
            "YZ" => "root.yZConstructionPlane",
            _ => throw new ArgumentException($"Unknown plane '{plane}'. Use XY, XZ, or YZ.")
        };
    }
}
