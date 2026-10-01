using System;
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
	/// <see cref="BasemapViewModel"/>: the Map key's hide is session-only and OVERRIDES the ticks without
	/// changing them; ticks persist and restore (the legacy "roads" id expands to every road kind); nothing
	/// reaches the page before map-ready, and the whole state is replayed there.
	/// </summary>
	public class BasemapViewModelTests
	{
		private sealed class Rig
		{
			public readonly AppSettings Settings;
			public readonly List<(bool Hidden, string[] Off)> Pushes = new();
			public readonly BasemapViewModel Vm;

			public Rig(AppSettings? settings = null)
			{
				Settings = settings ?? new AppSettings();
				var map = Null<IMapService>.Create(new()
				{
					["SetBasemapAsync"] = a =>
					{
						Pushes.Add(((bool)a![0]!, ((IReadOnlyList<string>)a[1]!).ToArray()));
						return Task.CompletedTask;
					},
				});
				var svc = Null<ISettingsService>.Create(new() { ["get_Settings"] = _ => Settings });
				Vm = new BasemapViewModel(map, svc);
			}

			public BasemapGroupOption Group(string id) => Vm.Groups.Single(g => g.Id == id);
		}

		[Fact]
		public void Defaults_ShowEverything()
		{
			var r = new Rig();
			Assert.True(r.Vm.IsMapShown);
			Assert.All(r.Vm.Groups, g => Assert.True(g.IsShown));
			Assert.Equal(BasemapGroups.All.Select(g => g.Id), r.Vm.Groups.Select(g => g.Id));
		}

		[Fact]
		public void Untick_PersistsTheUntickedSet_AndRestores()
		{
			var r = new Rig();
			r.Group(BasemapGroups.MinorRoads).IsShown = false;
			r.Group(BasemapGroups.Land).IsShown = false;
			Assert.Equal(new[] { BasemapGroups.Land, BasemapGroups.MinorRoads }, r.Settings.HiddenBasemapGroups);

			var again = new Rig(r.Settings);
			Assert.False(again.Group(BasemapGroups.MinorRoads).IsShown);
			Assert.False(again.Group(BasemapGroups.Land).IsShown);
			Assert.True(again.Group(BasemapGroups.Highways).IsShown);
			Assert.True(again.Group(BasemapGroups.Water).IsShown);
		}

		[Fact]
		public void LegacyRoads_InSettings_UnticksEveryRoadKind()
		{
			var s = new AppSettings { HiddenBasemapGroups = new() { BasemapGroups.LegacyRoads, BasemapGroups.Water } };
			var r = new Rig(s);
			Assert.All(BasemapGroups.Roads, id => Assert.False(r.Group(id).IsShown));
			Assert.False(r.Group(BasemapGroups.Water).IsShown);
			Assert.True(r.Group(BasemapGroups.Land).IsShown);
			Assert.DoesNotContain(BasemapGroups.LegacyRoads, r.Vm.OffGroups);
		}

		[Fact]
		public void RoadKinds_SitUnderOneRoadsHeading()
		{
			var r = new Rig();
			var roads = r.Vm.Groups.Where(g => g.IsSub).Select(g => g.Id);
			Assert.Equal(BasemapGroups.Roads, roads);
			Assert.Equal("Roads", r.Group(BasemapGroups.Highways).Heading);
			Assert.Single(r.Vm.Groups, g => g.Heading is not null);
		}

		[Fact]
		public void KeepOnlyHighways_SendsEveryOtherRoadKind()
		{
			var r = new Rig();
			foreach (var id in BasemapGroups.Roads.Where(id => id != BasemapGroups.Highways))
			{
				r.Group(id).IsShown = false;
			}
			Assert.Equal(new[] { BasemapGroups.MajorRoads, BasemapGroups.MinorRoads, BasemapGroups.Paths, BasemapGroups.Rail },
				r.Vm.OffGroups);
		}

		[Fact]
		public void UnknownGroupInSettings_IsIgnored()
		{
			var s = new AppSettings { HiddenBasemapGroups = new() { "bogus", BasemapGroups.Names } };
			var r = new Rig(s);
			Assert.False(r.Group(BasemapGroups.Names).IsShown);
			Assert.Equal(new[] { BasemapGroups.Names }, r.Vm.OffGroups);
		}

		[Fact]
		public void Hide_IsNotPersisted_AndKeepsTheTicks()
		{
			var r = new Rig();
			r.Group(BasemapGroups.Buildings).IsShown = false;
			r.Vm.IsMapShown = false;

			Assert.Equal(new[] { BasemapGroups.Buildings }, r.Settings.HiddenBasemapGroups); // hide wrote nothing
			Assert.True(new Rig(r.Settings).Vm.IsMapShown);                                   // relaunch = shown

			r.Vm.IsMapShown = true;
			Assert.False(r.Group(BasemapGroups.Buildings).IsShown);
			Assert.True(r.Group(BasemapGroups.Highways).IsShown);
		}

		[Fact]
		public async Task NothingPushedBeforeReady_ThenTheWholeStateIsReplayed()
		{
			var r = new Rig();
			r.Vm.IsMapShown = false;
			r.Group(BasemapGroups.Water).IsShown = false;
			Assert.Empty(r.Pushes);

			await r.Vm.OnMapsReadyAsync();
			var p = Assert.Single(r.Pushes);
			Assert.True(p.Hidden);
			Assert.Equal(new[] { BasemapGroups.Water }, p.Off);

			r.Vm.IsMapShown = true;
			Assert.False(r.Pushes[^1].Hidden);
			Assert.Equal(new[] { BasemapGroups.Water }, r.Pushes[^1].Off);
		}

		[Fact]
		public async Task Ready_WithNothingChanged_PushesNothing()
		{
			var r = new Rig();
			await r.Vm.OnMapsReadyAsync();
			Assert.Empty(r.Pushes);
		}

		[Fact]
		public void Counties_GreyUnlessTheStyleHasCountyLines()
		{
			var r = new Rig();
			r.Vm.SetStyle(new MapStyle("dark", "Dark", "style-dark.json", "https://mapassets/style-dark.json"));
			Assert.False(r.Group(BasemapGroups.Counties).IsAvailable);
			Assert.NotNull(r.Group(BasemapGroups.Counties).ToolTip);
			Assert.True(r.Group(BasemapGroups.Highways).IsAvailable);

			r.Vm.SetStyle(new StyleProvider().GetStyles().Single(s => s.Id == "dataVizBlack"));
			Assert.True(r.Group(BasemapGroups.Counties).IsAvailable);
			Assert.Null(r.Group(BasemapGroups.Counties).ToolTip);
		}
	}
}
