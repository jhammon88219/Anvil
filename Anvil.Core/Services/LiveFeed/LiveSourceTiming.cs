using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Anvil.Models;

namespace Anvil.Services
{
	/// <summary>One live source's name in a timing run.</summary>
	public enum TimedSource { Iem, Nwws }

	/// <summary>
	/// What a timing run has found so far. Leads are IEM arrival MINUS Weather Wire arrival (positive = Weather Wire first).
	/// Lags are arrival minus the product's stamped issue time — only to the MINUTE (issue times are minute-stamped), so the
	/// pair leads are the precise numbers and the lags a rough cross-check.
	/// </summary>
	public sealed record LiveSourceTimingSummary(
		int HeardIem, int HeardNwws, int Pairs,
		double? MedianLeadSeconds, double? P10LeadSeconds, double? P90LeadSeconds, double? NwwsFirstShare,
		double? MedianIemLagSeconds, double? MedianNwwsLagSeconds);

	/// <summary>
	/// Dev → Live source timing's matcher: the SAME product heard on both sources, paired, and how much sooner one had it.
	/// Pure and thread-safe — the runner feeds it arrivals stamped the moment each source handed a product over.
	/// </summary>
	/// <remarks>
	/// ⚠️ THE MATCH KEY: the sources name a product differently (IEM's "yyyyMMddHHmm-CCCC-TTAAII-AWIPSID", Weather Wire's
	/// "process.sequence"), so a product is matched by OFFICE · AWIPS id · issue MINUTE — and one minute either side, since
	/// the two can stamp the same product a minute apart. The first arrival per source per key counts (corrections that
	/// repeat a key are ignored). Unpaired arrivals older than <see cref="PairWindow"/> are dropped.
	/// </remarks>
	public sealed class LiveSourceTiming
	{
		/// <summary>An arrival waits this long for its twin on the other source.</summary>
		public static readonly TimeSpan PairWindow = TimeSpan.FromMinutes(10);

		/// <summary>A lag outside this is a stamp we can't trust (a re-send, a clock), not a delivery time.</summary>
		private static readonly TimeSpan MaxTrustedLag = TimeSpan.FromMinutes(30);

		private readonly object _gate = new();
		private readonly Dictionary<string, DateTimeOffset>[] _waiting = { new(StringComparer.Ordinal), new(StringComparer.Ordinal) };
		private readonly HashSet<string>[] _paired = { new(StringComparer.Ordinal), new(StringComparer.Ordinal) };
		private readonly List<double> _leads = new();
		private readonly List<double>[] _lags = { new(), new() };
		private readonly int[] _heard = new int[2];

		/// <summary>A product <paramref name="source"/> handed over at <paramref name="arrived"/> (stamped by the caller).</summary>
		public void Heard(TimedSource source, LiveProduct product, DateTimeOffset arrived)
		{
			var me = (int)source;
			var other = 1 - me;
			var key = KeyOf(product.Office, product.AwipsId, product.Issued);
			lock (_gate)
			{
				_heard[me]++;
				var lag = arrived - product.Issued;
				if (lag >= -TimeSpan.FromMinutes(1) && lag <= MaxTrustedLag) { _lags[me].Add(lag.TotalSeconds); }

				Expire(arrived);
				if (_paired[me].Contains(key) || _waiting[me].ContainsKey(key)) { return; } // a repeat of the same product

				// Its twin on the other source: the same minute, or one minute either side.
				foreach (var twin in Neighbours(product))
				{
					if (_waiting[other].Remove(twin, out var otherArrived))
					{
						var iem = source == TimedSource.Iem ? arrived : otherArrived;
						var nwws = source == TimedSource.Nwws ? arrived : otherArrived;
						_leads.Add((iem - nwws).TotalSeconds);
						_paired[me].Add(key);
						_paired[other].Add(twin);
						return;
					}
				}
				_waiting[me][key] = arrived;
			}
		}

		public LiveSourceTimingSummary Summary()
		{
			lock (_gate)
			{
				var leads = _leads.OrderBy(x => x).ToArray();
				return new LiveSourceTimingSummary(
					_heard[0], _heard[1], leads.Length,
					Percentile(leads, 0.5), Percentile(leads, 0.1), Percentile(leads, 0.9),
					leads.Length == 0 ? null : leads.Count(x => x > 0) / (double)leads.Length,
					Percentile(_lags[0].OrderBy(x => x).ToArray(), 0.5),
					Percentile(_lags[1].OrderBy(x => x).ToArray(), 0.5));
			}
		}

		/// <summary>OFFICE|AWIPS|yyyyMMddHHmm (UTC). Internal for tests.</summary>
		internal static string KeyOf(string office, string awips, DateTimeOffset issued) =>
			string.Concat(office.Trim().ToUpperInvariant(), "|", awips.Trim().ToUpperInvariant(), "|",
				issued.ToUniversalTime().ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture));

		private static IEnumerable<string> Neighbours(LiveProduct p)
		{
			yield return KeyOf(p.Office, p.AwipsId, p.Issued);
			yield return KeyOf(p.Office, p.AwipsId, p.Issued.AddMinutes(-1));
			yield return KeyOf(p.Office, p.AwipsId, p.Issued.AddMinutes(1));
		}

		private void Expire(DateTimeOffset now)
		{
			foreach (var waiting in _waiting)
			{
				foreach (var stale in waiting.Where(kv => now - kv.Value > PairWindow).Select(kv => kv.Key).ToList()) { waiting.Remove(stale); }
			}
			// Paired keys only guard against repeats; a run's worth is small, but don't let a day-long run grow forever.
			foreach (var paired in _paired) { if (paired.Count > 20000) { paired.Clear(); } }
		}

		/// <summary>Nearest-rank percentile of a SORTED array, or null when empty. Internal for tests.</summary>
		internal static double? Percentile(double[] sorted, double p)
		{
			if (sorted.Length == 0) { return null; }
			var i = (int)Math.Round(p * (sorted.Length - 1), MidpointRounding.AwayFromZero);
			return sorted[Math.Clamp(i, 0, sorted.Length - 1)];
		}
	}
}
