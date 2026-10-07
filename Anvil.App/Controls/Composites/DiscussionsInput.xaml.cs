using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.ViewModels;

namespace Anvil.Controls.Composites
{
	/// <summary>
	/// Mesoscale discussions (see the XAML header): card, one row per kind, what's in effect, and the door to the
	/// Mesoscale Discussions window. Bound to <see cref="MesoDiscussionsViewModel"/>; hosted by both the Now and
	/// Past windows. Every decision is the VM's — both clicks go through <see cref="MesoDiscussionsViewModel.OpenReader"/>.
	/// </summary>
	public sealed partial class DiscussionsInput : UserControl
	{
		public DiscussionsInput()
		{
			InitializeComponent();
		}

		private void OnRowClick(object sender, RoutedEventArgs e)
		{
			if (sender is FrameworkElement { Tag: DiscussionRow row }) { ViewModel?.OpenReader(row); }
		}

		private void OnOpenReaderClick(object sender, RoutedEventArgs e) => ViewModel?.OpenReader();

		/// <summary>x:Bind formatter for the counts.</summary>
		public static string Fmt(int count) => count.ToString(System.Globalization.CultureInfo.InvariantCulture);

		public Visibility HasText(string? value) => string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;

		public Visibility Shown(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

		/// <summary>The view model; bound from the host (NowCastTab / PastCastTab → ViewModel.Discussions).</summary>
		public MesoDiscussionsViewModel ViewModel
		{
			get => (MesoDiscussionsViewModel)GetValue(ViewModelProperty);
			set => SetValue(ViewModelProperty, value);
		}

		public static readonly DependencyProperty ViewModelProperty =
			DependencyProperty.Register(nameof(ViewModel), typeof(MesoDiscussionsViewModel), typeof(DiscussionsInput), new PropertyMetadata(null));
	}
}
