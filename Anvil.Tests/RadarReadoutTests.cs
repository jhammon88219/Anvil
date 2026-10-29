using System.Collections.Generic;
using Anvil.Models;
using Anvil.Services;
using Anvil.ViewModels;
using Xunit;
using static Anvil.Tests.TemporalPanePersistenceTests;

namespace Anvil.Tests
{
	/// <summary>
	/// <see cref="RadarViewModel.RaiseRadarReadout"/> raises only what CHANGED (perf audit issue 2): it runs every second
	/// and on every frame step, and re-raising all fifteen properties each time re-ran every binding and the Atlas's
	/// read-out for values that hadn't moved. It must still never leave a binding stale, and its per-second beat
	/// (<see cref="RadarViewModel.ReadoutTicked"/>) must fire on every pass, changed or not.
	/// </summary>
	public class RadarReadoutTests
	{
		private static RadarViewModel NewRadarOnSite()
		{
			var settings = new AppSettings();
			var svc = Null<ISettingsService>.Create(new() { ["get_Settings"] = _ => settings });
			var radar = new RadarViewModel(Null<IMapService>.Create(), Null<IRadarSiteProvider>.Create(),
				Null<ILevel2RadarService>.Create(), Null<IDowEventProvider>.Create(), svc, null);
			radar.IsPastEventMode = true; // a site with no loop load behind it
			radar.SelectedRadarOption = new RadarOption("KTLX", new RadarSite("KTLX", "Norman", 35.333, -97.278));
			return radar;
		}

		private static List<string> Record(RadarViewModel radar)
		{
			var raised = new List<string>();
			radar.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? string.Empty);
			return raised;
		}

		[Fact]
		public void SecondPass_WithNothingChanged_RaisesNoneOfTheSiteProperties()
		{
			var radar = NewRadarOnSite();
			radar.RaiseRadarReadout();
			var raised = Record(radar);

			radar.RaiseRadarReadout();

			Assert.DoesNotContain(nameof(RadarViewModel.RadarSiteId), raised);
			Assert.DoesNotContain(nameof(RadarViewModel.RadarCardTitle), raised);
			Assert.DoesNotContain(nameof(RadarViewModel.RadarCardCoords), raised);
			Assert.DoesNotContain(nameof(RadarViewModel.NewestLoadedFrameTime), raised);
		}

		[Fact]
		public void RaiseFromElsewhere_ForgetsTheSentValue_SoTheNextPassRaisesItAgain()
		{
			var radar = NewRadarOnSite();
			radar.RaiseRadarReadout();
			radar.RaisePropertyChangedFor(nameof(RadarViewModel.RadarSiteId)); // another path raised it
			var raised = Record(radar);

			radar.RaiseRadarReadout();

			Assert.Contains(nameof(RadarViewModel.RadarSiteId), raised);
			Assert.DoesNotContain(nameof(RadarViewModel.RadarCardTitle), raised); // the others stay quiet
		}

		[Fact]
		public void ReadoutTicked_FiresOnEveryPass_ChangedOrNot()
		{
			var radar = NewRadarOnSite();
			var ticks = 0;
			radar.ReadoutTicked += (_, _) => ticks++;

			radar.RaiseRadarReadout();
			radar.RaiseRadarReadout();

			Assert.Equal(2, ticks);
		}
	}
}
