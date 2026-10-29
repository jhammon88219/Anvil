using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.ViewModels;

namespace Anvil.Controls.TemporalPanes.DragMoveLayerExpanders
{
	/// <summary>
	/// Mesoscale discussions (see the XAML header): card, one row per kind, what's in effect, and the door to the
	/// Mesoscale Discussions window. Bound to <see cref="MesoDiscussionsViewModel"/>; hosted by both the Now and
	/// Past windows. Every decision is the VM's — both clicks go through <see cref="MesoDiscussionsViewModel.OpenReader"/>.
	/// </summary>
	public sealed partial class DragMoveLayerExpanderMesoDiscussionsBody : UserControl
	{
		public DragMoveLayerExpanderMesoDiscussionsBody()
		{
			InitializeComponent();
		}

		private void OnRowClick(object sender, RoutedEventArgs e)
		{
			if (sender is FrameworkElement { Tag: DiscussionRow row }) { ViewModel?.FlyTo(row); }
		}

		private void OnOpenReaderClick(object sender, RoutedEventArgs e) => ViewModel?.OpenReader();

		/// <summary>x:Bind formatter for the counts.</summary>
		public static string Fmt(int count) => count.ToString(System.Globalization.CultureInfo.InvariantCulture);

		public Visibility Shown(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

		/// <summary>The view model; bound from the host (TemporalPaneNowCastContent / TemporalPanePastCastContent → ViewModel.Discussions).</summary>
		public MesoDiscussionsViewModel ViewModel
		{
			get => (MesoDiscussionsViewModel)GetValue(ViewModelProperty);
			set => SetValue(ViewModelProperty, value);
		}

		public static readonly DependencyProperty ViewModelProperty =
			DependencyProperty.Register(nameof(ViewModel), typeof(MesoDiscussionsViewModel), typeof(DragMoveLayerExpanderMesoDiscussionsBody), new PropertyMetadata(null));
	}
}
