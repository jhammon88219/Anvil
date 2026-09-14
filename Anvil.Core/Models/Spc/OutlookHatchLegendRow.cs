namespace Anvil.Models
{
	/// <summary>
	/// One Conditional Intensity Group row in the ForeCast legend: the catalog level (swatch + name) and
	/// whether TODAY's cached outlook actually carries that group. The catalog scale lists every CIG a
	/// product can have, so without <see cref="InOutlook"/> a hidden-but-present hatch is easy to forget.
	/// </summary>
	public sealed record OutlookHatchLegendRow(SpcRiskLevel Level, bool InOutlook)
	{
		/// <summary>The row's right-hand status, worded exactly as the user asked.</summary>
		public string StatusText => InOutlook ? "In Outlook" : "Not In Outlook";
	}
}
