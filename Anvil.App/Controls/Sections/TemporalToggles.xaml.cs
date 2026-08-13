using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.ViewModels;

namespace Anvil.Controls.Sections
{
	/// <summary>
	/// The overlay bar's left section: three temporal features (PastCast / NowCast / ForeCast), each a
	/// SINGLE toggle (the settings cog is gone). A click activates the mode and opens its floating card
	/// (see <see cref="TemporalCards"/>); clicking the active mode flips its card open/closed, and you leave
	/// a mode by choosing another (Past excludes Now/Fore; Now and Fore coexist). The lit state binds OneWay
	/// to <see cref="MapViewModel.IsPastCast"/> etc.; the click routes through
	/// <see cref="MapViewModel.ToggleTemporalMode"/>. Binds the coordinator <see cref="MapViewModel"/>.
	/// </summary>
	public sealed partial class TemporalToggles : UserControl
	{
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

		// Each mode's single toggle routes here (IsChecked is OneWay to the mode projection, so the click
		// drives the VM, not the reverse). See MapViewModel.ToggleTemporalMode for the activate/flip rules.
		private void OnPastClick(object sender, RoutedEventArgs e) => ViewModel?.ToggleTemporalMode(TemporalCard.Past);
		private void OnNowClick(object sender, RoutedEventArgs e) => ViewModel?.ToggleTemporalMode(TemporalCard.Now);
		private void OnForeClick(object sender, RoutedEventArgs e) => ViewModel?.ToggleTemporalMode(TemporalCard.Fore);
	}
}
