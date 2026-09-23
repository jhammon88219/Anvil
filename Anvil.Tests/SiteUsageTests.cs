using System;
using System.IO;
using Anvil.Services;
using Anvil.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Anvil.Tests
{
	/// <summary>
	/// The Radar Atlas's "Your use" record: <see cref="SiteUsageStore"/> persistence and
	/// <see cref="SiteUsageTracker"/>'s site-hours clock (start on load, stop on selection change, pause while
	/// minimized, checkpoint, clear). The tracker runs on a hand-cranked clock through its test ctor, so no
	/// radar VM is needed. Each test gets a throwaway folder.
	/// </summary>
	public class SiteUsageTests
	{
		private static readonly DateTimeOffset T0 = new(2026, 3, 4, 12, 0, 0, TimeSpan.Zero);

		private static string TempDir() =>
			Path.Combine(Path.GetTempPath(), "AnvilSiteUsageTests", Guid.NewGuid().ToString("N"));

		private static SiteUsageStore NewStore(string dir) => new(NullLogger<SiteUsageStore>.Instance, dir);

		private sealed class Clock
		{
			public DateTimeOffset Now = T0;
			public void Advance(TimeSpan by) => Now += by;
		}

		private static (SiteUsageTracker Tracker, SiteUsageStore Store, Clock Clock) New(string? dir = null)
		{
			var store = NewStore(dir ?? TempDir());
			var clock = new Clock();
			return (new SiteUsageTracker(store, () => clock.Now), store, clock);
		}

		[Fact]
		public void Load_CountsLiveAndReplaySeparately()
		{
			var (t, store, _) = New();
			t.OnSiteLoaded("KTLX", replay: false);
			t.OnSiteLoaded("KTLX", replay: true);
			t.OnSiteLoaded("KTLX", replay: false);

			var u = store.Get("KTLX")!;
			Assert.Equal(2, u.LiveLoads);
			Assert.Equal(1, u.ReplayLoads);
			Assert.Equal(T0, u.FirstUsedUtc);
		}

		[Fact]
		public void Clock_RunsFromLoad_AndBanksOnSelectionChange()
		{
			var (t, store, clock) = New();
			t.OnSiteLoaded("KTLX", replay: false);
			clock.Advance(TimeSpan.FromMinutes(30));

			// Running stretch is visible before it's banked…
			Assert.Equal(1800, t.SecondsLoaded("KTLX"), 3);
			Assert.Equal(0, store.Get("KTLX")!.SecondsLoaded);

			// …and banked (then stopped) when the selection moves off the site.
			t.OnSelectionChanged("KFWS");
			clock.Advance(TimeSpan.FromHours(2));
			Assert.Equal(1800, store.Get("KTLX")!.SecondsLoaded, 3);
			Assert.Equal(1800, t.SecondsLoaded("KTLX"), 3);
			Assert.False(t.IsCurrent("KTLX"));
		}

		[Fact]
		public void SelectionChange_ToSameSite_DoesNotStopTheClock()
		{
			var (t, _, clock) = New();
			t.OnSiteLoaded("KTLX", replay: false);
			t.OnSelectionChanged("KTLX");
			clock.Advance(TimeSpan.FromMinutes(10));
			Assert.Equal(600, t.SecondsLoaded("KTLX"), 3);
		}

		[Fact]
		public void Clock_PausesWhileMinimized()
		{
			var (t, _, clock) = New();
			t.OnSiteLoaded("KTLX", replay: false);
			clock.Advance(TimeSpan.FromMinutes(10));
			t.SetMinimized(true);
			clock.Advance(TimeSpan.FromHours(8)); // overnight, minimized — must not count
			t.SetMinimized(false);
			clock.Advance(TimeSpan.FromMinutes(5));
			t.Shutdown();

			Assert.Equal(15 * 60, t.SecondsLoaded("KTLX"), 3);
		}

		[Fact]
		public void LoadWhileMinimized_CountsTheLoad_ButNotTheTime()
		{
			var (t, store, clock) = New();
			t.SetMinimized(true);
			t.OnSiteLoaded("KTLX", replay: false);
			clock.Advance(TimeSpan.FromHours(1));
			t.Shutdown();

			Assert.Equal(1, store.Get("KTLX")!.TotalLoads);
			Assert.Equal(0, t.SecondsLoaded("KTLX"));
		}

		[Fact]
		public void FrameLanded_CheckpointsOnlyPastTheInterval()
		{
			var (t, store, clock) = New();
			t.OnSiteLoaded("KTLX", replay: false);

			clock.Advance(SiteUsageTracker.CheckpointEvery - TimeSpan.FromSeconds(1));
			t.OnFrameLanded();
			Assert.Equal(0, store.Get("KTLX")!.SecondsLoaded);

			clock.Advance(TimeSpan.FromSeconds(1));
			t.OnFrameLanded();
			Assert.Equal(SiteUsageTracker.CheckpointEvery.TotalSeconds, store.Get("KTLX")!.SecondsLoaded, 3);

			// The clock keeps running after a checkpoint, with no double count.
			clock.Advance(TimeSpan.FromMinutes(1));
			Assert.Equal(SiteUsageTracker.CheckpointEvery.TotalSeconds + 60, t.SecondsLoaded("KTLX"), 3);
		}

		[Fact]
		public void Clear_OfCurrentSite_DoesNotReAddTimeBeforeTheClear()
		{
			var (t, store, clock) = New();
			t.OnSiteLoaded("KTLX", replay: false);
			clock.Advance(TimeSpan.FromHours(1));
			t.Clear("KTLX");
			Assert.Null(store.Get("KTLX"));

			clock.Advance(TimeSpan.FromMinutes(2));
			t.Shutdown();
			Assert.Equal(120, store.Get("KTLX")!.SecondsLoaded, 3);
			Assert.Equal(0, store.Get("KTLX")!.TotalLoads);
		}

		[Fact]
		public void ClearAll_EmptiesEverySite()
		{
			var (t, store, _) = New();
			t.OnSiteLoaded("KTLX", replay: false);
			t.OnSiteLoaded("KFWS", replay: false);
			t.ClearAll();
			Assert.Empty(store.All);
		}

		[Fact]
		public void Rank_OrdersBySiteHours_IncludingTheRunningStretch()
		{
			var (t, _, clock) = New();
			t.OnSiteLoaded("KFWS", replay: false);
			clock.Advance(TimeSpan.FromMinutes(30));
			t.OnSelectionChanged("KTLX");
			t.OnSiteLoaded("KTLX", replay: false);
			clock.Advance(TimeSpan.FromMinutes(10));

			Assert.Equal(1, t.Rank("KFWS"));
			Assert.Equal(2, t.Rank("KTLX"));

			clock.Advance(TimeSpan.FromMinutes(25)); // KTLX's unbanked 35 min now beats KFWS's 30
			Assert.Equal(1, t.Rank("KTLX"));
			Assert.Null(t.Rank("KABR"));
		}

		[Fact]
		public void Store_PersistsAcrossInstances()
		{
			var dir = TempDir();
			var (t, _, clock) = New(dir);
			t.OnSiteLoaded("KTLX", replay: true);
			clock.Advance(TimeSpan.FromMinutes(45));
			t.Shutdown();

			var reloaded = NewStore(dir).Get("ktlx"); // ICAO lookup is case-insensitive
			Assert.NotNull(reloaded);
			Assert.Equal(1, reloaded!.ReplayLoads);
			Assert.Equal(45 * 60, reloaded.SecondsLoaded, 3);
		}

		[Fact]
		public void Store_CorruptFile_StartsEmpty()
		{
			var dir = TempDir();
			Directory.CreateDirectory(dir);
			File.WriteAllText(Path.Combine(dir, "site-usage.json"), "{ not json");
			Assert.Empty(NewStore(dir).All);
		}
	}
}
