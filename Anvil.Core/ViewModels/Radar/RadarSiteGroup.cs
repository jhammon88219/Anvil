using System.Collections.Generic;

namespace Anvil.ViewModels
{
	/// <summary>
	/// One SECTION of the Radar Atlas's grouped list (Home / Favorites / All sites), the shape a grouped
	/// CollectionViewSource wants: the rows themselves plus a header. A plain List, not observable — the
	/// Atlas rebuilds every section wholesale on each filter or favorites change, so a group never
	/// mutates in place.
	/// </summary>
	public sealed class RadarSiteGroup : List<RadarSiteRow>
	{
		public RadarSiteGroup(string header, IEnumerable<RadarSiteRow> rows) : base(rows) => Header = header;

		/// <summary>Section header, e.g. "Favorites · 3".</summary>
		public string Header { get; }
	}
}
