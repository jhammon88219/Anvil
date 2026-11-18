using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.ViewModels;

namespace Anvil.Controls.LiveDataSettingsConsoles
{
	/// <summary>Settings → Live Data's Source section (see the XAML header).</summary>
	public sealed partial class LiveDataSettingsConsoleSource : UserControl
	{
		public LiveDataSettingsConsoleSource()
		{
			InitializeComponent();
		}

		public LiveDataViewModel ViewModel
		{
			get => (LiveDataViewModel)GetValue(ViewModelProperty);
			set => SetValue(ViewModelProperty, value);
		}

		public static readonly DependencyProperty ViewModelProperty =
			DependencyProperty.Register(nameof(ViewModel), typeof(LiveDataViewModel), typeof(LiveDataSettingsConsoleSource),
				new PropertyMetadata(null));

		/// <summary>The details table's accent head: shown only on the picked source's <paramref name="column"/>.</summary>
		public double Picked(int picked, int column) => picked == column ? 1 : 0;

		/// <summary>The details table's grey head: every column but the picked one.</summary>
		public double Unpicked(int picked, int column) => picked == column ? 0 : 1;

		/// <summary>The dot before the picked source's head.</summary>
		public Visibility PickedMark(int picked, int column) => picked == column ? Visibility.Visible : Visibility.Collapsed;

		private async void OnRestoreDefaults(object sender, RoutedEventArgs e)
		{
			if (ViewModel is { } vm && await Anvil.Dialogs.ConfirmResetDialog.ConfirmAsync(this, "Restore Source default?",
				"Live updates go back to the IEM live feed. Your NOAA Weather Wire account is kept."))
			{
				vm.ResetSource();
			}
		}
	}
}
