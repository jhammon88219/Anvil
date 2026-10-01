using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Anvil.ViewModels
{
	/// <summary>One row of the Map key's layer flyout: a basemap group (<c>Models.BasemapGroups</c>) and its tick.</summary>
	public sealed class BasemapGroupOption : ObservableObject
	{
		private readonly Action _changed;

		public BasemapGroupOption(string id, string label, bool isShown, Action changed)
		{
			Id = id;
			Label = label;
			_isShown = isShown;
			_changed = changed;
		}

		/// <summary>The group id the page and the settings file know it by.</summary>
		public string Id { get; }

		/// <summary>The row's words.</summary>
		public string Label { get; }

		private bool _isShown;

		/// <summary>Ticked = this group draws (while the map itself is shown). Two-way with the row's box.</summary>
		public bool IsShown
		{
			get => _isShown;
			set
			{
				if (SetProperty(ref _isShown, value)) { _changed(); }
			}
		}

		private bool _isAvailable = true;

		/// <summary>False when the loaded basemap has no layers in this group (Counties, on four of the five
		/// styles) — the row greys, keeping its tick for the style that has them.</summary>
		public bool IsAvailable
		{
			get => _isAvailable;
			set
			{
				if (SetProperty(ref _isAvailable, value)) { OnPropertyChanged(nameof(ToolTip)); }
			}
		}

		/// <summary>Why a greyed row is grey; null (no tooltip) otherwise.</summary>
		public string? ToolTip => _isAvailable ? null : "This basemap has no " + Label.ToLowerInvariant() + " layer";
	}
}
