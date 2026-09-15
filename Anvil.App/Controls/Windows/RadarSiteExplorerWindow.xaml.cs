using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Anvil.Converters;
using Anvil.ViewModels;

namespace Anvil.Controls.Windows
{
	/// <summary>
	/// The Radar Site Explorer panel — a non-modal, searchable master–detail browser over the radar
	/// network, floating above the OverlayBar. Bound to the coordinator <see cref="MapViewModel"/> (like
	/// <see cref="SettingsWindow"/>): its visibility follows <see cref="MapViewModel.IsSiteExplorerOpen"/>
	/// and it reaches into <see cref="MapViewModel.SiteExplorer"/> for the list/detail. The close triangle
	/// and Load button are handled here in code-behind.
	/// </summary>
	public sealed partial class RadarSiteExplorerWindow : UserControl
	{
		public RadarSiteExplorerWindow()
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
			DependencyProperty.Register(nameof(ViewModel), typeof(MapViewModel), typeof(RadarSiteExplorerWindow), new PropertyMetadata(null, OnViewModelChanged));

		// The grouped list's CollectionViewSource can't x:Bind, so its Source is handed over here. ⚠️ This
		// metadata callback runs before x:Bind's own DP listener, so the sections exist by the time the
		// SelectedItem binding pushes the map-synced site — otherwise the ListView would null it out.
		private static void OnViewModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
		{
			var window = (RadarSiteExplorerWindow)d;
			((CollectionViewSource)window.Resources["SiteGroupsSource"]).Source =
				(e.NewValue as MapViewModel)?.SiteExplorer.SiteGroups;
		}

		// Favorite / home presentation. STATIC so the row template (x:DataType RadarSiteRow) can call them too.
		// E734 FavoriteStar / E735 FavoriteStarFill — ⚠️ unverified codepoints, check on first run.
		public static string StarGlyph(bool favorite) => favorite ? "" : "";
		public static string FavoriteToolTip(bool favorite) => favorite ? "Remove from favorites" : "Add to favorites";
		public static string HomeLabel(bool isHome) => isHome ? "Home site" : "Set as home";
		public static string HomeToolTip(bool isHome) => isHome
			? "Clear the home site"
			: "Make this the home site — the bar's Home key loads it";
		public static Visibility ShowIf(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

		// x:Bind helpers (bool → Visibility) — no value-converter lookup needed on a UserControl.
		public Visibility VisibleWhen(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
		public Visibility CollapsedWhen(bool value) => value ? Visibility.Collapsed : Visibility.Visible;
		public Visibility VisibleWhenText(string value) => string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;

		// The selected row's status-dot brush, for the detail pane (safe when nothing is selected).
		public Microsoft.UI.Xaml.Media.Brush SelectedStatusBrush(RadarSiteRow? row) =>
			(Microsoft.UI.Xaml.Media.Brush)_offlineToBrush.Convert(row?.IsOffline ?? false, typeof(Microsoft.UI.Xaml.Media.Brush), null!, null!);

		private readonly OfflineToBrushConverter _offlineToBrush = new();

		// The selection follows the map's radar site, which can be far down the list — keep it on screen,
		// both when the window opens onto it and when a marker click moves it while open.
		private void OnSiteListLoaded(object sender, RoutedEventArgs e) => ScrollToSelection((ListView)sender);
		private void OnSiteListSelectionChanged(object sender, SelectionChangedEventArgs e) => ScrollToSelection((ListView)sender);

		private static void ScrollToSelection(ListView list)
		{
			if (list.SelectedItem is { } item)
			{
				list.ScrollIntoView(item);
			}
		}

		// A row's star. The row is the Button's DataContext (the item template's).
		private void OnRowFavoriteClick(object sender, RoutedEventArgs e)
		{
			if (ViewModel is not null && (sender as FrameworkElement)?.DataContext is RadarSiteRow row)
			{
				ViewModel.SiteFavorites.ToggleFavorite(row);
			}
		}

		private void OnDetailFavoriteClick(object sender, RoutedEventArgs e)
		{
			if (ViewModel?.SiteExplorer.SelectedSite is { } row)
			{
				ViewModel.SiteFavorites.ToggleFavorite(row);
			}
		}

		private void OnDetailHomeClick(object sender, RoutedEventArgs e)
		{
			if (ViewModel?.SiteExplorer.SelectedSite is { } row)
			{
				ViewModel.SiteFavorites.ToggleHome(row);
			}
		}

		// Load the selected site's radar loop on the map, then close the panel.
		private void OnLoadClick(object sender, RoutedEventArgs e)
		{
			if (ViewModel is null)
			{
				return;
			}

			ViewModel.SiteExplorer.LoadOnMap();
			ViewModel.IsSiteExplorerOpen = false;
		}
	}
}
