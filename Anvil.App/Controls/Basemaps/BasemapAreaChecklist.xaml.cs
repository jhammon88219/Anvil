using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.ViewModels;

namespace Anvil.Controls.Basemaps
{
	/// <summary>The offline map's Area checklist (see the XAML header).</summary>
	public sealed partial class BasemapAreaChecklist : UserControl
	{
		public BasemapAreaChecklist()
		{
			InitializeComponent();
		}

		public BasemapSetupViewModel ViewModel
		{
			get => (BasemapSetupViewModel)GetValue(ViewModelProperty);
			set => SetValue(ViewModelProperty, value);
		}

		public static readonly DependencyProperty ViewModelProperty =
			DependencyProperty.Register(nameof(ViewModel), typeof(BasemapSetupViewModel), typeof(BasemapAreaChecklist), new PropertyMetadata(null));

		public Visibility NothingTicked(bool hasAnyArea) => hasAnyArea ? Visibility.Collapsed : Visibility.Visible;
	}
}
