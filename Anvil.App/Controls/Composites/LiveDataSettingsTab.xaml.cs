using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.ViewModels;

namespace Anvil.Controls.Composites
{
	/// <summary>
	/// Settings → Live Data (see the XAML header): Source, Weather Wire account, Polling and Connection — each its own section
	/// control — over <see cref="LiveDataViewModel"/>.
	/// </summary>
	public sealed partial class LiveDataSettingsTab : UserControl
	{
		public LiveDataSettingsTab()
		{
			InitializeComponent();
		}

		/// <summary>The live-data view model; bound from the host.</summary>
		public LiveDataViewModel ViewModel
		{
			get => (LiveDataViewModel)GetValue(ViewModelProperty);
			set => SetValue(ViewModelProperty, value);
		}

		public static readonly DependencyProperty ViewModelProperty =
			DependencyProperty.Register(nameof(ViewModel), typeof(LiveDataViewModel), typeof(LiveDataSettingsTab), new PropertyMetadata(null));

		private async void OnResetAll(object sender, RoutedEventArgs e)
		{
			if (ViewModel is { } vm && await Anvil.Dialogs.ConfirmResetDialog.ConfirmAsync(this, "Reset all Live Data settings?",
				"The source and every polling interval go back to their defaults — the DEFAULT column on this page. Your NOAA Weather " +
				"Wire account is kept."))
			{
				vm.RestoreDefaults();
			}
		}
	}
}
