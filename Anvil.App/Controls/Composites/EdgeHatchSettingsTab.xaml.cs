using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.ViewModels;

namespace Anvil.Controls.Composites
{
	/// <summary>
	/// EDGE-HATCH (experimental bolt-on): the Settings window's Edges tab (see the XAML header) over
	/// <see cref="EdgeHatchViewModel"/>.
	/// </summary>
	public sealed partial class EdgeHatchSettingsTab : UserControl
	{
		public EdgeHatchSettingsTab()
		{
			InitializeComponent();
		}

		/// <summary>The edge-hatch view model; bound from the host.</summary>
		public EdgeHatchViewModel ViewModel
		{
			get => (EdgeHatchViewModel)GetValue(ViewModelProperty);
			set => SetValue(ViewModelProperty, value);
		}

		public static readonly DependencyProperty ViewModelProperty =
			DependencyProperty.Register(nameof(ViewModel), typeof(EdgeHatchViewModel), typeof(EdgeHatchSettingsTab), new PropertyMetadata(null));
	}
}
