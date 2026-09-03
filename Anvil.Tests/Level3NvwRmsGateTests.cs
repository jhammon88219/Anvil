using System;
using System.Collections.Generic;
using System.Linq;
using Anvil.Models;
using Anvil.Services;
using Xunit;

namespace Anvil.Tests
{
	/// <summary>
	/// The RMS quality gate on NVW levels (doc 01 §2: "drop levels whose RMS exceeds your threshold before
	/// fitting"). The product reports a per-level fit residual in knots and it was previously parsed and
	/// ignored.
	/// </summary>
	public class Level3NvwRmsGateTests
	{
		private static WindProfile Profile(params double[] rmsKt)
		{
			var levels = rmsKt.Select((r, i) => new WindProfileLevel(250 * (i + 1), 10, 5, 20, 270, r)).ToList();
			return new WindProfile(levels, DateTime.UnixEpoch, "NVW", "TEST", 0);
		}

		[Fact]
		public void KeepsLevelsAtTypicalRealWorldRms()
		{
			// ⚠️ Measured on a real product: NVW levels routinely sit at 4.5–8.1 kt. If a change to the
			// threshold starts discarding these, it is the threshold that is wrong, not the data.
			var p = Level3NvwProvider.ApplyRmsGate(Profile(4.5, 5.2, 6.4, 7.2, 8.1));
			Assert.NotNull(p);
			Assert.Equal(5, p!.Levels.Count);
		}

		[Fact]
		public void DropsOnlyThePoorlyFittedLevels()
		{
			var p = Level3NvwProvider.ApplyRmsGate(Profile(3.0, 12.0, 5.0, 30.0));
			Assert.NotNull(p);
			Assert.Equal(2, p!.Levels.Count);
			Assert.All(p.Levels, l => Assert.True(l.RmsKt < 10));
		}

		[Fact]
		public void AnAllBadProfileEmptiesRatherThanPassingGarbageOn()
		{
			// The provider turns an emptied profile into "no data" so the chain falls through to our own VAD,
			// rather than handing Bunkers a profile it will reject with a less specific reason.
			var p = Level3NvwProvider.ApplyRmsGate(Profile(20, 25, 30));
			Assert.NotNull(p);
			Assert.Empty(p!.Levels);
		}

		[Fact]
		public void PreservesEverythingElseAboutTheProfile()
		{
			var original = Profile(3.0, 40.0);
			var gated = Level3NvwProvider.ApplyRmsGate(original);
			Assert.Equal(original.Source, gated!.Source);
			Assert.Equal(original.SiteId, gated.SiteId);
			Assert.Equal(original.ValidTimeUtc, gated.ValidTimeUtc);
			Assert.Equal(original.RadarHeightFtMsl, gated.RadarHeightFtMsl);
		}

		[Fact]
		public void NullIn_NullOut() => Assert.Null(Level3NvwProvider.ApplyRmsGate(null));

		[Fact]
		public void TheRealFixtureSurvivesTheGate()
		{
			// End to end against the checked-in product: gating must not destroy a genuinely good profile.
			var path = System.IO.Path.Combine(
				AppContext.BaseDirectory, "Fixtures", "TLX_NVW_2020_03_31_00_02_54.bin");
			var parsed = Level3NvwParser.Parse(System.IO.File.ReadAllBytes(path), "TLX");
			var gated = Level3NvwProvider.ApplyRmsGate(parsed);
			Assert.NotNull(gated);
			Assert.True(gated!.Levels.Count >= parsed!.Levels.Count - 3,
				$"gate removed {parsed.Levels.Count - gated.Levels.Count} of {parsed.Levels.Count} levels");
			Assert.NotEmpty(gated.Levels);
		}
	}
}
