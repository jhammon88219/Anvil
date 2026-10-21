using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Anvil.Models
{
	/// <summary>
	/// One HRRR field Anvil reads, named the way the model's <c>.idx</c> names it (variable + level), and which
	/// file carries it. The table is <see cref="HrrrFields"/>.
	/// </summary>
	/// <param name="Id">Anvil's token (persisted later; never rename).</param>
	/// <param name="Name">Plain words for the UI.</param>
	/// <param name="IdxVariable">The .idx's 4th field ("REFC").</param>
	/// <param name="IdxLevel">The .idx's 5th field ("entire atmosphere").</param>
	/// <param name="IsHourlyMax">A max over the PAST hour (template 4.8) — hourly steps only, none at f00.</param>
	/// <param name="Encoding">How a value becomes the page's one byte (<see cref="ModelFieldEncoding"/>).</param>
	/// <param name="RampId">Its colours: <c>reflectivity</c> = radar's own table (radar-ramps.js), else a
	/// <see cref="ModelRamps"/> id.</param>
	public sealed record HrrrField(string Id, string Name, string IdxVariable, string IdxLevel, bool IsHourlyMax,
		ModelFieldEncoding Encoding, string RampId)
	{
		/// <summary>
		/// The step this field comes in AT a lead: 15 min (the sub-hourly file) for an instant field through
		/// <see cref="HrrrFields.SubHourlyLastHour"/>, then 60; always 60 for an hourly max.
		/// </summary>
		public int StepMinutesAt(int leadMinutes) =>
			IsHourlyMax || leadMinutes > HrrrFields.SubHourlyLastHour * 60 ? 60 : 15;

		/// <summary>
		/// The file a lead lives in and the .idx time text that picks it. Instant fields: lead 0 is the hourly file's
		/// "anl"; every later 15-min lead to f18 is the SUB-HOURLY file of the hour it ends in ("75 min fcst" is in
		/// f02); past f18 (a 48 h run) the hourly file again, "19 hour fcst". Hourly maxes: the hourly file,
		/// "11-12 hour max fcst".
		/// </summary>
		public (string FileKind, int FileHour, string IdxTime) Locate(int leadMinutes)
		{
			var step = StepMinutesAt(leadMinutes);
			if (leadMinutes < 0 || leadMinutes % step != 0)
				throw new ArgumentOutOfRangeException(nameof(leadMinutes), $"{Id} comes every {step} min at that lead.");
			if (IsHourlyMax)
			{
				if (leadMinutes == 0) throw new ArgumentOutOfRangeException(nameof(leadMinutes), $"{Id} has no analysis (f00).");
				var h = leadMinutes / 60;
				return (HrrrFields.HourlyFile, h, string.Create(CultureInfo.InvariantCulture, $"{h - 1}-{h} hour max fcst"));
			}
			if (leadMinutes == 0) return (HrrrFields.HourlyFile, 0, "anl");
			if (step == 60) return (HrrrFields.HourlyFile, leadMinutes / 60, string.Create(CultureInfo.InvariantCulture, $"{leadMinutes / 60} hour fcst"));
			return (HrrrFields.SubHourlyFile, (leadMinutes + 59) / 60, string.Create(CultureInfo.InvariantCulture, $"{leadMinutes} min fcst"));
		}
	}

	/// <summary>
	/// The HRRR fields Anvil reads. ⭐ Add one = a row here (check its .idx variable + level against a real file:
	/// <c>curl …/hrrr.tHHz.wrfsfcf01.grib2.idx</c>).
	/// </summary>
	public static class HrrrFields
	{
		public const string HourlyFile = "wrfsfc";
		public const string SubHourlyFile = "wrfsubh";

		/// <summary>⚠️ The 15-min file stops at f18 even on a 48 h run (checked 2026-10-21: 12Z wrfsubhf19 = 404).</summary>
		public const int SubHourlyLastHour = 18;

		/// <summary>Composite reflectivity — the max dBZ in the column: "future radar".</summary>
		public static readonly HrrrField CompositeReflectivity =
			new("refc", "Future radar (composite)", "REFC", "entire atmosphere", IsHourlyMax: false,
				ModelFieldEncoding.Reflectivity, ModelRamps.Reflectivity);

		/// <summary>Reflectivity 1 km above ground — closer to what a radar's low tilt sees.</summary>
		public static readonly HrrrField Reflectivity1Km =
			new("refd1km", "Future radar (1 km)", "REFD", "1000 m above ground", IsHourlyMax: false,
				ModelFieldEncoding.Reflectivity, ModelRamps.Reflectivity);

		/// <summary>2–5 km updraft helicity, max over the past hour — rotating updrafts (supercells). m²/s².</summary>
		public static readonly HrrrField UpdraftHelicity25 =
			new("uh25", "Updraft helicity tracks", "MXUPHL", "5000-2000 m above ground", IsHourlyMax: true,
				ModelFieldEncoding.UpdraftHelicity, ModelRamps.UpdraftHelicity);

		public static IReadOnlyList<HrrrField> All { get; } = new[] { CompositeReflectivity, Reflectivity1Km, UpdraftHelicity25 };

		public static HrrrField? ById(string id) => All.FirstOrDefault(f => f.Id == id);

		/// <summary>The HRRR runs 48 h at 00/06/12/18Z, 18 h every other hour.</summary>
		public static int LastHourOf(DateTimeOffset runUtc) => runUtc.Hour % 6 == 0 ? 48 : 18;
	}
}
