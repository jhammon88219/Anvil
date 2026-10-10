using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Anvil.Models;
using Anvil.Services;
using Anvil.ViewModels;
using Xunit;
using static Anvil.Tests.TemporalWindowPersistenceTests;

namespace Anvil.Tests
{
	/// <summary>
	/// "Show only my home and favorite sites" (app note #1, 2026-10-10): ONE setting behind two switches (Settings →
	/// Radar → Radar site keys and the site picker's footer), pushed to the page as the flag + the id list TOGETHER,
	/// re-pushed whenever home or a star changes so the map never shows a stale set.
	/// </summary>
	public class FavoriteSitesOnlyTests
	{
		private static readonly RadarSite Ktlx = new("KTLX", "Norman", 35.333, -97.278);
		private static readonly RadarSite Kinx = new("KINX", "Tulsa", 36.175, -95.564);
		private static readonly RadarSite Kfws = new("KFWS", "Dallas/Ft Worth", 32.573, -97.303);

		private sealed class Harness
		{
			public readonly AppSettings Settings = new() { HomeSiteId = "KTLX", FavoriteSiteIds = new() { "KINX" } };
			public readonly List<(bool On, string Json)> Pushes = new();
			public readonly RadarViewModel Radar;
			public readonly RadarSiteFavoritesViewModel Vm;

			public Harness()
			{
				var svc = Null<ISettingsService>.Create(new() { ["get_Settings"] = _ => Settings });
				var map = Null<IMapService>.Create(new()
				{
					["SetFavoriteSitesOnlyAsync"] = a => { Pushes.Add(((bool)a![0]!, (string)a[1]!)); return Task.CompletedTask; },
				});
				var sites = Null<IRadarSiteProvider>.Create(new() { ["GetSites"] = _ => (IReadOnlyList<RadarSite>)new[] { Ktlx, Kinx, Kfws } });
				Radar = new RadarViewModel(map, sites, Null<ILevel2RadarService>.Create(), Null<IDowEventProvider>.Create(), svc, null);
				Vm = new RadarSiteFavoritesViewModel(Radar, svc, map);
			}

			public RadarSiteRow Row(string id) => Radar.RadarSiteRows.Single(r => r.Id == id);
		}

		private static string[] Ids(string json) => System.Text.Json.JsonSerializer.Deserialize<string[]>(json)!.OrderBy(s => s).ToArray();

		[Fact]
		public async Task MapReady_PushesTheFlagWithHomeAndFavorites()
		{
			var h = new Harness();
			h.Vm.ShowOnlyFavoritesOnMap = true; // before map-ready: held, not pushed
			Assert.Empty(h.Pushes);

			await h.Vm.OnMapsReadyAsync();

			var (on, json) = Assert.Single(h.Pushes);
			Assert.True(on);
			Assert.Equal(new[] { "KINX", "KTLX" }, Ids(json));
		}

		[Fact]
		public async Task TheSwitch_PersistsAndPushesBothWays()
		{
			var h = new Harness();
			await h.Vm.OnMapsReadyAsync();
			h.Pushes.Clear();

			h.Vm.ShowOnlyFavoritesOnMap = true;
			Assert.True(h.Settings.ShowOnlyFavoriteSites);
			h.Vm.ShowOnlyFavoritesOnMap = false;
			Assert.False(h.Settings.ShowOnlyFavoriteSites);

			Assert.Equal(new[] { true, false }, h.Pushes.Select(p => p.On));
		}

		[Fact]
		public async Task StarringOrHoming_RePushesTheNewSet()
		{
			var h = new Harness();
			h.Vm.ShowOnlyFavoritesOnMap = true;
			await h.Vm.OnMapsReadyAsync();
			h.Pushes.Clear();

			h.Vm.ToggleFavorite(h.Row("KFWS"));
			Assert.Equal(new[] { "KFWS", "KINX", "KTLX" }, Ids(h.Pushes.Last().Json));

			h.Vm.ToggleHome(h.Row("KTLX")); // clears home
			Assert.Equal(new[] { "KFWS", "KINX" }, Ids(h.Pushes.Last().Json));
			Assert.All(h.Pushes, p => Assert.True(p.On));
		}

		[Fact]
		public void TheNote_SaysSoWhenThereIsNothingToShow()
		{
			var h = new Harness();
			Assert.StartsWith("Star sites", h.Vm.FavoritesOnlyNote);

			h.Vm.ToggleFavorite(h.Row("KINX"));
			h.Vm.ToggleHome(h.Row("KTLX"));

			Assert.True(h.Vm.IsEmpty);
			Assert.StartsWith("You have no home site or favorites", h.Vm.FavoritesOnlyNote);
		}
	}
}
