namespace Anvil.Models
{
	/// <summary>
	/// One site's result from a live availability pass, reported the moment THAT site's probe lands — so the
	/// map can cascade grey → colour site by site instead of flipping every marker when the slowest probe ends.
	/// </summary>
	public readonly record struct SiteCheckResult(string SiteId, bool IsLive);
}
