using System;
using System.Threading.Tasks;
using Anvil.Models;
using Anvil.Services;
using Anvil.ViewModels;
using Xunit;
using static Anvil.Tests.TemporalWindowPersistenceTests;

namespace Anvil.Tests
{
	/// <summary>
	/// PastCast's Clear (<see cref="RadarViewModel.ClearReplay"/>) and the header readouts it drives: a loaded
	/// window is forgotten, the date + start pickers land on NOW (start rounded down to 5 min), and the header
	/// falls back to its placeholders. The window is ARMED here (no site), the engine's other success path.
	/// </summary>
	public class PastCastClearTests
	{
		private static RadarViewModel NewRadar()
		{
			var settings = new AppSettings();
			var svc = Null<ISettingsService>.Create(new() { ["get_Settings"] = _ => settings });
			return new RadarViewModel(Null<IMapService>.Create(), Null<IRadarSiteProvider>.Create(),
				Null<ILevel2RadarService>.Create(), Null<IDowEventProvider>.Create(), svc, null);
		}

		private static async Task<RadarViewModel> ArmedAt(DateTimeOffset localDate, TimeSpan start)
		{
			var radar = NewRadar();
			radar.IsPastEventMode = true;
			radar.PastEventDate = localDate;
			radar.PastEventTime = start;
			radar.PastEventDurationIndex = 2; // 2 hours
			Assert.True(await radar.LoadSelectedPastEventAsync()); // no site → arms the window
			return radar;
		}

		[Fact]
		public async Task LoadedHeader_NamesTheLoadedWindow()
		{
			var radar = await ArmedAt(new DateTimeOffset(2011, 5, 24, 0, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2011, 5, 24))),
				new TimeSpan(17, 0, 0));
			Assert.True(radar.HasLoadedReplayWindow);
			Assert.Equal(new DateTime(2011, 5, 24).ToString("dddd MMM d, yyyy"), radar.LoadedReplayDateText);
			Assert.Equal("5:00–7:00", radar.LoadedReplayRangeDigits);
			Assert.Equal(new DateTime(2011, 5, 24, 19, 0, 0).ToString("tt"), radar.LoadedReplayRangeSuffix);
		}

		[Fact]
		public async Task Clear_ForgetsTheWindow_AndPutsThePickersOnNow()
		{
			var radar = await ArmedAt(new DateTimeOffset(2011, 5, 24, 0, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2011, 5, 24))),
				new TimeSpan(17, 0, 0));
			var before = DateTime.Now;
			radar.ClearReplay();
			var after = DateTime.Now;

			Assert.False(radar.HasLoadedReplayWindow);
			Assert.Equal("No replay date", radar.LoadedReplayDateText);
			Assert.Equal("No Timeframe Loaded", radar.LoadedReplayRangeDigits);
			Assert.Equal(string.Empty, radar.LoadedReplayRangeSuffix);

			// Today (either side of a midnight tick), start on a 5-minute mark within the last 5 minutes.
			var date = radar.PastEventDate!.Value.Date;
			Assert.True(date == before.Date || date == after.Date);
			Assert.Equal(0, radar.PastEventTime.Minutes % 5);
			var start = date + radar.PastEventTime;
			Assert.InRange((before - start).TotalMinutes, -1, 5);
		}

		[Fact]
		public async Task Armed_WithNoSite_SaysSelectASite_AndClearEmptiesTheReadouts()
		{
			var radar = await ArmedAt(new DateTimeOffset(2011, 5, 24, 0, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2011, 5, 24))),
				new TimeSpan(17, 0, 0));
			Assert.Equal("Select a site", radar.ReplayCaption);
			Assert.Equal(string.Empty, radar.ReplaySite);
			Assert.Equal(string.Empty, radar.ReplayCount);
			Assert.False(radar.HasReplayError);

			radar.ClearReplay();
			Assert.Equal(string.Empty, radar.ReplayCaption);
			Assert.Equal(string.Empty, radar.ReplayCount);
		}

		// The user's call, 2026-10-02: Clear leaves NO radar site selected (it used to keep the current one).
		[Fact]
		public void Clear_UnselectsTheSite()
		{
			var radar = NewRadar();
			radar.IsPastEventMode = true;
			radar.SelectedRadarOption = new RadarOption("KTLX", new RadarSite("KTLX", "Norman", 35.333, -97.278));
			Assert.NotNull(radar.SelectedRadarOption);

			radar.ClearReplay();
			Assert.Null(radar.SelectedRadarOption);
		}

		[Fact]
		public void Clear_OutsidePastCast_DoesNothing()
		{
			var radar = NewRadar();
			var date = radar.PastEventDate;
			radar.ClearReplay();
			Assert.Equal(date, radar.PastEventDate);
		}
	}
}
