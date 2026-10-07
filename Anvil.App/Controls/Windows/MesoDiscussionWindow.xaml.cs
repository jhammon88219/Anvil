using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.ViewModels;

namespace Anvil.Controls.Windows
{
	/// <summary>
	/// The Mesoscale Discussions window (see the XAML header): the window's discussions, grouped by the map's moment,
	/// and a reader for the selected one. Bound to the coordinator <see cref="MapViewModel"/>; what it shows is
	/// <see cref="MapViewModel.Discussions"/>. The code is the list's clicks and the reader's three buttons.
	/// </summary>
	public sealed partial class MesoDiscussionWindow : UserControl
	{
		public MesoDiscussionWindow()
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
			DependencyProperty.Register(nameof(ViewModel), typeof(MapViewModel), typeof(MesoDiscussionWindow), new PropertyMetadata(null));

		private void OnRowClick(object sender, RoutedEventArgs e)
		{
			if (sender is FrameworkElement { Tag: DiscussionRow row } && ViewModel is { } vm) { vm.Discussions.Selected = row; }
		}

		private void OnPreviousClick(object sender, RoutedEventArgs e) => ViewModel?.Discussions.Step(-1);

		private void OnNextClick(object sender, RoutedEventArgs e) => ViewModel?.Discussions.Step(+1);

		private void OnShowOnMapClick(object sender, RoutedEventArgs e) => ViewModel?.Discussions.ShowSelectedOnMap();

		// Statics so the row template can call them too (x:Bind in a template resolves against the ITEM).
		public static Visibility Vis(bool on) => on ? Visibility.Visible : Visibility.Collapsed;

		public static Visibility Hidden(bool on) => on ? Visibility.Collapsed : Visibility.Visible;

		public static Visibility HasText(string? value) => string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;

		/// <summary>Earlier / later than the map's moment = dimmed.</summary>
		public static double RowOpacity(bool inEffect) => inEffect ? 1.0 : 0.55;

		/// <summary>The selected row's fill shows; the rest's is transparent.</summary>
		public static double SelectedOpacity(bool selected) => selected ? 1.0 : 0.0;
	}
}
