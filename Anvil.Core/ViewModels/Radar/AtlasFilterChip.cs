namespace Anvil.ViewModels
{
	/// <summary>
	/// How the Atlas's "All sites" section is ordered. ⚠️ It orders that section ONLY — Home and Favorites
	/// keep the order they were pinned in, because their whole point is that the user put them there.
	/// </summary>
	public enum AtlasSortMode
	{
		/// <summary>By ICAO. The DEFAULT, and what the list has always done — it's the id people scan for.</summary>
		Icao,
		/// <summary>By site name, so "Norman" and "Wichita" read alphabetically.</summary>
		Name,
		/// <summary>Closest to the user-location marker first. Unavailable without one.</summary>
		Nearest,
	}

	/// <summary>Which filter group a chip stands for — what its ✕ clears.</summary>
	public enum AtlasFilterKind
	{
		Network,
		Status,
		Region,
		State,
		Sort,
	}

	/// <summary>
	/// One active filter, shown as a removable chip on the Atlas's feedback row.
	/// </summary>
	/// <remarks>
	/// ⚠️ A chip stands for a whole GROUP, not one ticked box: three networks read "NEXRAD +2", one chip, so
	/// the row can't grow past five however many boxes are ticked. Its ✕ therefore clears the group.
	/// <para>⚠️ Only filters hidden inside the flyout get a chip. The Favorites checkbox and the search box
	/// are visible and already show their own state — a chip for either would be the same fact twice.</para>
	/// <para>Sort gets a chip even though it isn't a filter: it appears only when it ISN'T the default, and
	/// "Clear all" deliberately leaves it alone (see <see cref="RadarAtlasViewModel.ClearAllFilters"/>).</para>
	/// </remarks>
	public sealed record AtlasFilterChip(AtlasFilterKind Kind, string Label);
}
