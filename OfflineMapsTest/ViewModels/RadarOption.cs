using OfflineMapsTest.Models;

namespace OfflineMapsTest.ViewModels
{
	/// <summary>
	/// One entry in the radar site selector: a display label plus the site it selects.
	/// A null <see cref="Site"/> is the "None" entry, which clears the radar layer.
	/// </summary>
	public record RadarOption(string Label, RadarSite? Site);

	/// <summary>
	/// One entry in the radar Product (moment) selector — the C# mirror of the JS registry in
	/// <c>radar-products.js</c>. <paramref name="Id"/> is the product id passed to
	/// <c>window.setRadarProduct</c> (must match the JS ids); <paramref name="Label"/> is the combo text;
	/// <paramref name="IsLazy"/> marks a product built lazily (velocity — the only one that dealiases),
	/// so its frames aren't display-ready until built (drives the scrubber's "still loading" gate).
	/// </summary>
	public record RadarProductOption(string Id, string Label, bool IsLazy);
}
