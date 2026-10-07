namespace Anvil.Models
{
	/// <summary>
	/// The HIGHEST solid category an outlook actually contains (not its product's full scale), and the colour to
	/// draw it in — the FEED's own fill when the feature carries one, else the catalog's (the feed wins).
	/// The ForeCast window's header square + readout (<c>OutlookViewModel.HeaderSquareFill</c>).
	/// </summary>
	public sealed record OutlookPeak(SpcRiskLevel Level, string Fill);
}
