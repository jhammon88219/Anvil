using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.ViewModels;

namespace Anvil.Controls.Composites
{
	/// <summary>
	/// The overlay bar's CENTRE section: three temporal features (PastCast / NowCast / ForeCast), each a
	/// SPLIT key (<see cref="Primitives.SplitTemporalToggle"/>) whose square toggles the mode and whose
	/// full-height SIDE CAR latches that mode's settings window. The square toggles ITS MODE and nothing
	/// else — unlit → on, LIT → off — while the car shows and hides the window and never touches the mode.
	/// Turning a mode on also opens its
	/// window (<see cref="MapViewModel.OpenTemporal"/>), so the common path stays one click. You can also
	/// leave a mode by choosing another (Past excludes Now/Fore; Now and Fore coexist). The lit state binds
	/// OneWay to <see cref="MapViewModel.IsPastCast"/> etc.; the click routes through
	/// <see cref="MapViewModel.ToggleTemporalMode"/>. Binds the coordinator <see cref="MapViewModel"/>.
	/// </summary>
	public sealed partial class TemporalToggles : UserControl
	{
		// ===== Key sizing =====
		// ⚠️ THERE ISN'T ANY, and that is the point. Every dimension in the bar is declared in XAML — the
		// shared numbers in Controls/Styles.xaml section 2, the per-key ones in the key that uses them.
		//
		// This control used to MEASURE: the bar's content row stretched to whatever its tallest content
		// needed, and a SizeChanged handler here mirrored that height onto every key's Width and scaled the
		// glyph and the name off it. That machinery existed for the per-pane product chips, which lived in
		// the bar and made it taller in quad layout. The chips moved to the pane notches, the bar's height
		// stopped moving, and the measuring was reacting to nothing — while leaving the keys at whatever size
		// the radar console beside them happened to imply. The keys declare a size now and the bar's Auto row
		// sizes to THEM. See the ⚠️ history in Styles.xaml before putting any of it back.

		public TemporalToggles()
		{
			InitializeComponent();
		}

		/// <summary>The coordinator view model; bound from the host.</summary>
		public MapViewModel ViewModel
		{
			get => (MapViewModel)GetValue(ViewModelProperty);
			set => SetValue(ViewModelProperty, value);
		}

		public static readonly DependencyProperty ViewModelProperty =
			DependencyProperty.Register(nameof(ViewModel), typeof(MapViewModel), typeof(TemporalToggles), new PropertyMetadata(null));

		// Each mode's SQUARE routes here (IsModeOn is OneWay to the mode projection, so the click drives the
		// VM, not the reverse). ToggleTemporalMode just flips that mode on or off; turning one on also opens
		// its window, which is the view model's business and not this control's.
		//
		// The RAIL needs no handler — it two-ways straight to that window's open flag through the key's
		// IsPanelOpen. That is the split: one control per contract, and only the mode half needs a decision
		// made for it.
		private void OnPastClick(object sender, RoutedEventArgs e) => ViewModel?.ToggleTemporalMode(TemporalMode.Past);
		private void OnNowClick(object sender, RoutedEventArgs e) => ViewModel?.ToggleTemporalMode(TemporalMode.Now);
		private void OnForeClick(object sender, RoutedEventArgs e) => ViewModel?.ToggleTemporalMode(TemporalMode.Fore);
	}
}
