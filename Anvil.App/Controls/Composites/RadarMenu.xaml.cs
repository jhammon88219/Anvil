using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.ViewModels;

namespace Anvil.Controls.Composites
{
	/// <summary>
	/// The top menu bar's "Radar" item: the radar site picker (home + favorites) drawn as a menu title. See the XAML header
	/// for the shape and the rules.
	/// </summary>
	public sealed partial class RadarMenu : UserControl
	{
		public RadarMenu()
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
			DependencyProperty.Register(nameof(ViewModel), typeof(MapViewModel), typeof(RadarMenu), new PropertyMetadata(null));

		// E80F Home / E735 FavoriteStarFill — ⚠️ unverified codepoints, see the XAML header.
		public static string PinGlyph(bool isHome, bool isFavorite) =>
			isHome ? "" : isFavorite ? "" : string.Empty;

		public string UnloadText(RadarSiteRow? loaded) => loaded is null ? string.Empty : $"Unload {loaded.Id}";

		public Visibility UnloadVisibility(RadarSiteRow? loaded) => loaded is null ? Visibility.Collapsed : Visibility.Visible;

		// ⚠️ Every click lands here, the already-loaded site included (that one re-flies). Then RE-ASSERT the selection: the
		// picker set SelectedItem itself, and a refused pick changes no property to put it back.
		private void OnSitePicked(object? sender, object item)
		{
			if (ViewModel is not { } vm || item is not RadarSiteRow row)
			{
				return;
			}
			vm.SiteFavorites.Pick(row);
			RadarMenuSitePicker.SelectedItem = vm.SiteFavorites.LoadedSite;
		}

		// Unload = clicking the loaded site's own key (RadarSiteMenu's "Unload radar" does the same).
		private void OnUnloadClick(object sender, RoutedEventArgs e)
		{
			RadarMenuSitePicker.CloseDropDown();
			if (ViewModel?.SiteFavorites.LoadedSite is { } loaded)
			{
				ViewModel.Radar.OnRadarSiteClicked(loaded.Id);
			}
		}

		private void OnOpenAtlasClick(object sender, RoutedEventArgs e)
		{
			RadarMenuSitePicker.CloseDropDown();
			ViewModel?.OpenAtlasOnSites(); // a site-picker door: lands on Radar sites
		}
	}
}
