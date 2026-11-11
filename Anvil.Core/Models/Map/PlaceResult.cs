namespace Anvil.Models
{
	/// <summary>Where a <see cref="PlaceResult"/> came from: the bundled gazetteer, the online geocoder the search
	/// falls back to when the gazetteer has nothing, or that geocoder answering a STREET ADDRESS (an exact spot, so
	/// the map flies in close — see <see cref="PlaceResult.FlyToZoom"/>).</summary>
	public enum PlaceSource { Offline, Online, Address }

	/// <summary>
	/// One city/town the place search can fly to. Immutable; <see cref="Display"/> is what the search box
	/// shows once the place is picked, and what the on-map pin is labelled with.
	/// </summary>
	/// <param name="State">Two-letter postal code (e.g. <c>OK</c>), or empty when the source didn't say.</param>
	/// <param name="Population">0 when unknown (every online result) — it only ranks offline matches and
	/// picks the fly-to zoom.</param>
	public sealed record PlaceResult(string Name, string State, double Latitude, double Longitude, int Population, PlaceSource Source)
	{
		/// <summary>"Moore, OK" — or just the name when there is no state.</summary>
		public string Display => State.Length > 0 ? $"{Name}, {State}" : Name;

		/// <summary>The right-hand hint on a suggestion row: the population for a gazetteer hit, or the
		/// attribution for an online one (OpenStreetMap's licence asks for it wherever its results show).</summary>
		public string Detail => Source is PlaceSource.Online or PlaceSource.Address
			? "OpenStreetMap"
			: Population >= 1000 ? $"pop {Population / 1000:N0}k" : Population > 0 ? $"pop {Population:N0}" : string.Empty;

		/// <summary>How close the fly-to lands: a big city needs more context around it than a village, and a street
		/// address is one spot, so it lands street-level.</summary>
		public double FlyToZoom => Source == PlaceSource.Address ? 15 : Population switch
		{
			>= 250_000 => 9,
			>= 20_000 => 10,
			_ => 11,
		};

		public override string ToString() => Display;
	}
}
