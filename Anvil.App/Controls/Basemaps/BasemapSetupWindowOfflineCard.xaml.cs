using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.ViewModels;

namespace Anvil.Controls.Basemaps
{
	/// <summary>The map setup window's offline choice (see the XAML header).</summary>
	public sealed partial class BasemapSetupWindowOfflineCard : UserControl
	{
		public BasemapSetupWindowOfflineCard()
		{
			InitializeComponent();
		}

		public BasemapSetupViewModel ViewModel
		{
			get => (BasemapSetupViewModel)GetValue(ViewModelProperty);
			set => SetValue(ViewModelProperty, value);
		}

		public static readonly DependencyProperty ViewModelProperty =
			DependencyProperty.Register(nameof(ViewModel), typeof(BasemapSetupViewModel), typeof(BasemapSetupWindowOfflineCard), new PropertyMetadata(null));
	}
}
