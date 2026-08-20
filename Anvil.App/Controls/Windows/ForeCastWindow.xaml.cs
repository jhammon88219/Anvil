using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.ViewModels;

namespace Anvil.Controls.Sections
{
	/// <summary>
	/// The ForeCast settings panel (see the XAML header), hosted in its own OS window by
	/// <see cref="WindowManager"/> and driven by <see cref="MapViewModel.IsForeWindowOpen"/>. Bound to the
	/// coordinator <see cref="MapViewModel"/>.
	/// </summary>
	public sealed partial class ForeCastWindow : UserControl
	{
		public ForeCastWindow()
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
			DependencyProperty.Register(nameof(ViewModel), typeof(MapViewModel), typeof(ForeCastWindow), new PropertyMetadata(null));
	}
}
