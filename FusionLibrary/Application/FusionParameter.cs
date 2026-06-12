// FusionParameter.cs
// Immutable data class representing one Fusion 360 user parameter.
// Implements ICADParameter from CAD_Library — no HTTP calls here,
// populated by FusionParameterCollection from the add-in response.

using CAD;

namespace Fusion.Application
{
    public sealed class FusionParameter : ICADParameter
    {
        public string Name { get; init; } = string.Empty;

        /// <summary>Unit-carrying expression, e.g. "120 mm" or "width * 2".</summary>
        public string Expression { get; init; } = string.Empty;

        /// <summary>Value in Fusion internal units (centimetres / radians).</summary>
        public double Value { get; init; }

        public string Unit { get; init; } = string.Empty;

        public string Comment { get; init; } = string.Empty;

        public override string ToString() =>
            $"FusionParameter({Name} = {Expression}  [{Value} internal])";
    }
}
