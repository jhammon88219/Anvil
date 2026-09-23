using System;
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
			: "Make this the home site — it tops the site picker on the tools tier";
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

		// The age tile's colour, ramped by the newest scan's age: green while current, amber past the
		// glossary's RecentKnee, red past RadarSiteStatus.Staleness (where the site reads offline), grey with
		// no scan at all. ⚠️ LITERAL data colours — the same three the status square and the map key use, and
		// the same knees the hint's words name, so the colour and the sentence can't disagree.
		public static Microsoft.UI.Xaml.Media.Brush AgeBrush(double? minutes)
		{
			if (minutes is not double m)
			{
				return new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x6E, 0x76, 0x81));
			}
			if (m <= Anvil.Services.RadarGlossary.RecentKnee.TotalMinutes)
			{
				return new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x3F, 0xB9, 0x50));
			}
			if (m <= Anvil.Services.RadarSiteStatus.Staleness.TotalMinutes)
			{
				return new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0xD2, 0x99, 0x22));
			}
			return new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0xF8, 0x51, 0x49));
		}

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

		// A chip's ✕ — clears the whole filter group the chip stands for. The chip is the Button's
		// DataContext (the item template's), exactly like the row star below.
		private void OnChipClearClick(object sender, RoutedEventArgs e)
		{
			if (ViewModel is not null && (sender as FrameworkElement)?.DataContext is AtlasFilterChip chip)
			{
				ViewModel.RadarAtlas.ClearChip(chip);
			}
		}

		// "Clear all" — the filters, NOT the sort (see RadarAtlasViewModel.ClearAllFilters).
		private void OnClearFiltersClick(object sender, RoutedEventArgs e) =>
			ViewModel?.RadarAtlas.ClearAllFilters();

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

		// "Your use" Clear — confirm first (the dialog says how much goes), then clear one site or all of them.
		// ⚠️ XamlRoot is THIS control's: the Atlas is its own OS window, so the dialog must open over it.
		private async void OnClearUsageClick(object sender, RoutedEventArgs e)
		{
			if (ViewModel?.RadarAtlas is not { } atlas)
			{
				return;
			}

			var dialog = new Anvil.Dialogs.ClearSiteUsageDialog(
				atlas.ClearUsageTitle, atlas.ClearUsageBody, atlas.ClearAllUsageLabel)
			{
				XamlRoot = XamlRoot,
			};
			if (await dialog.ShowAsync() == ContentDialogResult.Primary)
			{
				atlas.ClearUsage(dialog.AllSites);
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
