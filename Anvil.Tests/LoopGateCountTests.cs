using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Anvil.Models;
using Anvil.Services;
using Anvil.ViewModels;
using Xunit;
using static Anvil.Tests.TemporalWindowPersistenceTests;

namespace Anvil.Tests
{
	/// <summary>
	/// The loop holding gate's "Frames built" count during a real replay load (fake archive, no map): a cell counts
	/// only once its frame is FILLED. The first-paint frame's cell lights on reflectivity alone (Rule 1), so counting
	/// lit cells read "10 of 10" for 8 s on a 2024 replay while the gate still waited on that frame's velocity.
	/// </summary>
	public class LoopGateCountTests
	{
		private static readonly RadarSite Ktlx = new("KTLX", "Norman", 35.333, -97.278);

		private static async Task<RadarViewModel> LoadedThreeFrames()
		{
			var settings = new AppSettings { HoldPastCastLoads = true };
			var svc = Null<ISettingsService>.Create(new() { ["get_Settings"] = _ => settings });
			var keys = new[] { "2024/05/07/KTLX/KTLX20240507_033006_V06", "2024/05/07/KTLX/KTLX20240507_033512_V06",
				"2024/05/07/KTLX/KTLX20240507_034018_V06" };
			var archive = Null<ILevel2RadarService>.Create(new()
			{
				["GetKeysForWindowAsync"] = _ => Task.FromResult<IReadOnlyList<string>>(keys),
				["EnsureCachedAsync"] = a => Task.FromResult<RadarVolume?>(new RadarVolume("https://radarlevel2/x.V06", Ktlx,
					Level2RadarService.ParseVolumeTime((string)a![1]!)!.Value)),
			});
			var radar = new RadarViewModel(Null<IMapService>.Create(), Null<IRadarSiteProvider>.Create(), archive,
				Null<IDowEventProvider>.Create(), svc, null);
			radar.IsPastEventMode = true;
			radar.SelectedRadarOption = new RadarOption("KTLX", Ktlx);
			radar.PastEventDate = new DateTimeOffset(2024, 5, 6, 0, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2024, 5, 6)));
			radar.PastEventTime = new TimeSpan(22, 30, 0);
			radar.PastEventDurationIndex = 0; // 30 min
			Assert.True(await radar.LoadSelectedPastEventAsync());
			return radar;
		}

		[Fact]
		public async Task The_first_paint_frame_counts_as_built_only_once_its_velocity_lands()
		{
			var radar = await LoadedThreeFrames();
			Assert.True(radar.LoopGate.IsShown);
			Assert.Equal(3, radar.LoopGate.Total);

			for (var i = 0; i < 3; i++) radar.OnRadarFrameReady(i, hasData: true);
			// The page: frames 1 and 2 have the duo; frame 0 (first paint) has reflectivity only.
			radar.SetBuildProgress(2, 3, new[] { true, true, true }, new[] { false, true, true });
			Assert.True(radar.Segments[0].IsReady);       // its CELL is lit (Rule 1) …
			Assert.Equal(2, radar.LoopGate.Built);        // … but it doesn't count as built
			Assert.True(radar.LoopGate.IsShown);          // and the gate still holds

			radar.SetBuildProgress(3, 3, new[] { true, true, true }, new[] { true, true, true });
			Assert.Equal(3, radar.LoopGate.Built);
			Assert.False(radar.LoopGate.IsLoading);       // released
		}
	}
}
