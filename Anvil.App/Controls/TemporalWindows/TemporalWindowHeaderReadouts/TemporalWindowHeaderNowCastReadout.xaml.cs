using System;
using System.Globalization;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.Models;

namespace Anvil.Controls.TemporalWindows.TemporalWindowHeaderReadouts
{
	/// <summary>
	/// The NowCast header's Readout — the clock (see the XAML header). Formats <see cref="Time"/>; the host's
	/// clock tick sets it.
	/// </summary>
	public sealed partial class TemporalWindowHeaderNowCastReadout : UserControl
	{
		public TemporalWindowHeaderNowCastReadout()
		{
			InitializeComponent();
			// The AM/PM slot's two invisible samples: it is as wide as the wider, so noon doesn't nudge the clock.
			AmSample.Text = CultureInfo.CurrentCulture.DateTimeFormat.AMDesignator;
			PmSample.Text = CultureInfo.CurrentCulture.DateTimeFormat.PMDesignator;
		}

		/// <summary>The moment to show (local). Set once per shown second by the host's tick.</summary>
		public DateTimeOffset Time
		{
			get => (DateTimeOffset)GetValue(TimeProperty);
			set => SetValue(TimeProperty, value);
		}
		public static readonly DependencyProperty TimeProperty =
			DependencyProperty.Register(nameof(Time), typeof(DateTimeOffset), typeof(TemporalWindowHeaderNowCastReadout),
				new PropertyMetadata(default(DateTimeOffset), (d, _) => ((TemporalWindowHeaderNowCastReadout)d).Show()));

		private void Show()
		{
			var now = Time;
			// Digits and AM/PM (or Z) are separate cells, as on the radar console's clock (RadarControls). Settings →
			// Units picks 12-hour / 24-hour / UTC; the tooltip always gives the OTHER zone.
			var culture = CultureInfo.CurrentCulture;
			var units = UnitFormat.Current;
			var shown = UnitFormat.Zoned(now);
			ClockDigits.Text = shown.ToString(units.Is24Hour ? "HH:mm" : "h:mm", culture);
			ClockSuffix.Text = units.IsUtc ? "Z" : units.Is24Hour ? string.Empty : shown.ToString("tt", culture);
			var local = now.ToLocalTime();
			ToolTipService.SetToolTip(ClockTime, units.IsUtc
				? $"{local:h:mm tt} {ZoneLabel(local.DateTime)}"
				: $"{ZoneLabel(local.DateTime)} · {now.UtcDateTime:HH:mm} UTC");
		}

		// Windows has no zone ABBREVIATION, only names: "Central Daylight Time" → "CDT" by initials. A name that
		// doesn't abbreviate cleanly (non-US, localized) falls back to the UTC offset.
		private static string ZoneLabel(DateTime local)
		{
			var zone = TimeZoneInfo.Local;
			var name = zone.IsDaylightSavingTime(local) ? zone.DaylightName : zone.StandardName;
			var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
			if (words.Length is >= 2 and <= 4 && words.All(w => char.IsUpper(w[0])))
			{
				return new string(words.Select(w => w[0]).ToArray());
			}
			var offset = zone.GetUtcOffset(local);
			return $"UTC{(offset < TimeSpan.Zero ? "−" : "+")}{offset:hh\\:mm}";
		}
	}
}
