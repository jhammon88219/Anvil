using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.ViewModels;

namespace Anvil.Controls.Windows
{
	/// <summary>
	/// The Outlook Discussion window (see the XAML header): SPC's forecast discussion for the outlook on the map.
	/// Bound to the coordinator <see cref="MapViewModel"/>; everything it shows is a MapViewModel property.
	/// </summary>
	public sealed partial class OutlookDiscussionWindow : UserControl
	{
		public OutlookDiscussionWindow()
		{
			InitializeComponent();
		}

		/// <summary>The coordinator view model; set by the host BEFORE the content loads.</summary>
		public MapViewModel ViewModel
		{
			get => (MapViewModel)GetValue(ViewModelProperty);
			set => SetValue(ViewModelProperty, value);
		}

		public static readonly DependencyProperty ViewModelProperty =
			DependencyProperty.Register(nameof(ViewModel), typeof(MapViewModel), typeof(OutlookDiscussionWindow), new PropertyMetadata(null));
	}
}
