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

		private bool _isDocked;

		/// <summary>The pane sits on the RIGHT edge (ForeCast): the rail button mirrors. Set by the pane.</summary>
		public bool IsRightEdge { get; internal set; }

		/// <summary>Docked beside the rail (the rail button's sidecar). Set by the pane.</summary>
		public bool IsDocked
		{
			get => _isDocked;
			internal set => SetProperty(ref _isDocked, value);
		}

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

		/// <summary>The rail button's SIDECAR: dock / undock this section (the pane owns the set).</summary>
		public void ToggleDocked() => DockToggled?.Invoke(this, EventArgs.Empty);

		internal event EventHandler? DockToggled;
	}

	/// <summary>
	/// ONE temporal mode's pane state (TemporalPanes/TemporalPane): Full or Rail (<see cref="TemporalPaneStates"/>,
	/// persisted per mode), which edge it sits on, its layer <see cref="Sections"/> in the full pane's order — what the
	/// rail draws a button for — and the DOCKED set (persisted per mode): the sections shown beside the rail.
	/// </summary>
	/// <remarks>
	/// ⚠️ ONE RENDERING: docking never copies a section. The pane's Content shows a section iff
	/// <see cref="ShownSections"/> holds its id — every section in Full, only the docked ones in Rail — so the dock stack
	/// IS the full pane with the undocked parts collapsed, in the full pane's order with the gaps closed.
	/// </remarks>
	public sealed class TemporalPaneViewModel : ObservableObject
	{
		private readonly Action<string> _writeState;
		private readonly Action<List<string>> _writeDocked;
		private readonly IReadOnlyCollection<string> _ownLayers;
		private string _state;
		private List<string> _docked;

		internal TemporalPaneViewModel(TemporalMode mode, IReadOnlyCollection<string> ownLayers,
			IEnumerable<TemporalPaneSectionViewModel> sections, Func<string?> readState, Action<string> writeState,
			Func<IEnumerable<string>?> readDocked, Action<List<string>> writeDocked)
		{
			Mode = mode;
			_ownLayers = ownLayers;
			_writeState = writeState;
			_writeDocked = writeDocked;
			_state = TemporalPaneStates.Normalize(readState());
			_docked = TemporalPaneDock.NormalizeDocked(readDocked(), ownLayers);
			Sections = new ObservableCollection<TemporalPaneSectionViewModel>(sections);
			foreach (var s in Sections)
			{
				s.IsDocked = _docked.Contains(s.Id);
				s.IsRightEdge = IsRightEdge;
				s.DockToggled += (_, _) => ToggleDocked(s.Id);
			}
		}

		/// <summary>The header block is docked (the rail's header button).</summary>
		public bool IsHeaderDocked => _docked.Contains(TemporalPaneDock.Header);

		/// <summary>The rail's header button: dock / undock the header block (it has no layer, so nothing to toggle).</summary>
		public void ToggleHeaderDocked() => ToggleDocked(TemporalPaneDock.Header);

		/// <summary>Anything is docked — the dock stack has something to show in Rail state.</summary>
		public bool HasDock => _docked.Count > 0;

		/// <summary>Whether <paramref name="id"/> (a layer id or <see cref="TemporalPaneDock.Header"/>) is docked.</summary>
		public bool IsDocked(string id) => _docked.Contains(id);

		/// <summary>A rail sidecar's (or the header button's) click: dock or undock <paramref name="id"/>.</summary>
		public void ToggleDocked(string id)
		{
			if (id != TemporalPaneDock.Header && !_ownLayers.Contains(id)) { return; }
			_docked = TemporalPaneDock.ToggleDocked(_docked, id);
			_writeDocked(new List<string>(_docked));
			if (Section(id) is { } section) { section.IsDocked = _docked.Contains(id); }
			OnPropertyChanged(nameof(IsHeaderDocked));
			OnPropertyChanged(nameof(HasDock));
			OnPropertyChanged(nameof(ShownSections));
		}

		/// <summary>
		/// The section ids the Content shows now: in Full, the header + every own layer; in Rail, the docked ones only.
		/// A NEW collection on every change (bound by value).
		/// </summary>
		public IReadOnlyCollection<string> ShownSections =>
			IsRail ? _docked.ToHashSet() : _ownLayers.Append(TemporalPaneDock.Header).ToHashSet();

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
			OnPropertyChanged(nameof(ShownSections));
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
