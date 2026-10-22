using System;
using System.Globalization;
using Anvil.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Anvil.ViewModels
{
	/// <summary>
	/// One row of the NowCast Warnings section's day list (<see cref="WarningDayList"/>): issued · place · until/ended ·
	/// tag. Kept across refreshes by <see cref="Key"/> (RowSync) and UPDATED in place — a warning that ends changes its
	/// text and dims, it is not a new row. Times are LOCAL, like the tiles and the card.
	/// </summary>
	public sealed class WarningListRow : ObservableObject
	{
		public WarningListRow(string key, WarningDayEntry entry, bool isDrawn)
		{
			Key = key;
			_entry = entry;
			_isDrawn = isDrawn;
		}

		public string Key { get; }

		private WarningDayEntry _entry;

		/// <summary>The warning as last built; setting it re-raises every text.</summary>
		public WarningDayEntry Entry
		{
			get => _entry;
			set
			{
				if (_entry == value) { return; }
				_entry = value;
				OnPropertyChanged(string.Empty); // every derived text + Dim
			}
		}

		public WarningTarget Target => _entry.Target;

		public string Phenom => _entry.Target.Phenom;

		/// <summary>"11:52 AM" — when the event was first issued.</summary>
		public string TimeText => Clock(_entry.Target.Sent);

		/// <summary>"Cleveland, OK".</summary>
		public string PlaceText => _entry.Target.Place.Length > 0 ? _entry.Target.Place : "—";

		/// <summary>"until 12:30 PM" · "ended 9:45 AM" · "cancelled".</summary>
		public string StatusText => _entry.InEffect
			? (_entry.Target.Ends != default ? "until " + Clock(_entry.Target.Ends) : "in effect")
			: _entry.EndedAt is { } t ? "ended " + Clock(t) : "cancelled";

		/// <summary>The damage-threat tag: "Emergency" (TO/FF catastrophic), "Destructive" (SV), "PDS" (TO considerable),
		/// "Considerable" (SV/FF); "" for a base warning.</summary>
		public string TagText => (_entry.Target.Tier, _entry.Target.Phenom) switch
		{
			(2, "SV") => "Destructive",
			(2, _) => "Emergency",
			(1, "TO") => "PDS",
			(1, _) => "Considerable",
			_ => string.Empty,
		};

		public bool InEffect => _entry.InEffect;

		private bool _isDrawn;

		/// <summary>Its type is ticked (drawn on the map).</summary>
		public bool IsDrawn
		{
			get => _isDrawn;
			set { if (SetProperty(ref _isDrawn, value)) { OnPropertyChanged(nameof(Dim)); } }
		}

		/// <summary>The row dims when it has ended OR its type isn't drawn.</summary>
		public bool Dim => !_entry.InEffect || !_isDrawn;

		private static string Clock(DateTimeOffset t) =>
			t.ToLocalTime().ToString("h:mm tt", CultureInfo.CurrentCulture);
	}
}
