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

		/// <summary>The comparison table's lift for <paramref name="column"/>: shown only on the picked source.</summary>
		public double Lift(int picked, int column) => picked == column ? 1 : 0;

		// Clicking a column picks its source — the same as the picker above it.
		private void OnColumnTapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
		{
			if (ViewModel is not null && sender is FrameworkElement { Tag: string tag } && int.TryParse(tag, out var index))
			{
				ViewModel.SourceIndex = index;
			}
		}

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
