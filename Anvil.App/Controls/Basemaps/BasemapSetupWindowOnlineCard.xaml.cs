using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.ViewModels;

namespace Anvil.Controls.Basemaps
{
	/// <summary>The map setup window's online choice (see the XAML header).</summary>
	public sealed partial class BasemapSetupWindowOnlineCard : UserControl
	{
		public BasemapSetupWindowOnlineCard()
		{
			InitializeComponent();
		}

		public BasemapSetupViewModel ViewModel
		{
			get => (BasemapSetupViewModel)GetValue(ViewModelProperty);
			set => SetValue(ViewModelProperty, value);
		}

		public static readonly DependencyProperty ViewModelProperty =
			DependencyProperty.Register(nameof(ViewModel), typeof(BasemapSetupViewModel), typeof(BasemapSetupWindowOnlineCard), new PropertyMetadata(null));
	}
}
