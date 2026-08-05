namespace Anvil.Models
{
	/// <summary>
	/// One legend row for an SPC outlook: an official SPC risk / probability level with the colors and
	/// human-readable name SPC itself publishes (<c>Fill</c>/<c>Stroke</c> = SPC's own hex). The full,
	/// ordered scale per product family lives in <see cref="Services.SpcOutlookLegend"/>.
	/// <see cref="IsSignificant"/> marks the "significant severe" level, drawn as a hatch pattern (not a
	/// solid fill) both on the map and in the legend swatch.
	/// </summary>
	public record OutlookLegendEntry(string Label, string Fill, string Stroke, bool IsSignificant);
}
