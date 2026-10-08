using System;
using Anvil.ViewModels;
using Xunit;

namespace Anvil.Tests
{
	/// <summary>
	/// The console's age line (<see cref="RadarViewModel.RadarAgeText"/>): seconds while they matter, counted from the
	/// scan's START, for the frame ON SCREEN (the user's calls, 2026-10-08).
	/// </summary>
	public class RadarAgeTextTests
	{
		[Theory]
		[InlineData(0, "0 s")]
		[InlineData(45, "45 s")]
		[InlineData(59, "59 s")]
		[InlineData(60, "1 min")]
		[InlineData(100, "1 min 40 s")]
		[InlineData(120, "2 min")]
		[InlineData(599, "9 min 59 s")]
		[InlineData(600, "10 min")]          // from 10 min: whole minutes, as before
		[InlineData(725, "12 min")]
		[InlineData(3960, "1 hr 6 min")]
		[InlineData(-5, "0 s")]              // a clock a little behind the radar's never reads negative
		public void AgeWords_SecondsUnderTenMinutes_ThenTheLargestUnits(int seconds, string expected)
		{
			Assert.Equal(expected, RadarViewModel.AgeWords(TimeSpan.FromSeconds(seconds)));
		}

		[Fact]
		public void AgeWords_KeepsReplayAgesReadable()
		{
			Assert.StartsWith("2 yr", RadarViewModel.AgeWords(TimeSpan.FromDays(2 * 365.25 + 40)));
		}
	}
}
