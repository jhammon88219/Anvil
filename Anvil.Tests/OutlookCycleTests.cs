using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Anvil.Models;
using Anvil.Services;
using Anvil.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Anvil.Tests
{
	/// <summary>
	/// <see cref="SpcIssuanceCycles"/> (the one cycle table both outlook sections read, measured against IEM on
	/// 2026-09-30), PastCast's Auto order over it, and ForeCast's Cycle row: Latest follows the live feed, an
	/// earlier issuance is fetched from IEM and HELD.
	/// </summary>
	public class OutlookCycleTests
	{
		private static DateTimeOffset Z(int y, int mo, int d, int h, int mi = 0) => new(y, mo, d, h, mi, 0, TimeSpan.Zero);

		[Fact]
		public void Table_MatchesIemsCycles()
		{
			Assert.Equal(new[] { 6, 13, 16, 20, 1 }, SpcIssuanceCycles.For(1));
			Assert.Equal(new[] { 7, 17 }, SpcIssuanceCycles.For(2));   // 07, NOT 06 — IEM has no Day 2 06
			Assert.Equal(new[] { 8, 20 }, SpcIssuanceCycles.For(3));
			Assert.Empty(SpcIssuanceCycles.For(4));
			Assert.Equal("1630Z", SpcIssuanceCycles.Label(16));
			Assert.Equal("06Z", SpcIssuanceCycles.Label(6));
		}

		[Fact]
		public void IssuedAt_IsRelativeToTheValidDay()
		{
			var v = new DateOnly(2026, 10, 1);
			Assert.Equal(Z(2026, 10, 1, 6), SpcIssuanceCycles.IssuedAtUtc(1, 6, v));       // Day 1 06Z: ON the valid day
			Assert.Equal(Z(2026, 10, 2, 1), SpcIssuanceCycles.IssuedAtUtc(1, 1, v));       // Day 1 01Z: the night after
			Assert.Equal(Z(2026, 9, 30, 17), SpcIssuanceCycles.IssuedAtUtc(2, 17, v));     // Day 2: the day BEFORE
			Assert.Equal(Z(2026, 9, 29, 8), SpcIssuanceCycles.IssuedAtUtc(3, 8, v));       // Day 3: two days before
		}

		[Fact]
		public void PastAuto_PutsTheIssuanceInEffectFirst()
		{
			// 17:00Z → the 1630Z is in effect; the rest follow latest-first.
			Assert.Equal(new[] { 16, 1, 20, 13, 6 }, PastOutlookViewModel.OrderedAutoCycles(1, Z(2026, 9, 20, 17)));
			// 12:30Z, before the 13Z: the 06Z (issued that morning) — the old table put it a DAY late.
			Assert.Equal(6, PastOutlookViewModel.OrderedAutoCycles(1, Z(2026, 9, 20, 12, 30))[0]);
			// Day 2: every issuance precedes the valid day, so the last one leads.
			Assert.Equal(new[] { 17, 7 }, PastOutlookViewModel.OrderedAutoCycles(2, Z(2026, 9, 20, 18)));
		}

		// ── ForeCast ──

		private sealed class Rig
		{
			public readonly List<string> Calls = new();
			public readonly List<(DateOnly Date, int Day, int Cycle)> Fetches = new();
			public readonly OutlookViewModel Vm;

			public Rig(DateTimeOffset liveExpire)
			{
				var product = new SpcOutlookProduct("d1c", 1, SpcOutlookType.Categorical, "Categorical", "d1c.geojson", "https://x/d1c.geojson");
				var map = TemporalWindowPersistenceTests.Null<IMapService>.Create(new()
				{
					["ShowOutlookAsync"] = a => { Calls.Add("show " + ((SpcOutlookProduct)a![0]!).CacheFileName); return Task.CompletedTask; },
					["ClearOutlookAsync"] = _ => { Calls.Add("clear"); return Task.CompletedTask; },
				});
				var spc = TemporalWindowPersistenceTests.Null<ISpcOutlookService>.Create(new()
				{
					["get_AvailableDays"] = _ => new[] { 1, 4 },
					["GetProductsForDay"] = a => (int)a![0]! == 1 ? new[] { product }
						: new[] { new SpcOutlookProduct("d4", 4, SpcOutlookType.ExtendedProbabilistic, "Probabilistic", "d4.geojson", "https://x/d4.geojson") },
					["GetTimesForProduct"] = a => ((SpcOutlookProduct)a![0]!).Id == "d1c"
						? new SpcOutlookTimes(liveExpire.AddHours(-23), liveExpire.AddHours(-23), liveExpire) : null,
					["EnsurePastOutlookAsync"] = a =>
					{
						Fetches.Add(((DateOnly)a![0]!, (int)a[1]!, (int)a[2]!));
						return Task.FromResult(new PastOutlookResult(true, (int)a[2]!, new[] { SpcOutlookType.Categorical }, null, null));
					},
				});
				Vm = new OutlookViewModel(map, spc, TemporalWindowPersistenceTests.Null<IDispatcher>.Create(), NullLogger<OutlookViewModel>.Instance);
			}
		}

		[Fact]
		public async Task ForeCast_LatestByDefault_AnEarlierIssuanceIsFetchedAndHeld_LatestGoesBack()
		{
			// Today's Day 1: valid yesterday-12Z-ish → expires 12Z tomorrow, so its 06Z/13Z are already out.
			var validDay = SpcIssuanceCycles.ConvectiveDay(DateTimeOffset.UtcNow.AddHours(-12));
			var rig = new Rig(new DateTimeOffset(validDay.AddDays(1).ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero));
			var vm = rig.Vm;
			await vm.OnMapsReadyAsync();
			vm.IsOutlookVisible = true;

			Assert.Null(vm.SelectedCycleOption.Cycle);                  // Latest
			Assert.Equal("show d1c.geojson", rig.Calls[^1]);
			Assert.True(vm.CanPickCycle);
			Assert.Contains(vm.CycleOptions, o => o.Cycle == 6);

			vm.SelectedCycleOption = vm.CycleOptions.First(o => o.Cycle == 6);
			await Task.Yield();
			Assert.Equal((validDay, 1, 6), rig.Fetches[^1]);           // IEM's valid = the day the outlook is FOR
			Assert.Equal("show " + SpcOutlookService.PastCacheName(validDay, 1, 6, SpcOutlookType.Categorical), rig.Calls[^1]);
			Assert.StartsWith("Holding the 06Z issuance", vm.CardFooter);

			var shows = rig.Calls.Count;
			vm.OnOutlooksRefreshed();                                   // a refresh must not move a held issuance
			Assert.Equal(shows, rig.Calls.Count);

			vm.SelectedCycleOption = vm.CycleOptions[0];                // Latest
			Assert.Equal("show d1c.geojson", rig.Calls[^1]);
		}

		[Fact]
		public void ForeCast_DaysWithoutCycles_OfferLatestOnly()
		{
			var rig = new Rig(DateTimeOffset.UtcNow.AddHours(12));
			rig.Vm.SelectedDayOption = rig.Vm.Days.First(d => d.Day == 4);
			Assert.Single(rig.Vm.CycleOptions);
			Assert.False(rig.Vm.CanPickCycle);
		}
	}
}
