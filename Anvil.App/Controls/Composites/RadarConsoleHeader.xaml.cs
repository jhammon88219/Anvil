using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.ViewModels;

namespace Anvil.Controls.Composites
{
	/// <summary>
	/// The floating radar console's TOP ROW: the site picker (home + favorites) and Inspect + the rings/ruler picker (the
	/// network picker moved on to the map-tools rail, 2026-11-04). See the XAML header for the shape and the rules.
	/// </summary>
	public sealed partial class RadarConsoleHeader : UserControl
	{
		public RadarConsoleHeader()
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
			DependencyProperty.Register(nameof(ViewModel), typeof(MapViewModel), typeof(RadarConsoleHeader), new PropertyMetadata(null));

		// The PastCast loading screen's lock (RadarViewModel.AreControlsLocked) greys the pickers, as it did on the strip.
		public bool Free(bool locked) => !locked;

		// ===== The right group is EDGE-ALIGNED to the console row below (the section rules, the user's call) =====
		//                 [⌂ KTLX ˅] ············ ⌕ ◎▴
		//                          the longest scan line's text end ┤
		// The site picker centres by layout (on play, the console's midpoint); the right group takes a MEASURED margin, so it
		// follows the VCP text as it changes. A sub-pixel change is a no-op, so calling it from layout settles.
		// (The left group — the network picker — moved to the map-tools rail, 2026-11-04.)

		/// <summary>Put the rings/ruler picker's right edge on <paramref name="scanRight"/>, in <paramref name="root"/>'s
		/// coordinates.</summary>
		public void AlignToConsole(double scanRight, UIElement root)
		{
			if (double.IsNaN(scanRight) || ActualWidth <= 0)
			{
				return;
			}
			var origin = TransformToVisual(root).TransformPoint(default).X;
			var right = Math.Max(0, origin + ActualWidth - scanRight);
			if (Math.Abs(right - RadarTools.Margin.Right) > 0.5)
			{
				RadarTools.Margin = new Thickness(0, 0, right, 0);
			}
		}

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
