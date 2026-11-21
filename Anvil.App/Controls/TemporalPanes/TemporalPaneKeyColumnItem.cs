using System;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Anvil.Controls.TemporalPanes.DragMoveLayerExpanders;

namespace Anvil.Controls.TemporalPanes
{
	/// <summary>
	/// ONE KEY of a minimized temporal pane (TemporalPaneKeyColumn) — DATA, not a control: it reads and drives the
	/// layer's own <see cref="DragMoveLayerExpander"/>, so the key and the full pane's header can never disagree.
	/// <list type="bullet">
	/// <item>show/hide = the header box's click (<see cref="DragMoveLayerExpander.ToggleFromKey"/> → the same VM call; a
	///   select-all section's key is that select-all, and a partial [-] reads as <see cref="IsChecked"/> null);</item>
	/// <item>opacity = the header slider's value (<see cref="DragMoveLayerExpander.OpacityValue"/>, which is bound TWO-WAY
	///   to the VM, so writing it here moves the map).</item>
	/// </list>
	/// ⚠️ <see cref="Dispose"/> when the column rebuilds — it holds property callbacks on the expander.
	/// </summary>
	public sealed partial class TemporalPaneKeyColumnItem : ObservableObject, IDisposable
	{
		private readonly DragMoveLayerExpander _layer;
		private readonly (DependencyProperty Property, long Token)[] _watches;

		internal TemporalPaneKeyColumnItem(DragMoveLayerExpander layer)
		{
			_layer = layer;
			_watches = new[]
			{
				Watch(DragMoveLayerExpander.IsCheckedProperty, nameof(IsChecked), nameof(ToolTip)),
				Watch(DragMoveLayerExpander.OpacityValueProperty, nameof(Opacity), nameof(Percent), nameof(ToolTip)),
				Watch(DragMoveLayerExpander.IsOpacityEnabledProperty, nameof(IsOpacityEnabled)),
				Watch(DragMoveLayerExpander.IsCheckBoxEnabledProperty, nameof(CanToggle)),
				Watch(DragMoveLayerExpander.HeaderProperty, nameof(Name), nameof(ToolTip)),
			};
		}

		private (DependencyProperty, long) Watch(DependencyProperty property, params string[] raises) =>
			(property, _layer.RegisterPropertyChangedCallback(property, (_, _) =>
			{
				foreach (var name in raises) { OnPropertyChanged(name); }
			}));

		/// <summary>The layer's name (the expander's header).</summary>
		public string Name => _layer.Header;

		/// <summary>The key's icon: path data on a 24 × 24 grid (<see cref="IconFor"/>).</summary>
		public string IconData => IconFor(_layer.LayerId);

		/// <summary>The header box's state: true on, false off, null = some of the section's types (the [-]).</summary>
		public bool? IsChecked => _layer.IsChecked;

		/// <summary>The key can show/hide its layer (the header has a live box).</summary>
		public bool CanToggle => _layer.ShowCheckBox && _layer.IsCheckBoxEnabled;

		/// <summary>The layer has an opacity (the header shows a slider) — else right-click does nothing.</summary>
		public bool HasOpacity => _layer.ShowOpacity;

		/// <summary>False while the layer draws nothing (the header's slider greys the same way).</summary>
		public bool IsOpacityEnabled => _layer.IsOpacityEnabled;

		/// <summary>The layer's opacity, 0-1 — the header slider's value, written straight through to it.</summary>
		public double Opacity
		{
			get => _layer.OpacityValue;
			set
			{
				if (Math.Abs(_layer.OpacityValue - value) < 0.0005) { return; }
				_layer.OpacityValue = value;
			}
		}

		/// <summary>The opacity as the header writes it ("85%").</summary>
		public string Percent => Math.Round(Opacity * 100).ToString("0", CultureInfo.InvariantCulture) + "%";

		/// <summary>The key's tooltip — the only place a minimized pane names its layers.</summary>
		public string ToolTip => Name + " · " + (IsChecked == false ? "hidden" : HasOpacity ? Percent : "shown")
			+ (HasOpacity ? "\nRight-click for opacity" : "");

		/// <summary>Show/hide — exactly the header box's click.</summary>
		public void Toggle() => _layer.ToggleFromKey();

		public void Dispose()
		{
			foreach (var (property, token) in _watches) { _layer.UnregisterPropertyChangedCallback(property, token); }
		}

		// THE ICONS, one per Models.LayerOrder id: stroked paths on a 24 × 24 grid (drawn 16 px), mirroring the layer's own
		// look where it has one (a warning polygon, a dashed watch box, a cell with its track). An id with no icon gets the
		// plain square, so a new layer still gets a key — add its line here.
		internal static string IconFor(string layerId) => layerId switch
		{
			"cells" => "M4,16 a4,4 0 1 0 8,0 a4,4 0 1 0 -8,0 M11,13 L19,5 M15,5 L19,5 L19,9",
			"reports" => "M3.5,8 a2.5,2.5 0 1 0 5,0 a2.5,2.5 0 1 0 -5,0 M13.5,7 a2.5,2.5 0 1 0 5,0 a2.5,2.5 0 1 0 -5,0 M8.5,17 a2.5,2.5 0 1 0 5,0 a2.5,2.5 0 1 0 -5,0",
			"warnings" => "M4,7 L13,4 L20,10 L17,20 L6,18 Z",
			"watches" => "M4,5 L9,5 M13,5 L18,5 M20,7 L20,11 M20,15 L20,19 M18,19 L13,19 M9,19 L4,19 M4,17 L4,13 M4,9 L4,5",
			"tropical" => "M9,12 a3,3 0 1 0 6,0 a3,3 0 1 0 -6,0 M12,3 C7,3 4,7 4,10 M12,21 C17,21 20,17 20,14",
			"mds" => "M6,3 L15,3 L19,7 L19,21 L6,21 Z M9,11 L16,11 M9,15 L16,15",
			"radar" => "M4,12 a8,8 0 1 0 16,0 a8,8 0 1 0 -16,0 M12,12 L18,6",
			"outlook" => "M3,18 C6,11 9,8 12,8 C15,8 18,11 21,18 M7,18 C9,14 10.5,12.5 12,12.5 C13.5,12.5 15,14 17,18",
			"damage" => "M4,5 L20,5 M6,9 L18,9 M8,13 L16,13 M10,17 L14,17 M11.5,20 L12.5,20",
			"models" => "M4,4 L20,4 L20,20 L4,20 Z M4,10 L20,10 M4,16 L20,16 M10,4 L10,20 M16,4 L16,20",
			_ => "M5,5 L19,5 L19,19 L5,19 Z",
		};
	}
}
