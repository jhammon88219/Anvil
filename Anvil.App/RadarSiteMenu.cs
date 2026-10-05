using System;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Anvil.ViewModels;
using Windows.Foundation;

namespace Anvil
{
	/// <summary>
	/// The menu a map site key's CHEVRON opens (radar-sites.js posts <c>radarSiteMenu</c> →
	/// <see cref="WebMessageRouter.RadarSiteMenuRequested"/> → <see cref="MainWindow"/> → here). A real WinUI
	/// <see cref="MenuFlyout"/>, while the key itself stays HTML: ~160 keys must track the map every frame, which
	/// only the page can do, but a menu opens once and doesn't follow anything.
	/// </summary>
	/// <remarks>
	/// <code>
	///   ╭──────────────────────────────────────────────╮
	///   │ KTLX · Oklahoma City, OK      Online · NEXRAD │  ← header (disabled item; status words right)
	///   │ ──────────────────────────────────────────── │
	///   │ ▶  Load radar                   No recent data │  ← greyed when OFFLINE (reason on the right);
	///   │ ⧉  Open in Atlas                               │    "Unload radar" when it's the loaded site
	///   │ ──────────────────────────────────────────── │
	///   │ ⌂  Set as home                                 │  ← "Clear home site" when it is home
	///   │ ☆  Add to favorites                            │  ← "Remove from favorites" when starred
	///   ╰──────────────────────────────────────────────╯
	/// </code>
	/// Built fresh per open, so every label reads the row's state at that moment — nothing to keep in sync.
	/// "Load radar" is the SAME path a key's name click takes (<see cref="RadarViewModel.OnRadarSiteClicked"/>,
	/// which toggles), so the menu can't load a site any differently than the map does.
	/// ⚠️ <c>ShouldConstrainToRootBounds = false</c> (windowed), like every flyout here — else the owned panel
	/// windows paint over it.
	/// </remarks>
	public sealed class RadarSiteMenu
	{
		private readonly MapViewModel _vm;

		public RadarSiteMenu(MapViewModel vm) => _vm = vm;

		/// <summary>Open the menu for site <paramref name="id"/> with its top-left at <paramref name="at"/>, in
		/// <paramref name="target"/>'s coordinates (the WebView). An unknown id does nothing.</summary>
		public void Show(FrameworkElement target, string id, Point at)
		{
			var row = _vm.Radar.RadarSiteRows.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));
			if (row is null)
			{
				return;
			}

			var isLoaded = _vm.Radar.SelectedRadarOption?.Site == row.Site;
			var title = string.IsNullOrEmpty(row.State) ? $"{row.Id} · {row.Name}" : $"{row.Id} · {row.Name}, {row.State}";
			var menu = new MenuFlyout { ShouldConstrainToRootBounds = false };

			menu.Items.Add(new MenuFlyoutItem
			{
				Text = title,
				IsEnabled = false,
				KeyboardAcceleratorTextOverride = $"{row.StatusLabel} · {row.ClassLabel}",
			});
			menu.Items.Add(new MenuFlyoutSeparator());

			// An offline site can't be LOADED, but the loaded site going offline can still be unloaded.
			var canLoad = isLoaded || !row.IsOffline;
			var load = new MenuFlyoutItem
			{
				Text = isLoaded ? "Unload radar" : "Load radar",
				Icon = new SymbolIcon(isLoaded ? Symbol.Clear : Symbol.Play),
				IsEnabled = canLoad,
			};
			if (!canLoad)
			{
				load.KeyboardAcceleratorTextOverride = row.IsReplayDay ? "No data for this timeframe" : "No recent data";
			}
			load.Click += (_, _) => _vm.Radar.OnRadarSiteClicked(row.Id);
			menu.Items.Add(load);

			var atlas = new MenuFlyoutItem { Text = "Open in Atlas", Icon = new SymbolIcon(Symbol.Map) };
			atlas.Click += (_, _) => _vm.OpenAtlasOnSite(row);
			menu.Items.Add(atlas);

			menu.Items.Add(new MenuFlyoutSeparator());

			var home = new MenuFlyoutItem
			{
				Text = row.IsHome ? "Clear home site" : "Set as home",
				Icon = new SymbolIcon(Symbol.Home),
			};
			home.Click += (_, _) => _vm.SiteFavorites.ToggleHome(row);
			menu.Items.Add(home);

			var favorite = new MenuFlyoutItem
			{
				Text = row.IsFavorite ? "Remove from favorites" : "Add to favorites",
				Icon = new SymbolIcon(row.IsFavorite ? Symbol.UnFavorite : Symbol.Favorite),
			};
			favorite.Click += (_, _) => _vm.SiteFavorites.ToggleFavorite(row);
			menu.Items.Add(favorite);

			menu.ShowAt(target, new FlyoutShowOptions { Position = at, Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft });
		}
	}
}
