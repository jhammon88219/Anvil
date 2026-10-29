using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.ViewModels;
// ⚠️ Aliased: inside Anvil.Controls.* a bare "Primitives" is Anvil.Controls.Primitives (see CLAUDE.md).
using ToggleButton = Microsoft.UI.Xaml.Controls.Primitives.ToggleButton;

namespace Anvil.Controls.Composites
{
	/// <summary>
	/// The bar's LEFT section: three temporal modes (PastCast / NowCast / ForeCast), each a PLAIN key that toggles ITS
	/// MODE and nothing else — unlit → on, LIT → off. A mode's pane follows the mode on its own (see the XAML header).
	/// You can also leave a mode by choosing another (Past excludes Now/Fore; Now and Fore coexist). The lit state binds
	/// OneWay to <see cref="MapViewModel.IsPastCast"/> etc.; the click routes through
	/// <see cref="MapViewModel.ToggleTemporalMode"/>. Binds the coordinator <see cref="MapViewModel"/>.
	/// </summary>
	public sealed partial class TemporalToggles : UserControl
	{
		// ===== Key sizing =====
		// ⚠️ THERE ISN'T ANY, and that is the point. Every dimension in the bar is declared in XAML — the shared numbers
		// in Controls/Styles.xaml section 2 (BarKeyToggleStyle sizes the square). See the ⚠️ history there before
		// putting any measuring back.

		public TemporalToggles()
		{
			InitializeComponent();
		}

		// ===== The row ends on the left temporal pane's INNER edge (the user's call, 2026-10-22) =====
		// The spacer between PastCast and NowCast takes the slack; its own left edge doesn't move with it, so a repeat
		// call in the same layout lands on the same value. A 3 × 3 floor, like the tools tier's spacers.
		private const int SpacerMinColumns = 3;

		/// <summary>Size the spacer so ForeCast's right edge lands on <paramref name="innerEdge"/>, in
		/// <paramref name="root"/>'s coordinates.</summary>
		public void AlignToWindowEdge(double innerEdge, UIElement root)
		{
			if (TemporalKeysSpacer.ActualWidth <= 0 || ForeKey.ActualWidth <= 0)
			{
				return;
			}
			var start = TemporalKeysSpacer.TransformToVisual(root).TransformPoint(default).X;
			var after = TemporalKeysSpacer.Margin.Right + PillRow.ColumnSpacing + NowKey.ActualWidth
				+ PillRow.ColumnSpacing + ForeKey.ActualWidth;
			TemporalKeysSpacer.SetFillWidth(innerEdge - start - after, SpacerMinColumns);
		}

		/// <summary>The coordinator view model; bound from the host.</summary>
		public MapViewModel ViewModel
		{
			get => (MapViewModel)GetValue(ViewModelProperty);
			set => SetValue(ViewModelProperty, value);
		}

		public static readonly DependencyProperty ViewModelProperty =
			DependencyProperty.Register(nameof(ViewModel), typeof(MapViewModel), typeof(TemporalToggles), new PropertyMetadata(null));

		// Each key routes here (IsChecked is OneWay to the mode projection, so the click drives the VM, not the reverse),
		// then the key is re-asserted from the VM's answer — the ToggleButton has already flipped itself.
		private void OnPastClick(object sender, RoutedEventArgs e) => Toggle(TemporalMode.Past, PastKey);
		private void OnNowClick(object sender, RoutedEventArgs e) => Toggle(TemporalMode.Now, NowKey);
		private void OnForeClick(object sender, RoutedEventArgs e) => Toggle(TemporalMode.Fore, ForeKey);

		private void Toggle(TemporalMode mode, ToggleButton key)
		{
			if (ViewModel is null) { return; }
			ViewModel.ToggleTemporalMode(mode);
			key.IsChecked = mode switch
			{
				TemporalMode.Past => ViewModel.IsPastCast,
				TemporalMode.Now => ViewModel.IsNowCast,
				_ => ViewModel.IsForeCast,
			};
		}
	}
}
