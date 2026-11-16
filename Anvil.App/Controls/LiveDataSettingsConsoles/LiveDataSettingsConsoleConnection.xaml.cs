using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Anvil.Services;
using Anvil.ViewModels;

namespace Anvil.Controls.LiveDataSettingsConsoles
{
	/// <summary>Settings → Live Data's Connection section (see the XAML header): read-only link status.</summary>
	public sealed partial class LiveDataSettingsConsoleConnection : UserControl
	{
		// Status-dot literals (DATA colours, the activity expander's family): live green · down amber · off/connecting grey.
		private static readonly SolidColorBrush LiveBrush = new(ColorHelper.FromArgb(0xFF, 0x3F, 0xB9, 0x50));
		private static readonly SolidColorBrush DownBrush = new(ColorHelper.FromArgb(0xFF, 0xD2, 0x99, 0x22));
		private static readonly SolidColorBrush QuietBrush = new(ColorHelper.FromArgb(0xFF, 0x8A, 0x8F, 0x98));

		public LiveDataSettingsConsoleConnection()
		{
			InitializeComponent();
		}

		public LiveDataViewModel ViewModel
		{
			get => (LiveDataViewModel)GetValue(ViewModelProperty);
			set => SetValue(ViewModelProperty, value);
		}

		public static readonly DependencyProperty ViewModelProperty =
			DependencyProperty.Register(nameof(ViewModel), typeof(LiveDataViewModel), typeof(LiveDataSettingsConsoleConnection),
				new PropertyMetadata(null));

		public Brush StateBrush(LiveLinkState state) => state switch
		{
			LiveLinkState.Connected => LiveBrush,
			LiveLinkState.Down or LiveLinkState.NeedsAccount => DownBrush,
			_ => QuietBrush,
		};
	}
}
