using System;
using System.Collections.Generic;
using System.Diagnostics;
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
	/// The Dev load-time seeding pieces that can run without the app: which cached files a cold pass forgets
	/// (<see cref="Level2RadarService.IsCachedInRange"/>), the simulated speed cap (<see cref="DevBandwidthLimit"/>), and
	/// the run's plan (<see cref="LoadTimeSeedingViewModel"/>: built-ins only, by type, × windows × passes).
	/// </summary>
	public class LoadTimeSeedingTests
	{
		private static readonly DateTimeOffset From = new(1999, 5, 3, 23, 0, 0, TimeSpan.Zero);
		private static readonly DateTimeOffset To = From.AddHours(2);

		[Theory]
		[InlineData("KTLX_19990503_233012.V06", true)]       // base tilt
		[InlineData("KTLX_19990503_233012_e024.V06", true)]  // a higher tilt
		[InlineData("KTLX_19990503_233012.raw", true)]       // the whole volume
		[InlineData("KTLX_19990503_233012_vwp.V06", true)]   // the storm-motion volume
		[InlineData("KTLX_19990503_225959.V06", false)]      // a second before the window
		[InlineData("KTLX_19990504_010001.V06", false)]      // a second after
		[InlineData("KINX_19990503_233012.V06", false)]      // another site
		[InlineData("KTLX_garbage.V06", false)]
		public void A_cold_pass_forgets_only_that_sites_window(string file, bool expected) =>
			Assert.Equal(expected, Level2RadarService.IsCachedInRange(file, "KTLX", From, To));

		[Fact]
		public async Task The_speed_cap_holds_downloads_to_its_rate_and_off_costs_nothing()
		{
			try
			{
				DevBandwidthLimit.Mbps = 0;
				var off = Stopwatch.StartNew();
				await DevBandwidthLimit.ChargeAsync(50_000_000, CancellationToken.None);
				Assert.True(off.ElapsedMilliseconds < 50);

				DevBandwidthLimit.Mbps = 1; // 125,000 bytes/s
				var on = Stopwatch.StartNew();
				// Two downloads at ONCE share the link like a real one: 100 ms of budget each, so the pair takes ~200 ms.
				await Task.WhenAll(
					DevBandwidthLimit.ChargeAsync(12_500, CancellationToken.None),
					DevBandwidthLimit.ChargeAsync(12_500, CancellationToken.None));
				Assert.InRange(on.ElapsedMilliseconds, 170, 2_000);
			}
			finally
			{
				DevBandwidthLimit.Mbps = 0;
			}
		}

		private static SavedEvent Event(string id, SavedEventKind kind, bool builtIn = true) => new(id, id,
			new[] { new SavedEventLeg("KTLX", From, 120) }, 0, string.Empty, string.Empty, builtIn, kind);

		private static LoadTimeSeedingViewModel NewRun(params SavedEvent[] events)
		{
			var settings = new AppSettings();
			var svc = Null<ISettingsService>.Create(new() { ["get_Settings"] = _ => settings });
			var radar = new RadarViewModel(Null<IMapService>.Create(), Null<IRadarSiteProvider>.Create(),
				Null<ILevel2RadarService>.Create(), Null<IDowEventProvider>.Create(), svc, null);
			var library = Null<ISavedEventLibrary>.Create(new() { ["GetEvents"] = _ => (IReadOnlyList<SavedEvent>)events });
			return new LoadTimeSeedingViewModel(radar, library, Null<ILevel2RadarService>.Create(), Null<IMapService>.Create(), null);
		}

		[Fact]
		public void The_plan_is_built_in_events_by_type_times_windows_times_passes()
		{
			var run = NewRun(
				Event("moore", SavedEventKind.Tornado), Event("joplin", SavedEventKind.Tornado),
				Event("katrina", SavedEventKind.Hurricane), Event("mine", SavedEventKind.Tornado, builtIn: false));
			Assert.Equal("3 events × 3 windows × 3 passes = 27 loads", run.PlanText); // default 1, 2, 3 hr; cold, warm, revisit; user event out

			run.Hurricane = false;
			run.Warm = false;
			run.Windows[0].IsChecked = true; // 30 min too
			Assert.Equal("2 events × 4 windows × 2 passes = 16 loads", run.PlanText);

			run.Revisit = false;
			Assert.Equal("2 events × 4 windows × 1 pass = 8 loads", run.PlanText);
		}

		[Fact]
		public async Task Start_refuses_without_PastCast()
		{
			var run = NewRun(Event("moore", SavedEventKind.Tornado));
			await run.StartAsync();
			Assert.Equal("Turn on PastCast first.", run.StatusText);
			Assert.False(run.IsRunning);
		}
	}
}
