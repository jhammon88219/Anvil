using System;
using Anvil.Models;
using Anvil.Services;
using Xunit;

namespace Anvil.Tests
{
	/// <summary>
	/// <see cref="HrrrIndex"/> (a message's byte range from the .idx) and <see cref="HrrrField.Locate"/> (which file +
	/// .idx time a lead lives in). The idx lines are real ones (hrrr.t15z, 2026-10-21).
	/// </summary>
	public class HrrrIndexTests
	{
		private const string SubHourlyIdx =
			"1:0:d=2026102115:REFC:entire atmosphere:75 min fcst:\n" +
			"2:190997:d=2026102115:RETOP:cloud top:75 min fcst:\n" +
			"50:55151110:d=2026102115:REFC:entire atmosphere:90 min fcst:\n" +
			"51:55340000:d=2026102115:RETOP:cloud top:90 min fcst:\n" +
			"148:166610461:d=2026102115:REFC:entire atmosphere:120 min fcst:\n";

		[Fact]
		public void A_message_runs_to_the_next_offset()
		{
			Assert.Equal(new HrrrIndex.Range(0, 190997), HrrrIndex.Find(SubHourlyIdx, "REFC", "entire atmosphere", "75 min fcst"));
			Assert.Equal(new HrrrIndex.Range(55151110, 188890), HrrrIndex.Find(SubHourlyIdx, "REFC", "entire atmosphere", "90 min fcst"));
		}

		[Fact]
		public void The_last_message_is_an_open_range()
		{
			Assert.Equal(new HrrrIndex.Range(166610461, null), HrrrIndex.Find(SubHourlyIdx, "REFC", "entire atmosphere", "120 min fcst"));
		}

		[Fact]
		public void A_missing_entry_is_null()
		{
			Assert.Null(HrrrIndex.Find(SubHourlyIdx, "REFC", "entire atmosphere", "105 min fcst"));
		}

		[Theory]
		[InlineData(0, "wrfsfc", 0, "anl")]
		[InlineData(15, "wrfsubh", 1, "15 min fcst")]
		[InlineData(60, "wrfsubh", 1, "60 min fcst")]
		[InlineData(75, "wrfsubh", 2, "75 min fcst")]
		[InlineData(1080, "wrfsubh", 18, "1080 min fcst")]
		[InlineData(1140, "wrfsfc", 19, "19 hour fcst")]   // the 15-min file ends at f18
		[InlineData(2880, "wrfsfc", 48, "48 hour fcst")]
		public void Instant_fields_come_from_the_15_minute_file(int lead, string kind, int hour, string time)
		{
			Assert.Equal((kind, hour, time), HrrrFields.CompositeReflectivity.Locate(lead));
		}

		[Theory]
		[InlineData(60, 1, "0-1 hour max fcst")]
		[InlineData(720, 12, "11-12 hour max fcst")]
		public void Hourly_maxes_come_from_the_hourly_file(int lead, int hour, string time)
		{
			Assert.Equal(("wrfsfc", hour, time), HrrrFields.UpdraftHelicity25.Locate(lead));
		}

		[Fact]
		public void Hourly_maxes_have_no_analysis_or_quarter_hours()
		{
			Assert.Throws<ArgumentOutOfRangeException>(() => HrrrFields.UpdraftHelicity25.Locate(0));
			Assert.Throws<ArgumentOutOfRangeException>(() => HrrrFields.UpdraftHelicity25.Locate(45));
		}

		[Fact]
		public void Past_f18_an_instant_field_is_hourly_only()
		{
			Assert.Throws<ArgumentOutOfRangeException>(() => HrrrFields.CompositeReflectivity.Locate(1095));
		}

		[Theory]
		[InlineData(0, 48)]
		[InlineData(12, 48)]
		[InlineData(15, 18)]
		public void Synoptic_runs_go_to_48_hours(int hour, int last)
		{
			Assert.Equal(last, HrrrFields.LastHourOf(new DateTimeOffset(2026, 10, 21, hour, 0, 0, TimeSpan.Zero)));
		}
	}
}
