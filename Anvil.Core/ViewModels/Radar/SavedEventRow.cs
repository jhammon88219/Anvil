using System;
using System.Collections.Generic;
using System.Linq;
using Anvil.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Anvil.ViewModels
{
	/// <summary>
	/// One row of the saved-event list (the PastCast window's section AND the Atlas's Past events tab): a
	/// <see cref="SavedEvent"/> dressed for display, plus the list's per-row state (picked, which leg is
	/// current, whether it opens its group) and the Atlas detail's (which leg the detail shows).
	/// </summary>
	/// <remarks>
	/// ⚠️ All times shown are LOCAL, converted from the event's UTC legs here — the same frame the
	/// Timeframe pickers use, so the row and the card never disagree about what 5:30 PM means.
	/// <para>⚠️ TWO leg indices, on purpose: <see cref="CurrentLegIndex"/> is the leg that was PICKED (loaded —
	/// the PastCast list's highlight), <see cref="DetailLegIndex"/> the leg the Atlas detail is SHOWING. Browsing
	/// legs in the Atlas must not pretend a different one is loaded.</para>
	/// </remarks>
	public sealed class SavedEventRow : ObservableObject
	{
		public SavedEventRow(SavedEvent ev, Func<string?, string>? siteName = null)
		{
			Event = ev;
			Legs = ev.Legs.Select((leg, i) => new SavedEventLegRow(this, ev, leg, i, siteName?.Invoke(leg.SiteId) ?? string.Empty)).ToList();
			_currentLegIndex = ev.DefaultLegIndex;
			_detailLegIndex = Math.Clamp(ev.DefaultLegIndex, 0, Legs.Count - 1);
			SyncLegs();
		}

		public SavedEvent Event { get; }
		public string Id => Event.Id;
		public string Name => Event.Name;
		public string Notes => Event.Notes;
		public bool HasNotes => !string.IsNullOrWhiteSpace(Event.Notes);
		public bool CanDelete => !Event.IsBuiltIn;
		public IReadOnlyList<SavedEventLegRow> Legs { get; }
		public bool HasMultipleLegs => Legs.Count > 1;

		/// <summary>The user's own — always tagged "Custom", never built in.</summary>
		public bool IsCustom => !Event.IsBuiltIn;

		/// <summary>"Tornado" — the type badge.</summary>
		public string KindLabel => SavedEventKinds.Label(Event.Kind);

		/// <summary>"1999" — the list row's right-hand year.</summary>
		public string YearText => Event.DefaultLeg.StartUtc.ToLocalTime().Year.ToString(System.Globalization.CultureInfo.InvariantCulture);

		/// <summary>"Remove 'Tuesday's MCS'?" — the delete flyout's question.</summary>
		public string DeletePrompt => $"Remove '{Event.Name}'? This can't be undone.";

		/// <summary>Where a built-in's times came from (tooltip); the user's own have none.</summary>
		public string ToolTip => string.IsNullOrEmpty(Event.Source) ? Event.Name : Event.Source;

		/// <summary>"May 31, 2013".</summary>
		public string DateText => Event.StartUtc.ToLocalTime().ToString("MMM d, yyyy");

		/// <summary>"5:30 PM · 2h" for one leg; the overall span for several.</summary>
		public string SummaryText
		{
			get
			{
				if (!HasMultipleLegs)
				{
					var leg = Event.Legs[0];
					return $"{leg.StartUtc.ToLocalTime():h:mm tt} · {DurationLabel(leg.DurationMinutes)}";
				}
				return $"{Event.StartUtc.ToLocalTime():h:mm tt} → {Event.EndUtc.ToLocalTime():h:mm tt}";
			}
		}

		/// <summary>The site pill: "KTLX", "KSGF +1", or "Any site" for a window-only event.</summary>
		public string SitesText
		{
			get
			{
				var first = Event.DefaultLeg.SiteId ?? "Any site";
				return HasMultipleLegs ? $"{first} +{Legs.Count - 1}" : first;
			}
		}

		// ── The Atlas detail ─────────────────────────────────────────────────────────────────────────

		/// <summary>"May 3, 1999 · F5, May 3 1999 outbreak…" — the line under the detail's name.</summary>
		public string DetailLine => HasNotes ? $"{DateText} · {Notes}" : IsCustom ? $"{DateText} · saved by you" : DateText;

		/// <summary>Where a built-in's times came from. Empty for the user's own (the section hides).</summary>
		public string SourceText => Event.Source;
		public bool HasSource => !string.IsNullOrWhiteSpace(Event.Source);

		/// <summary>What this kind's key time is called — "On the ground" / "Landfall" / "Across".</summary>
		public string KeyLabel => SavedEventKinds.KeyLabel(Event.Kind);

		private int _detailLegIndex;
		/// <summary>The leg the Atlas detail shows (tiles + highlighted leg row). NOT the loaded leg.</summary>
		public int DetailLegIndex
		{
			get => _detailLegIndex;
			set
			{
				var clamped = Math.Clamp(value, 0, Legs.Count - 1);
				if (SetProperty(ref _detailLegIndex, clamped))
				{
					SyncLegs();
					OnPropertyChanged(nameof(DetailLeg));
				}
			}
		}

		public SavedEventLegRow DetailLeg => Legs[_detailLegIndex];

		// ── List state, written by SavedEventsViewModel ────────────────────────────────────────────

		/// <summary>The row's group: its kind ("Tornado"), or "Other" for an untyped event.</summary>
		public string GroupLabel => KindLabel;

		private string _groupHeader = string.Empty;
		/// <summary>"Tornado · 10" — the group header's words, counted over the CURRENT filtered list.</summary>
		public string GroupHeader
		{
			get => _groupHeader;
			set => SetProperty(ref _groupHeader, value);
		}

		private bool _showGroupHeader;
		/// <summary>True on the first row of its group in the CURRENT filtered list.</summary>
		public bool ShowGroupHeader
		{
			get => _showGroupHeader;
			set => SetProperty(ref _showGroupHeader, value);
		}

		private bool _isSelected;
		/// <summary>PICKED (loaded) — the PastCast list's highlight.</summary>
		public bool IsSelected
		{
			get => _isSelected;
			set
			{
				if (SetProperty(ref _isSelected, value))
				{
					OnPropertyChanged(nameof(ShowLegs));
				}
			}
		}

		/// <summary>The leg timeline only opens on the picked row, and only when there is a choice.</summary>
		public bool ShowLegs => _isSelected && HasMultipleLegs;

		private int _currentLegIndex;
		/// <summary>The PICKED leg (see the class remarks for why it isn't <see cref="DetailLegIndex"/>).</summary>
		public int CurrentLegIndex
		{
			get => _currentLegIndex;
			set
			{
				var clamped = Math.Clamp(value, 0, Legs.Count - 1);
				if (SetProperty(ref _currentLegIndex, clamped))
				{
					SyncLegs();
				}
			}
		}

		public bool CanGoPrevious => _currentLegIndex > 0;
		public bool CanGoNext => _currentLegIndex < Legs.Count - 1;

		private void SyncLegs()
		{
			foreach (var leg in Legs)
			{
				leg.IsCurrent = leg.Index == _currentLegIndex;
				leg.IsDetail = leg.Index == _detailLegIndex;
			}
			OnPropertyChanged(nameof(CanGoPrevious));
			OnPropertyChanged(nameof(CanGoNext));
		}

		internal static string DurationLabel(int minutes) => minutes < 60 ? $"{minutes}m" : $"{minutes / 60}h";

		/// <summary>"2 hr" / "30 min" — the detail tile's longer form of <see cref="DurationLabel"/>.</summary>
		internal static string DurationWords(int minutes) => minutes < 60 ? $"{minutes} min" : $"{minutes / 60} hr";

		/// <summary>"6:23–7:48 PM", "6:10 AM" — a key time in LOCAL time, the AM/PM said once when both ends share it.</summary>
		internal static string FormatKey(SavedEventKey key)
		{
			var start = key.StartUtc.ToLocalTime();
			if (key.EndUtc is not { } endUtc)
			{
				return start.ToString("h:mm tt");
			}
			var end = endUtc.ToLocalTime();
			return start.ToString("tt") == end.ToString("tt")
				? $"{start:h:mm}–{end:h:mm tt}"
				: $"{start:h:mm tt}–{end:h:mm tt}";
		}
	}

	/// <summary>
	/// One radar leg of a saved event: the PastCast list's timeline lane (fractions within the whole event,
	/// so the view owns the pixels) and the Atlas detail's leg row + tiles.
	/// </summary>
	public sealed class SavedEventLegRow : ObservableObject
	{
		internal SavedEventLegRow(SavedEventRow owner, SavedEvent ev, SavedEventLeg leg, int index, string siteName)
		{
			Owner = owner;
			Leg = leg;
			Index = index;
			SiteName = siteName;
			_kind = ev.Kind;
			_legCount = ev.Legs.Count;

			var span = Math.Max(1, (ev.EndUtc - ev.StartUtc).TotalMinutes);
			LeftFraction = (leg.StartUtc - ev.StartUtc).TotalMinutes / span;
			WidthFraction = leg.DurationMinutes / span;
		}

		private readonly SavedEventKind _kind;
		private readonly int _legCount;

		public SavedEventRow Owner { get; }
		public SavedEventLeg Leg { get; }
		public int Index { get; }
		public string SiteText => Leg.SiteId ?? "Any";

		/// <summary>"Norman · OK" — the site's place, from the radar site list (empty when unknown).</summary>
		public string SiteName { get; }

		public string TimeText => $"{Leg.StartUtc.ToLocalTime():h:mm tt} · {SavedEventRow.DurationLabel(Leg.DurationMinutes)}";
		public double LeftFraction { get; }
		public double WidthFraction { get; }

		// ── The key time, and the Atlas detail's three tiles for this leg ──────────────────────────────

		public bool HasKey => Leg.Key is not null;

		/// <summary>Tile value: "6:23–7:48 PM" — or "Add a time" / "—" when the leg has none.</summary>
		public string KeyValueText => Leg.Key is { } k ? SavedEventRow.FormatKey(k) : Owner.IsCustom ? "Add a time" : "—";

		/// <summary>Tile label: "ON THE GROUND", "LANDFALL · BURAS".</summary>
		public string KeyLabelText
		{
			get
			{
				var label = SavedEventKinds.KeyLabel(_kind).ToUpperInvariant();
				return Leg.Key is { Place.Length: > 0 } k ? $"{label} · {k.Place.ToUpperInvariant()}" : label;
			}
		}

		/// <summary>The leg row's right-hand text: "On the ground 6:23 PM", "Across 12:30 PM · Cedar Rapids", "—".</summary>
		public string KeyShortText =>
			Leg.Key is { } k
				? $"{SavedEventKinds.KeyLabel(_kind)} {k.StartUtc.ToLocalTime():h:mm tt}{(k.Place.Length > 0 ? " · " + k.Place : "")}"
				: "—";

		/// <summary>Tile value: "6:00 PM · 2 hr".</summary>
		public string WindowValueText => $"{Leg.StartUtc.ToLocalTime():h:mm tt} · {SavedEventRow.DurationWords(Leg.DurationMinutes)}";

		/// <summary>Tile label: "REPLAY WINDOW · MAY 3, 1999".</summary>
		public string WindowLabelText => $"REPLAY WINDOW · {Leg.StartUtc.ToLocalTime():MMM d, yyyy}".ToUpperInvariant();

		/// <summary>Tile label: "RADAR · LEG 1 OF 2".</summary>
		public string RadarLabelText => $"RADAR · LEG {Index + 1} OF {_legCount}";

		private bool _isCurrent;
		/// <summary>The PICKED leg (the PastCast lane's accent fill).</summary>
		public bool IsCurrent
		{
			get => _isCurrent;
			set => SetProperty(ref _isCurrent, value);
		}

		private bool _isDetail;
		/// <summary>The leg the Atlas detail is showing (its leg row's selected face).</summary>
		public bool IsDetail
		{
			get => _isDetail;
			set => SetProperty(ref _isDetail, value);
		}
	}
}
