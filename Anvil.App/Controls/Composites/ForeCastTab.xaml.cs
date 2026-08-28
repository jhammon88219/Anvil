using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.ViewModels;

namespace Anvil.Controls.Composites
{
	/// <summary>
	/// The ForeCast tab body of the Timeframe window (see the XAML header) — the SPC outlook day/product
	/// selectors, opacity and legend. Bound to the coordinator <see cref="MapViewModel"/>; the selectors
	/// drive <c>ViewModel.Outlook</c>.
	/// </summary>
	public sealed partial class ForeCastTab : UserControl
	{
		public ForeCastTab()
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
			DependencyProperty.Register(nameof(ViewModel), typeof(MapViewModel), typeof(ForeCastTab), new PropertyMetadata(null));
	}
}
