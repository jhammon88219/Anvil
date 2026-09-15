using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Anvil.Converters;
using Anvil.ViewModels;

namespace Anvil.Controls.Windows
{
	/// <summary>
	/// The Radar Atlas panel — a non-modal, searchable master–detail browser over the radar
	/// network, floating above the OverlayBar. Bound to the coordinator <see cref="MapViewModel"/> (like
	/// <see cref="SettingsWindow"/>): its visibility follows <see cref="MapViewModel.IsRadarAtlasOpen"/>
	/// and it reaches into <see cref="MapViewModel.RadarAtlas"/> for the list/detail. The close triangle
	/// and Load button are handled here in code-behind.
	/// </summary>
	public sealed partial class RadarAtlasWindow : UserControl
	{
		public RadarAtlasWindow()
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
			DependencyProperty.Register(nameof(ViewModel), typeof(MapViewModel), typeof(RadarAtlasWindow), new PropertyMetadata(null, OnViewModelChanged));

		// The grouped list's CollectionViewSource can't x:Bind, so its Source is handed over here. ⚠️ This
		// metadata callback runs before x:Bind's own DP listener, so the sections exist by the time the
		// SelectedItem binding pushes the map-synced site — otherwise the ListView would null it out.
		private static void OnViewModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
		{
			var window = (RadarAtlasWindow)d;
			((CollectionViewSource)window.Resources["SiteGroupsSource"]).Source =
				(e.NewValue as MapViewModel)?.RadarAtlas.SiteGroups;
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

		// The row's network bar (the map key's right zone): its data fill, and which of the three glyphs shows.
		// ⚠️ name must be a RadarSiteClass member name ('Operational' / 'Tdwr' / 'Research').
		public static Microsoft.UI.Xaml.Media.Brush NetworkBrush(Anvil.Models.RadarSiteClass siteClass) =>
			SiteNetworkToBrushConverter.For(siteClass);
		public static Visibility ClassVisible(Anvil.Models.RadarSiteClass siteClass, string name) =>
			siteClass.ToString() == name ? Visibility.Visible : Visibility.Collapsed;

		// x:Bind helpers (bool → Visibility) — no value-converter lookup needed on a UserControl.
		public Visibility VisibleWhen(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
		public Visibility CollapsedWhen(bool value) => value ? Visibility.Collapsed : Visibility.Visible;
		public Visibility VisibleWhenText(string value) => string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;

		// The detail pane's status-dot brush. Bound to SelectedSite.Availability, so it tracks a status change
		// on the selected row, not just a change of selection.
		public static Microsoft.UI.Xaml.Media.Brush StatusBrush(Anvil.Models.SiteAvailability availability) =>
			SiteAvailabilityToBrushConverter.For(availability);

		// The row's ICAO + name + home mark sit INSIDE the status block, so their brush follows the fill.
		public static Microsoft.UI.Xaml.Media.Brush StatusInk(Anvil.Models.SiteAvailability availability) =>
			SiteAvailabilityToBrushConverter.InkFor(availability);

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
			if (ViewModel?.RadarAtlas.SelectedSite is { } row)
			{
				ViewModel.SiteFavorites.ToggleFavorite(row);
			}
		}

		private void OnDetailHomeClick(object sender, RoutedEventArgs e)
		{
			if (ViewModel?.RadarAtlas.SelectedSite is { } row)
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

			ViewModel.RadarAtlas.LoadOnMap();
			ViewModel.IsRadarAtlasOpen = false;
		}
	}
}
