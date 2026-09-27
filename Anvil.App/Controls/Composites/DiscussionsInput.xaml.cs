using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.ViewModels;

namespace Anvil.Controls.Composites
{
	/// <summary>
	/// Mesoscale discussions (see the XAML header): card, one row per kind, the window's list and a reader.
	/// Bound to <see cref="MesoDiscussionsViewModel"/>; hosted by both the Now and Past windows. The code is
	/// the list's selection write-back and the reader's three buttons — every decision is the VM's.
	/// </summary>
	public sealed partial class DiscussionsInput : UserControl
	{
		public DiscussionsInput()
		{
			InitializeComponent();
		}

		// SelectedItem is bound ONE-WAY (VM → list); this is the other direction. The VM ignores a re-select
		// of what it already holds, so the echo of its own push is harmless.
		private void OnDiscussionSelectionChanged(object sender, SelectionChangedEventArgs e)
		{
			if (ViewModel is null) { return; }
			// ⚠️ A cleared source raises a selection change to null; that is the VM's own rebuild, not a pick.
			if (DiscussionList.SelectedItem is DiscussionRow row) { ViewModel.Selected = row; }
		}

		private void OnPreviousClick(object sender, RoutedEventArgs e) => ViewModel?.Step(-1);

		private void OnNextClick(object sender, RoutedEventArgs e) => ViewModel?.Step(+1);

		private void OnShowOnMapClick(object sender, RoutedEventArgs e) => ViewModel?.ShowSelectedOnMap();

		/// <summary>x:Bind formatter for the counts.</summary>
		public static string Fmt(int count) => count.ToString(System.Globalization.CultureInfo.InvariantCulture);

		/// <summary>A discussion not in effect at the map's moment is listed, but dimmed.</summary>
		public static double RowOpacity(bool inEffect) => inEffect ? 1.0 : 0.5;

		public Visibility HasText(string? value) => string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;

		public Visibility Shown(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

		public Visibility Hidden(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

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
