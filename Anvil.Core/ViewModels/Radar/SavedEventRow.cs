using System;
using System.Collections.Generic;
using System.Linq;
using Anvil.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Anvil.ViewModels
{
	/// <summary>
	/// One row of the PastCast saved-event list: a <see cref="SavedEvent"/> dressed for display, plus the
	/// list's per-row state (selected, which leg is current, whether it opens its group).
	/// </summary>
	/// <remarks>
	/// ⚠️ All times shown are LOCAL, converted from the event's UTC legs here — the same frame the
	/// Timeframe pickers use, so the row and the card never disagree about what 5:30 PM means.
	/// </remarks>
	public sealed class SavedEventRow : ObservableObject
	{
		public SavedEventRow(SavedEvent ev)
		{
			Event = ev;
			Legs = ev.Legs.Select((leg, i) => new SavedEventLegRow(this, ev, leg, i)).ToList();
			_currentLegIndex = ev.DefaultLegIndex;
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

		// ── List state, written by SavedEventsViewModel ────────────────────────────────────────────

		/// <summary>"Yours" / "Built-in".</summary>
		public string GroupLabel => Event.IsBuiltIn ? "Built-in" : "Yours";

		private bool _showGroupHeader;
		/// <summary>True on the first row of its group in the CURRENT filtered list.</summary>
		public bool ShowGroupHeader
		{
			get => _showGroupHeader;
			set => SetProperty(ref _showGroupHeader, value);
		}

		private bool _isSelected;
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
			}
			OnPropertyChanged(nameof(CanGoPrevious));
			OnPropertyChanged(nameof(CanGoNext));
		}

		internal static string DurationLabel(int minutes) => minutes < 60 ? $"{minutes}m" : $"{minutes / 60}h";
	}

	/// <summary>
	/// One lane of a saved event's leg timeline: the site, its window, and where that window sits within
	/// the whole event (as fractions, so the view owns the pixels).
	/// </summary>
	public sealed class SavedEventLegRow : ObservableObject
	{
		internal SavedEventLegRow(SavedEventRow owner, SavedEvent ev, SavedEventLeg leg, int index)
		{
			Owner = owner;
			Leg = leg;
			Index = index;

			var span = Math.Max(1, (ev.EndUtc - ev.StartUtc).TotalMinutes);
			LeftFraction = (leg.StartUtc - ev.StartUtc).TotalMinutes / span;
			WidthFraction = leg.DurationMinutes / span;
		}

		public SavedEventRow Owner { get; }
		public SavedEventLeg Leg { get; }
		public int Index { get; }
		public string SiteText => Leg.SiteId ?? "Any";
		public string TimeText => $"{Leg.StartUtc.ToLocalTime():h:mm tt} · {SavedEventRow.DurationLabel(Leg.DurationMinutes)}";
		public double LeftFraction { get; }
		public double WidthFraction { get; }

		private bool _isCurrent;
		public bool IsCurrent
		{
			get => _isCurrent;
			set => SetProperty(ref _isCurrent, value);
		}
	}
}
