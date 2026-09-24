namespace Anvil.Models
{
	/// <summary>
	/// Outcome of building the NWS damage-survey overlay for one replay window (see
	/// <see cref="Anvil.Services.IDamageSurveyService.EnsureWindowAsync"/>). The per-layer counts are what
	/// the window file actually holds — tornado features whose time span overlaps the window — and drive the
	/// section's rows. A valid-but-empty window (no surveyed tornado in it) is <see cref="Found"/> = true with
	/// zero counts and no error.
	/// </summary>
	/// <param name="Found">True if the window file on disk reflects this window (fresh or last-known-good
	/// day caches), even if it holds nothing.</param>
	/// <param name="Areas">Damage polygons (the EF-contour swaths some offices draw).</param>
	/// <param name="Tracks">Tornado track centerlines — one per surveyed tornado.</param>
	/// <param name="Points">Individual survey damage points.</param>
	/// <param name="Error">A human-readable failure reason, or null on success.</param>
	public sealed record DamageSurveyResult(
		bool Found,
		int Areas,
		int Tracks,
		int Points,
		string? Error);
}
