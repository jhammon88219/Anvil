using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using Anvil.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Anvil.ViewModels
{
	/// <summary>
	/// ONE layer section of a temporal pane, as the RAIL and the section's header box both see it: its id, the rail's
	/// stand-in letter, whether its layer is on (lit), whether it can be toggled at all, and the ONE toggle both use.
	/// </summary>
	/// <remarks>
	/// LIT = ANY of the layer's types shown — a section's box can be partly ticked (the user's call, 2026-10-29). The
	/// state itself lives on the subsystem VM; this only reads it (<see cref="IsOn"/>, re-raised when one of the watched
	/// properties changes) and hands a click to it (<see cref="Toggle"/>), so the rail and the box can never disagree.
	/// </remarks>
	public sealed class TemporalPaneSectionViewModel : ObservableObject
	{
		private readonly Func<bool?> _shown;
		private readonly Func<bool> _enabled;
		private readonly Action _toggle;

		internal TemporalPaneSectionViewModel(string id, string letter, string title, Func<bool?> shown, Action toggle,
			INotifyPropertyChanged source, string[] watched, Func<bool>? enabled = null)
		{
			Id = id;
			Letter = letter;
			Title = title;
			_shown = shown;
			_toggle = toggle;
			_enabled = enabled ?? (() => true);
			source.PropertyChanged += (_, e) =>
			{
				if (e.PropertyName is null || Array.IndexOf(watched, e.PropertyName) < 0) return;
				OnPropertyChanged(nameof(IsOn));
				OnPropertyChanged(nameof(IsEnabled));
			};
		}

		/// <summary>The section's <see cref="LayerOrder"/> id.</summary>
		public string Id { get; }

		/// <summary>The rail button's stand-in for an icon (real icons are later polish).</summary>
		public string Letter { get; }

		/// <summary>The section's name, as its header shows it (the rail button's tooltip).</summary>
		public string Title { get; }

		/// <summary>Lit: any of the layer's types is shown.</summary>
		public bool IsOn => _shown() != false;

		/// <summary>False while the layer can't be toggled (Damage surveys before a window is loaded).</summary>
		public bool IsEnabled => _enabled();

		/// <summary>The section's box and its rail button both land here.</summary>
		public void Toggle()
		{
			if (IsEnabled) _toggle();
		}
	}

	/// <summary>
	/// ONE temporal mode's pane state (TemporalPanes/TemporalPane): Full or Rail (<see cref="TemporalPaneStates"/>,
	/// persisted per mode), which edge it sits on, and its layer <see cref="Sections"/> in the full pane's order —
	/// what the rail draws a button for.
	/// </summary>
	public sealed class TemporalPaneViewModel : ObservableObject
	{
		private readonly Action<string> _writeState;
		private readonly IReadOnlyCollection<string> _ownLayers;
		private string _state;

		internal TemporalPaneViewModel(TemporalMode mode, IReadOnlyCollection<string> ownLayers,
			IEnumerable<TemporalPaneSectionViewModel> sections, Func<string?> readState, Action<string> writeState)
		{
			Mode = mode;
			_ownLayers = ownLayers;
			_writeState = writeState;
			_state = TemporalPaneStates.Normalize(readState());
			Sections = new ObservableCollection<TemporalPaneSectionViewModel>(sections);
		}

		public TemporalMode Mode { get; }

		/// <summary>ForeCast sits on the RIGHT edge, mirrored; Past + Now on the left.</summary>
		public bool IsRightEdge => Mode == TemporalMode.Fore;

		/// <summary>The state token (<see cref="TemporalPaneStates"/>).</summary>
		public string State => _state;

		/// <summary>Collapsed to its rail.</summary>
		public bool IsRail => TemporalPaneStates.IsRail(_state);

		/// <summary>The header's collapse button and the rail's expand button.</summary>
		public void ToggleState()
		{
			_state = TemporalPaneStates.Next(_state);
			_writeState(_state);
			OnPropertyChanged(nameof(State));
			OnPropertyChanged(nameof(IsRail));
		}

		/// <summary>The pane's layer sections, in the full pane's order (the rail's buttons, top to bottom).</summary>
		public ObservableCollection<TemporalPaneSectionViewModel> Sections { get; }

		public TemporalPaneSectionViewModel? Section(string id) => Sections.FirstOrDefault(s => s.Id == id);

		/// <summary>A section header's box: the same toggle as its rail button.</summary>
		public void Toggle(string id) => Section(id)?.Toggle();

		/// <summary>Re-sorts <see cref="Sections"/> to <paramref name="layerOrder"/> (moved in place — the rail keeps its buttons).</summary>
		internal void ApplyLayerOrder(IReadOnlyList<string> layerOrder)
		{
			var order = TemporalPaneDock.FullOrder(layerOrder, _ownLayers).Where(id => id != TemporalPaneDock.Header).ToList();
			for (int target = 0; target < order.Count; target++)
			{
				int at = -1;
				for (int i = target; i < Sections.Count; i++)
				{
					if (Sections[i].Id == order[target]) { at = i; break; }
				}
				if (at > target) Sections.Move(at, target);
			}
		}
	}
}
