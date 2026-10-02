using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.ViewModels;

namespace Anvil.Controls.Windows
{
	/// <summary>
	/// The Outlook Discussion window (see the XAML header): SPC's forecast discussion for the outlook on the map,
	/// parsed into labelled parts. Bound to the coordinator <see cref="MapViewModel"/>; what it shows is
	/// <see cref="MapViewModel.OutlookDiscussion"/>.
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

		// Statics so the DataTemplates can call them too (x:Bind in a template resolves against the ITEM).
		public static Visibility Vis(bool on) => on ? Visibility.Visible : Visibility.Collapsed;

		// Chevron right (collapsed) / down (open).
		public static string Chevron(bool open) => open ? "" : "";

		private void OnPreviousToggleClick(object sender, RoutedEventArgs e)
		{
			var d = ViewModel?.OutlookDiscussion;
			if (d is not null) d.IsPreviousExpanded = !d.IsPreviousExpanded;
		}
	}
}
