using System;
using Anvil.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Anvil.ViewModels
{
	/// <summary>
	/// One row of the storm-reports list: the report, whether its type is drawn (an unticked type's row dims), and its
	/// time + magnitude in the Settings → Units choices.
	/// </summary>
	public sealed class StormReportRow : ObservableObject
	{
		public StormReportRow(StormReportItem report, bool isDrawn, DateOnly? day = null)
		{
			Report = report;
			_isDrawn = isDrawn;
			_day = day;
		}

		public StormReportItem Report { get; }

		private bool _isDrawn;

		/// <summary>Its type is ticked (drawn on the map); false = the row dims.</summary>
		public bool IsDrawn { get => _isDrawn; set => SetProperty(ref _isDrawn, value); }

		private DateOnly? _day;

		/// <summary>The convective day the report belongs to (its time's DATE); null = unknown → SPC's own "20:49Z".</summary>
		public DateOnly? Day
		{
			get => _day;
			set { if (SetProperty(ref _day, value)) { OnPropertyChanged(nameof(TimeText)); } }
		}

		/// <summary>"3:49 PM" / "15:49" / "20:49Z" — Settings → Units.</summary>
		public string TimeText => _day is { } d && Report.TimeOn(d) is { } t ? UnitFormat.Clock(t) : Report.TimeText;

		/// <summary>"EF2" / "65 mph" / "1.75 in" — Settings → Units.</summary>
		public string MagText => Report.MagText;

		/// <summary>Settings → Units changed: re-word the row.</summary>
		public void RefreshUnits()
		{
			OnPropertyChanged(nameof(TimeText));
			OnPropertyChanged(nameof(MagText));
		}
	}
}
