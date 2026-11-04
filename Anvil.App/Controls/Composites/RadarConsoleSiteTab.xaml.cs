using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.ViewModels;

namespace Anvil.Controls.Composites
{
	/// <summary>
	/// The radar site picker (home + favorites) as a tab on top of the floating radar console (moved off the tools strip,
	/// 2026-11-03; back from the console's short-lived top row, 2026-11-04). See the XAML header for the shape and the rules.
	/// </summary>
	public sealed partial class RadarConsoleSiteTab : UserControl
	{
		public RadarConsoleSiteTab()
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
			DependencyProperty.Register(nameof(ViewModel), typeof(MapViewModel), typeof(RadarConsoleSiteTab), new PropertyMetadata(null));

		// E80F Home / E735 FavoriteStarFill — ⚠️ unverified codepoints, see the XAML header. A site that is neither gets no
		// mark (only the FACE can show one — the lists hold only home and favorites).
		public static string PinGlyph(bool isHome, bool isFavorite) =>
			isHome ? "" : isFavorite ? "" : string.Empty;

		public static Visibility PinVisibility(bool isHome, bool isFavorite) =>
			isHome || isFavorite ? Visibility.Visible : Visibility.Collapsed;

		// ⚠️ Every click lands here, the already-loaded site included (that one re-flies). Then RE-ASSERT the face: the
		// picker set SelectedItem itself, and a refused pick changes no property to put it back.
		private void OnSitePicked(object? sender, object item)
		{
			if (ViewModel is not { } vm || item is not RadarSiteRow row)
			{
				return;
			}
			vm.SiteFavorites.Pick(row);
			SitePicker.SelectedItem = vm.SiteFavorites.LoadedSite;
		}

		private void OnOpenAtlasClick(object sender, RoutedEventArgs e)
		{
			SitePicker.CloseDropDown();
			ViewModel?.OpenAtlasOnSites(); // a site-picker door: lands on Radar sites
		}
	}
}
