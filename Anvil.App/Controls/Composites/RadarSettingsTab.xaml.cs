using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.ViewModels;

namespace Anvil.Controls.Composites
{
	/// <summary>
	/// Settings → Radar (see the XAML header): Live polling, Memory, Site keys, Scrubber and Hatch scrubber — each its own
	/// section control — over the coordinator <see cref="MapViewModel"/>.
	/// </summary>
	public sealed partial class RadarSettingsTab : UserControl
	{
		public RadarSettingsTab()
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
			DependencyProperty.Register(nameof(ViewModel), typeof(MapViewModel), typeof(RadarSettingsTab), new PropertyMetadata(null));

		private async void OnResetAll(object sender, RoutedEventArgs e)
		{
			if (ViewModel is { } vm && await Anvil.Dialogs.ConfirmResetDialog.ConfirmAsync(this, "Reset all Radar settings?",
				"Live polling, Memory, Site keys, Scrubber and Hatch scrubber go back to their defaults — the DEFAULT column on this " +
				"page. This can't be undone."))
			{
				vm.ResetRadarPage();
			}
		}
	}
}
