using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Anvil.ViewModels
{
	/// <summary>
	/// One TOP-LEVEL row of the Map key's layer flyout (a <c>Models.BasemapGroups.Tree</c> category). With one
	/// leaf it is a plain row; with several it is an EXPANDER whose box is a select-all over its leaves.
	/// </summary>
	/// <remarks>
	/// ⚠️ THE BOX IS ONE-WAY + <see cref="ToggleAll"/>, PanelSection's select-all rule: all shown → clear them,
	/// otherwise (none, or the partial ■) show them all. <see cref="IsChecked"/> is null while partial.
	/// The leaves (<see cref="BasemapGroupOption"/>) are what persist; this row holds no state of its own except
	/// <see cref="IsExpanded"/>, which is session-only (every expander starts closed).
	/// </remarks>
	public sealed class BasemapCategory : ObservableObject
	{
		public BasemapCategory(string label, IReadOnlyList<BasemapGroupOption> leaves)
		{
			Label = label;
			Leaves = leaves;
			foreach (var leaf in leaves) { leaf.PropertyChanged += OnLeafChanged; }
		}

		/// <summary>The row's words (display only).</summary>
		public string Label { get; }

		/// <summary>The groups under this row, in flyout order. One for a plain row.</summary>
		public IReadOnlyList<BasemapGroupOption> Leaves { get; }

		/// <summary>True when this row is an expander (more than one leaf).</summary>
		public bool HasChildren => Leaves.Count > 1;

		/// <summary>All shown = true, none = false, some = null (the partial box).</summary>
		public bool? IsChecked =>
			Leaves.All(l => l.IsShown) ? true :
			Leaves.Any(l => l.IsShown) ? null :
			false;

		/// <summary>False only when NO leaf exists in the loaded style (Counties on four of five).</summary>
		public bool IsAvailable => Leaves.Any(l => l.IsAvailable);

		/// <summary>Why a greyed row is grey — a plain row's own leaf's reason; null otherwise.</summary>
		public string? ToolTip => HasChildren ? null : Leaves[0].ToolTip;

		private bool _isExpanded;

		/// <summary>Whether an expander's leaves show. Session-only.</summary>
		public bool IsExpanded
		{
			get => _isExpanded;
			set => SetProperty(ref _isExpanded, value);
		}

		/// <summary>The chevron's click.</summary>
		public void ToggleExpanded() => IsExpanded = !IsExpanded;

		/// <summary>The box's click: all shown → clear them, otherwise show them all.</summary>
		public void ToggleAll()
		{
			var show = IsChecked != true;
			foreach (var leaf in Leaves.Where(l => l.IsAvailable)) { leaf.IsShown = show; }
			// The box has already flipped itself; re-raise so it shows the answer even if nothing changed.
			OnPropertyChanged(nameof(IsChecked));
		}

		private void OnLeafChanged(object? sender, PropertyChangedEventArgs e)
		{
			if (e.PropertyName == nameof(BasemapGroupOption.IsShown)) { OnPropertyChanged(nameof(IsChecked)); }
			else if (e.PropertyName == nameof(BasemapGroupOption.IsAvailable))
			{
				OnPropertyChanged(nameof(IsAvailable));
				OnPropertyChanged(nameof(ToolTip));
			}
		}
	}
}
