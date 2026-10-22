using Anvil.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Anvil.ViewModels
{
	/// <summary>
	/// One row of the Storm reports section's list: the report + whether its type is DRAWN. Every report of the day
	/// lists (the user's call, 2026-10-21 — the list used to follow the ticks); an unticked type's rows only DIM.
	/// </summary>
	public sealed class StormReportRow : ObservableObject
	{
		public StormReportRow(StormReportItem report, bool isDrawn)
		{
			Report = report;
			_isDrawn = isDrawn;
		}

		public StormReportItem Report { get; }

		private bool _isDrawn;

		/// <summary>Its type is ticked (drawn on the map); false = the row dims.</summary>
		public bool IsDrawn { get => _isDrawn; set => SetProperty(ref _isDrawn, value); }
	}
}
