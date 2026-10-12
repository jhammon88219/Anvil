using System;
using System.Linq;
using Microsoft.UI.Xaml.Controls;
using Anvil.ViewModels;

namespace Anvil
{
	/// <summary>
	/// What the menu a map site key's CHEVRON opens SAYS and DOES (radar-sites.js posts <c>radarSiteMenu</c> →
	/// <see cref="WebMessageRouter.RadarSiteMenuRequested"/> → <see cref="MainWindow"/>). The menu ITSELF is
	/// declared in MainWindow.xaml (<c>SiteMenu</c>, attached to the map WebView) — UI is defined before runtime,
	/// never built during it — so this class holds no UI: <see cref="Describe"/> reads a site's state into a
	/// <see cref="RadarSiteMenuState"/> for MainWindow to show, and the four actions are what its items do.
	/// A real WinUI <see cref="MenuFlyout"/>, while the key itself stays HTML: ~160 keys must track the map every
	/// frame, which only the page can do, but a menu opens once and doesn't follow anything.
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
	/// Described fresh per open, so every label reads the row's state at that moment — nothing to keep in sync.
	/// "Load radar" is the SAME path a key's name click takes (<see cref="RadarViewModel.OnRadarSiteClicked"/>,
	/// which toggles), so the menu can't load a site any differently than the map does.
	/// </remarks>
	public sealed class RadarSiteMenu
	{
		private readonly MapViewModel _vm;

		public RadarSiteMenu(MapViewModel vm) => _vm = vm;

		/// <summary>The menu's contents for site <paramref name="id"/> right now, or null for an unknown id.</summary>
		public RadarSiteMenuState? Describe(string id)
		{
			var row = _vm.Radar.RadarSiteRows.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));
			if (row is null)
			{
				return null;
			}

			var isLoaded = _vm.Radar.SelectedRadarOption?.Site == row.Site;
			// An offline site can't be LOADED, but the loaded site going offline can still be unloaded.
			var canLoad = isLoaded || !row.IsOffline;
			return new RadarSiteMenuState(
				Row: row,
				Title: string.IsNullOrEmpty(row.State) ? $"{row.Id} · {row.Name}" : $"{row.Id} · {row.Name}, {row.State}",
				Status: $"{row.StatusLabel} · {row.ClassLabel}",
				LoadText: isLoaded ? "Unload radar" : "Load radar",
				LoadSymbol: isLoaded ? Symbol.Clear : Symbol.Play,
				CanLoad: canLoad,
				LoadReason: canLoad ? string.Empty : row.IsReplayDay ? "No data for this timeframe" : "No recent data",
				HomeText: row.IsHome ? "Clear home site" : "Set as home",
				FavoriteText: row.IsFavorite ? "Remove from favorites" : "Add to favorites",
				FavoriteSymbol: row.IsFavorite ? Symbol.UnFavorite : Symbol.Favorite);
		}

		public void Load(RadarSiteRow row) => _vm.Radar.OnRadarSiteClicked(row.Id);
		public void OpenInAtlas(RadarSiteRow row) => _vm.OpenAtlasOnSite(row);
		public void ToggleHome(RadarSiteRow row) => _vm.SiteFavorites.ToggleHome(row);
		public void ToggleFavorite(RadarSiteRow row) => _vm.SiteFavorites.ToggleFavorite(row);
	}

	/// <summary>One open of the site menu: the row it is about, and every label / icon / enabled state its
	/// declared items show (see <see cref="RadarSiteMenu.Describe"/>).</summary>
	public sealed record RadarSiteMenuState(
		RadarSiteRow Row,
		string Title,
		string Status,
		string LoadText,
		Symbol LoadSymbol,
		bool CanLoad,
		string LoadReason,
		string HomeText,
		string FavoriteText,
		Symbol FavoriteSymbol);
}
