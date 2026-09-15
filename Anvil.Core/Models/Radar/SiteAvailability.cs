namespace Anvil.Models
{
	/// <summary>
	/// What the app currently KNOWS about a radar site's data feed. <see cref="Unknown"/> until the first
	/// availability pass lands (and again right after leaving PastCast, until the live pass does), so a site
	/// is never shown green just because nobody has checked it yet.
	/// </summary>
	public enum SiteAvailability
	{
		Unknown,
		Online,
		Offline,
	}
}
