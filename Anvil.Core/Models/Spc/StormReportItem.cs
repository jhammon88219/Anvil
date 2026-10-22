using System.Globalization;
using System.Linq;

namespace Anvil.Models
{
	/// <summary>
	/// One SPC storm report as a ROW of the storm-reports list (the Storm reports section, NowCast + PastCast): the
	/// same fields the map's dot carries (storm-reports.js reads them from the cached GeoJSON), plus the row's
	/// display text. Built by <c>StormReportService</c>; clicking a row flies the map to it.
	/// </summary>
	/// <param name="Kind">"torn" / "wind" / "hail" — the map's layer key.</param>
	/// <param name="Time">SPC's UTC "HHMM" over the convective day (12Z→12Z), as written in the file.</param>
	/// <param name="Mag">SPC's raw magnitude column — EF number / mph / hundredths of an inch, or "UNK".</param>
	public sealed record StormReportItem(string Kind, double Lat, double Lon, string Time, string Mag,
		string Location, string County, string State)
	{
		/// <summary>"20:49Z" — SPC's own UTC wording, for when the convective day isn't known (see <see cref="TimeOn"/>).</summary>
		public string TimeText
		{
			get
			{
				var t = Time.Trim();
				if (t.Length is 3 or 4 && int.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out _))
				{
					t = t.PadLeft(4, '0');
					return $"{t[..2]}:{t[2..]}Z";
				}
				return t;
			}
		}

		/// <summary>The report's real moment on convective <paramref name="day"/> (12Z + <see cref="DayMinute"/>), null if
		/// the time is unreadable — what Settings → Units formats (a local time needs the DATE for its offset).</summary>
		public System.DateTimeOffset? TimeOn(System.DateOnly day) => DayMinute < 0 ? null
			: new System.DateTimeOffset(day.ToDateTime(new System.TimeOnly(12, 0)), System.TimeSpan.Zero).AddMinutes(DayMinute);

		/// <summary>"3 SSW Ash Valley, KS".</summary>
		public string Place => string.Join(", ", new[] { Location.Trim(), State.Trim() }.Where(s => s.Length > 0));

		/// <summary>"EF2" / "65 mph" / "1.75 in" (in the Settings → Units speed / hail units), or "" when SPC has none.
		/// ⚠️ MIRRORS storm-reports.js magText — the popup and the row must word a report the same way; change both.</summary>
		public string MagText
		{
			get
			{
				var m = Mag.Trim();
				if (m.Length == 0 || m == "UNK") { return string.Empty; }
				var ok = int.TryParse(m, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n);
				return Kind switch
				{
					"hail" => ok ? UnitFormat.HailFromInches(n / 100.0) : string.Empty,
					"wind" => ok ? UnitFormat.SpeedFromMph(n) : m,
					"torn" => ok ? $"EF{n}" : m,
					_ => m,
				};
			}
		}

		/// <summary>Minutes into the convective day (12Z = 0), so 01Z the next morning sorts AFTER 23Z. -1 if unreadable.</summary>
		public int DayMinute
		{
			get
			{
				if (!int.TryParse(Time.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var hhmm)) { return -1; }
				var minutes = hhmm / 100 * 60 + hhmm % 100;
				return (minutes - 12 * 60 + 24 * 60) % (24 * 60);
			}
		}
	}
}
