using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Anvil.Services;
using Anvil.ViewModels;

namespace Anvil.Controls.Composites
{
	/// <summary>
	/// The Settings window's Live Data tab (see the XAML header): the live source, the NOAA Weather Wire account, each
	/// feed's polling interval and the connection. State lives on <see cref="LiveDataViewModel"/>.
	/// </summary>
	public sealed partial class LiveDataSettingsTab : UserControl
	{
		// Status-dot literals (DATA colours, the activity expander's family): live green · down amber · off/connecting grey.
		private static readonly SolidColorBrush LiveBrush = new(ColorHelper.FromArgb(0xFF, 0x3F, 0xB9, 0x50));
		private static readonly SolidColorBrush DownBrush = new(ColorHelper.FromArgb(0xFF, 0xD2, 0x99, 0x22));
		private static readonly SolidColorBrush QuietBrush = new(ColorHelper.FromArgb(0xFF, 0x8A, 0x8F, 0x98));

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

		// ── x:Bind helpers ──

		public double AccountOpacity(bool enabled) => enabled ? 1.0 : 0.45;

		public Brush StateBrush(LiveLinkState state) => state switch
		{
			LiveLinkState.Connected => LiveBrush,
			LiveLinkState.Down or LiveLinkState.NeedsAccount => DownBrush,
			_ => QuietBrush,
		};

		// ── The account (the password goes straight to the VM, never through a binding) ──

		private void OnPasswordChanged(object sender, RoutedEventArgs e) => ViewModel?.SetPendingPassword(NwwsPasswordBox.Password);

		private void OnSaveAccountClick(object sender, RoutedEventArgs e)
		{
			if (ViewModel?.SaveAccount() == true) { NwwsPasswordBox.Password = string.Empty; } // now "Saved — type to replace"
		}

		private void OnTestConnectionClick(object sender, RoutedEventArgs e) => _ = ViewModel?.TestConnectionAsync();
	}
}
