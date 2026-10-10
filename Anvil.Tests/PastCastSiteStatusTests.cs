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
	/// PastCast site status (the user's call, 2026-10-04): NO site check runs in PastCast — the live feed says nothing
	/// about a past window. Every site is "Not checked" until you LOAD it for the window; the load's own listing of
	/// its scans marks it; a different window greys them all again. (RadarViewModel "PASTCAST availability".)
	/// </summary>
	public class PastCastSiteStatusTests
	{
		private static readonly RadarSite Ktlx = new("KTLX", "Norman", 35.333, -97.278);
		private static readonly RadarSite Kinx = new("KINX", "Tulsa", 36.175, -95.564);
		private static readonly string[] KtlxKeys = { "2024/05/07/KTLX/KTLX20240507_033006_V06" };

		// Two sites: KTLX has scans in any window, KINX has none. live = the live pass's answer (pending until set).
		private static RadarViewModel NewRadar(TaskCompletionSource<IReadOnlyCollection<string>>? live = null)
		{
			var settings = new AppSettings();
			var svc = Null<ISettingsService>.Create(new() { ["get_Settings"] = _ => settings });
			var archive = Null<ILevel2RadarService>.Create(new()
			{
				["GetKeysForWindowAsync"] = a => Task.FromResult<IReadOnlyList<string>>(
					((RadarSite)a![0]!).Id == "KTLX" ? KtlxKeys : Array.Empty<string>()),
				["EnsureCachedAsync"] = a => Task.FromResult<RadarVolume?>(new RadarVolume("https://radarlevel2/x.V06", Ktlx,
					Level2RadarService.ParseVolumeTime((string)a![1]!)!.Value)),
				["GetLiveSiteIdsAsync"] = _ => (live ?? new TaskCompletionSource<IReadOnlyCollection<string>>()).Task,
			});
			var sites = Null<IRadarSiteProvider>.Create(new() { ["GetSites"] = _ => (IReadOnlyList<RadarSite>)new[] { Ktlx, Kinx } });
			return new RadarViewModel(Null<IMapService>.Create(), sites, archive, Null<IDowEventProvider>.Create(), svc, null);
		}

		private static RadarSiteRow Row(RadarViewModel radar, string id) => radar.RadarSiteRows.Single(r => r.Id == id);

		private static async Task<bool> LoadAt(RadarViewModel radar, RadarSite site, TimeSpan start)
		{
			radar.PastEventDate = new DateTimeOffset(2024, 5, 6, 0, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2024, 5, 6)));
			radar.PastEventTime = start;
			radar.PastEventDurationIndex = 0;
			return await radar.LoadReplayAtSiteAsync(radar.RadarOptions.First(o => o.Site == site));
		}

		[Fact]
		public void Entering_PastCast_marks_every_site_not_checked()
		{
			var radar = NewRadar();
			radar.IsPastEventMode = true;
			Assert.All(radar.RadarSiteRows, r =>
			{
				Assert.Equal(SiteAvailability.Unknown, r.Availability);
				Assert.Equal("Not checked", r.StatusLabel);
			});
		}

		[Fact]
		public async Task A_load_checks_only_its_own_site_from_its_own_listing()
		{
			var radar = NewRadar();
			radar.IsPastEventMode = true;

			await LoadAt(radar, Ktlx, new TimeSpan(22, 30, 0));
			Assert.Equal(SiteAvailability.Online, Row(radar, "KTLX").Availability);
			Assert.Equal("Data for this timeframe", Row(radar, "KTLX").StatusLabel);
			Assert.Equal(SiteAvailability.Unknown, Row(radar, "KINX").Availability); // never loaded → never checked

			Assert.False(await LoadAt(radar, Kinx, new TimeSpan(22, 30, 0))); // clicking another site, same window
			Assert.Equal(SiteAvailability.Offline, Row(radar, "KINX").Availability);
			Assert.Equal(SiteAvailability.Online, Row(radar, "KTLX").Availability); // same window: its dot stays
		}

		[Fact]
		public async Task A_different_window_greys_every_dot_first()
		{
			var radar = NewRadar();
			radar.IsPastEventMode = true;
			await LoadAt(radar, Ktlx, new TimeSpan(22, 30, 0));
			await LoadAt(radar, Kinx, new TimeSpan(22, 30, 0));

			await LoadAt(radar, Ktlx, new TimeSpan(20, 0, 0)); // a new window
			Assert.Equal(SiteAvailability.Online, Row(radar, "KTLX").Availability);
			Assert.Equal(SiteAvailability.Unknown, Row(radar, "KINX").Availability); // that answer was for the old window
		}

		// A launch that restores PastCast: map-ready, THEN the session's modes, THEN the site checks (MapViewModel order).
		// Started from map-ready, the live pass ran — and showed "Checking radar sites 1 / …" in the bar — for the moment
		// before PastCast came back (2026-10-04).
		[Fact]
		public async Task A_launch_that_restores_PastCast_never_starts_a_site_check()
		{
			var radar = NewRadar();
			await radar.OnMapsReadyAsync();
			Assert.False(radar.IsSiteCheckRunning); // map-ready alone starts nothing

			radar.IsPastEventMode = true;           // RestoreTemporalSession
			radar.StartSiteChecks();
			Assert.False(radar.IsSiteCheckRunning);
			Assert.All(radar.RadarSiteRows, r => Assert.Equal("Not checked", r.StatusLabel));
		}

		[Fact]
		public async Task A_NowCast_launch_starts_its_announced_site_check()
		{
			var radar = NewRadar();
			await radar.OnMapsReadyAsync();
			radar.StartSiteChecks();
			Assert.True(radar.IsSiteCheckRunning);
			Assert.True(radar.IsSiteCheckAnnounced);
		}

		[Fact]
		public async Task Entering_PastCast_mid_check_ends_the_check_and_drops_its_result()
		{
			var live = new TaskCompletionSource<IReadOnlyCollection<string>>();
			var radar = NewRadar(live);
			radar.IsPastEventMode = true;
			radar.IsPastEventMode = false; // leaving PastCast starts an announced live check
			Assert.True(radar.IsSiteCheckRunning);
			var finished = new List<bool>();
			radar.SiteCheckFinished += (_, completed) => finished.Add(completed);

			radar.IsPastEventMode = true;  // e.g. a launch restoring PastCast mid-pass
			Assert.False(radar.IsSiteCheckRunning);
			Assert.Equal(new[] { false }, finished); // cut short, not completed — the bar clears it, nothing failed

			live.SetResult(new[] { "KTLX", "KINX" });
			await Task.Delay(50);
			Assert.All(radar.RadarSiteRows, r => Assert.Equal(SiteAvailability.Unknown, r.Availability));
		}
	}
}
