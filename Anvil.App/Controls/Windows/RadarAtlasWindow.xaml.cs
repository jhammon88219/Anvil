using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Anvil.Converters;
using Anvil.ViewModels;

namespace Anvil.Controls.Windows
{
	/// <summary>
	/// The Anvil Atlas — everything you can point the radar at: a title band, then two tabs, Radar sites (a
	/// searchable master–detail browser over the radar network, this file) and Past events
	/// (<see cref="Composites.PastEventsAtlasTab"/>). Bound to the coordinator <see cref="MapViewModel"/> (like
	/// <see cref="SettingsWindow"/>): its visibility follows <see cref="MapViewModel.IsRadarAtlasOpen"/>, its tab
	/// <see cref="MapViewModel.AtlasTabIndex"/>, and it reaches into <see cref="MapViewModel.RadarAtlas"/> for the
	/// sites list/detail. The Load button is handled here in code-behind.
	/// </summary>
	public sealed partial class RadarAtlasWindow : UserControl
	{
		public RadarAtlasWindow()
		{
			InitializeComponent();
		}

		// The strip's content, in AtlasTabIndex order. ⚠️ E81C (History) is an UNVERIFIED codepoint — check it
		// renders; EC05 is the Atlas key's own glyph.
		public System.Collections.ObjectModel.ObservableCollection<Primitives.TabEntry> Tabs { get; } = new()
		{
			new() { Glyph = "", Label = "Radar sites", Tooltip = "Every radar the app can load: status, scan, NWS notices and your use" },
			new() { Glyph = "", Label = "Past events", Tooltip = "Curated and saved storms to replay in PastCast" },
		};

		public Visibility TabVisibility(int selected, int tab) => selected == tab ? Visibility.Visible : Visibility.Collapsed;

		public string Count(int n) => n.ToString(System.Globalization.CultureInfo.InvariantCulture);

		/// <summary>"SCAN MODE · CLEAR-AIR" — the scan tile's label keeps the regime the old card spelled out.</summary>
		public static string ScanModeLabel(string regime) =>
			string.IsNullOrEmpty(regime) || regime == "Scan pattern" ? "SCAN MODE" : $"SCAN MODE · {regime.ToUpperInvariant()}";

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
			window.AttachHistory((e.NewValue as MapViewModel)?.RadarAtlas.History);
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
		public Visibility VisibleWhenBoth(bool a, bool b) => a && b ? Visibility.Visible : Visibility.Collapsed;

		// The NWS STATUS section's radar-state dot: the RADAR's own report, not availability. ⚠️ LITERAL data
		// colours — the same green / amber / red / grey as AgeBrush and the status square.
		public static Microsoft.UI.Xaml.Media.Brush NwsLevelBrush(RadarNwsLevel level) => level switch
		{
			RadarNwsLevel.Ok => new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x3F, 0xB9, 0x50)),
			RadarNwsLevel.Degraded => new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0xD2, 0x99, 0x22)),
			RadarNwsLevel.Down => new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0xF8, 0x51, 0x49)),
			_ => new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x6E, 0x76, 0x81)),
		};

		// E70D ChevronDown (folded) / E70E ChevronUp (unfolded).
		public static string ExpandGlyph(bool expanded) => expanded ? "" : "";

		// ── History sections (RadarSiteHistoryViewModel) ──
		// ⚠️ LITERAL data colours, the same green / amber / red / grey as NwsLevelBrush and AgeBrush.
		private static Microsoft.UI.Xaml.Media.SolidColorBrush Literal(byte r, byte g, byte b) =>
			new(Microsoft.UI.ColorHelper.FromArgb(0xFF, r, g, b));

		public static Microsoft.UI.Xaml.Media.Brush UptimeCellBrush(UptimeCellLevel level) => level switch
		{
			UptimeCellLevel.Up => Literal(0x3F, 0xB9, 0x50),
			UptimeCellLevel.Partial => Literal(0xD2, 0x99, 0x22),
			UptimeCellLevel.Down => Literal(0xF8, 0x51, 0x49),
			_ => Literal(0x6E, 0x76, 0x81),
		};

		public static Microsoft.UI.Xaml.Media.Brush HoleBrush(Anvil.Models.UptimeHoleKind kind) =>
			kind == Anvil.Models.UptimeHoleKind.Down ? Literal(0xF8, 0x51, 0x49) : Literal(0xD2, 0x99, 0x22);

		// Scan-pattern bar shades, most-used first: a blue for the top pattern, then steps of grey. Data colours,
		// readable on both themes.
		public static Microsoft.UI.Xaml.Media.Brush ShareBrush(int index) => index switch
		{
			0 => Literal(0x58, 0x8B, 0xE0),
			1 => Literal(0x8B, 0x94, 0x9E),
			2 => Literal(0x6E, 0x76, 0x81),
			_ => Literal(0x48, 0x4F, 0x58),
		};

		private void OnHistoryMessagesMoreClick(object sender, RoutedEventArgs e) =>
			ViewModel?.RadarAtlas.History.ToggleMessages();

		// The scan-pattern bar: one star column per pattern, sized by its share. Rebuilt in code because a
		// proportional row has no WinUI panel (EqualCellsPanel is EQUAL cells) and it's one bar.
		private RadarSiteHistoryViewModel? _history;

		private void AttachHistory(RadarSiteHistoryViewModel? history)
		{
			if (_history is not null) _history.PropertyChanged -= OnHistoryChanged;
			_history = history;
			if (_history is not null) _history.PropertyChanged += OnHistoryChanged;
			RebuildScanBar();
		}

		private void OnHistoryChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
		{
			if (e.PropertyName == nameof(RadarSiteHistoryViewModel.ScanShares)) RebuildScanBar();
		}

		private void RebuildScanBar()
		{
			ScanBar.Children.Clear();
			ScanBar.ColumnDefinitions.Clear();
			if (_history is null) return;
			foreach (var s in _history.ScanShares)
			{
				ScanBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(s.Share, GridUnitType.Star) });
				var segment = new Border { Background = ShareBrush(s.Index) };
				ToolTipService.SetToolTip(segment, s.Label);
				Grid.SetColumn(segment, ScanBar.ColumnDefinitions.Count - 1);
				ScanBar.Children.Add(segment);
			}
		}

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

		// NWS STATUS → Re-check: one check for every site; the VM enforces the 5-min cooldown (the button's
		// IsEnabled mirrors it, but a click racing the tick still lands on the VM's guard).
		private void OnNwsRecheckClick(object sender, RoutedEventArgs e) =>
			_ = ViewModel?.RadarAtlas.NwsStatus.CheckAsync();


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
