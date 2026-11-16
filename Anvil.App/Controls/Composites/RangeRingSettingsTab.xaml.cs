using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.ViewModels;

namespace Anvil.Controls.Composites
{
	/// <summary>
	/// Settings → Rings &amp; Ruler (see the XAML header): which range rings are drawn, how each looks, the distance labels and
	/// the ruler — each its own section control — over the coordinator <see cref="MapViewModel"/>; the state lives on
	/// <see cref="RadarViewModel.RangeRings"/>.
	/// </summary>
	public sealed partial class RangeRingSettingsTab : UserControl
	{
		public RangeRingSettingsTab()
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
			DependencyProperty.Register(nameof(ViewModel), typeof(MapViewModel), typeof(RangeRingSettingsTab), new PropertyMetadata(null));

		private async void OnResetAll(object sender, RoutedEventArgs e)
		{
			if (ViewModel is { } vm && await Anvil.Dialogs.ConfirmResetDialog.ConfirmAsync(this, "Reset all Rings & Ruler settings?",
				"Which rings are drawn, every ring's look, the distance labels and the ruler go back to their defaults — the DEFAULT " +
				"column on this page. This can't be undone."))
			{
				vm.ResetRingsPage();
			}
		}
	}
}
