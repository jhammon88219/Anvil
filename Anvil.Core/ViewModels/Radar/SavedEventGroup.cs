using System.Collections.Generic;

namespace Anvil.ViewModels
{
	/// <summary>One section of the Atlas's Past events list (the grouped CollectionViewSource's source) —
	/// the same shape as <see cref="RadarSiteGroup"/> on the Radar sites tab.</summary>
	public sealed class SavedEventGroup : List<SavedEventRow>
	{
		public SavedEventGroup(string header, IEnumerable<SavedEventRow> rows) : base(rows) => Header = header;

		/// <summary>Section header, e.g. "Tornado · 10".</summary>
		public string Header { get; }
	}
}
