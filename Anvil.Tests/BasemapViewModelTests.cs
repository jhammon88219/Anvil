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
	/// changing them; ticks + dimmer persist and restore; nothing reaches the page before map-ready, and the
	/// whole state is replayed there.
	/// </summary>
	public class BasemapViewModelTests
	{
		private sealed class Rig
		{
			public readonly AppSettings Settings;
			public readonly List<(bool Hidden, string[] Off, double Dim)> Pushes = new();
			public readonly BasemapViewModel Vm;

			public Rig(AppSettings? settings = null)
			{
				Settings = settings ?? new AppSettings();
				var map = Null<IMapService>.Create(new()
				{
					["SetBasemapAsync"] = a =>
					{
						Pushes.Add(((bool)a![0]!, ((IReadOnlyList<string>)a[1]!).ToArray(), (double)a[2]!));
						return Task.CompletedTask;
					},
				});
				var svc = Null<ISettingsService>.Create(new() { ["get_Settings"] = _ => Settings });
				Vm = new BasemapViewModel(map, svc);
			}

			public BasemapGroupOption Group(string id) => Vm.Groups.Single(g => g.Id == id);
		}

		[Fact]
		public void Defaults_ShowEverything_NoDim()
		{
			var r = new Rig();
			Assert.True(r.Vm.IsMapShown);
			Assert.All(r.Vm.Groups, g => Assert.True(g.IsShown));
			Assert.Equal(0, r.Vm.Dim);
			Assert.Equal(BasemapGroups.All.Select(g => g.Id), r.Vm.Groups.Select(g => g.Id));
		}

		[Fact]
		public void Untick_PersistsTheUntickedSet_AndRestores()
		{
			var r = new Rig();
			r.Group(BasemapGroups.Roads).IsShown = false;
			r.Group(BasemapGroups.Land).IsShown = false;
			Assert.Equal(new[] { BasemapGroups.Land, BasemapGroups.Roads }, r.Settings.HiddenBasemapGroups);

			var again = new Rig(r.Settings);
			Assert.False(again.Group(BasemapGroups.Roads).IsShown);
			Assert.False(again.Group(BasemapGroups.Land).IsShown);
			Assert.True(again.Group(BasemapGroups.Water).IsShown);
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
			Assert.True(r.Group(BasemapGroups.Roads).IsShown);
		}

		[Fact]
		public void Dim_ClampsAndPersists()
		{
			var r = new Rig();
			r.Vm.DimPercent = 40;
			Assert.Equal(0.4, r.Settings.BasemapDim, 6);
			r.Vm.Dim = 5;
			Assert.Equal(BasemapViewModel.MaxDim, r.Vm.Dim);
			r.Vm.Dim = double.NaN;
			Assert.Equal(0, r.Vm.Dim);
			Assert.Equal(0.3, new Rig(new AppSettings { BasemapDim = 0.3 }).Vm.Dim, 6);
		}

		[Fact]
		public async Task NothingPushedBeforeReady_ThenTheWholeStateIsReplayed()
		{
			var r = new Rig();
			r.Vm.IsMapShown = false;
			r.Group(BasemapGroups.Water).IsShown = false;
			r.Vm.DimPercent = 20;
			Assert.Empty(r.Pushes);

			await r.Vm.OnMapsReadyAsync();
			var p = Assert.Single(r.Pushes);
			Assert.True(p.Hidden);
			Assert.Equal(new[] { BasemapGroups.Water }, p.Off);
			Assert.Equal(0.2, p.Dim, 6);

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
			Assert.True(r.Group(BasemapGroups.Roads).IsAvailable);

			r.Vm.SetStyle(new StyleProvider().GetStyles().Single(s => s.Id == "dataVizBlack"));
			Assert.True(r.Group(BasemapGroups.Counties).IsAvailable);
			Assert.Null(r.Group(BasemapGroups.Counties).ToolTip);
		}
	}
}
