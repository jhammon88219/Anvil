using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.ViewModels;

namespace Anvil.Controls.Composites
{
	/// <summary>
	/// Body of the Sites key's side-car flyout: home + favorite sites, click to load, plus a link to the full
	/// explorer. It raises events rather than touching the flyout — the host (<c>MainWindow</c>) owns the
	/// Flyout and the window flag.
	/// </summary>
	public sealed partial class FavoriteSitesFlyoutContent : UserControl
	{
		public FavoriteSitesFlyoutContent()
		{
			InitializeComponent();
		}

		public RadarSiteFavoritesViewModel ViewModel
		{
			get => (RadarSiteFavoritesViewModel)GetValue(ViewModelProperty);
			set => SetValue(ViewModelProperty, value);
		}

		public static readonly DependencyProperty ViewModelProperty =
			DependencyProperty.Register(nameof(ViewModel), typeof(RadarSiteFavoritesViewModel), typeof(FavoriteSitesFlyoutContent), new PropertyMetadata(null));

		/// <summary>A site row was clicked and its load has been started.</summary>
		public event EventHandler? SitePicked;

		/// <summary>"Open site explorer" was clicked.</summary>
		public event EventHandler? OpenExplorerRequested;

		public Visibility VisibleWhen(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
		public Visibility CollapsedWhen(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

		// E80F Home / E735 FavoriteStarFill — unverified codepoints, see the XAML header.
		public static string PinGlyph(bool isHome) => isHome ? "" : "";

		private void OnSiteClick(object sender, ItemClickEventArgs e)
		{
			if (ViewModel is null || e.ClickedItem is not RadarSiteRow row) return;

			ViewModel.LoadOnMap(row);
			SitePicked?.Invoke(this, EventArgs.Empty);
		}

		private void OnOpenExplorerClick(object sender, RoutedEventArgs e) =>
			OpenExplorerRequested?.Invoke(this, EventArgs.Empty);
	}
}
