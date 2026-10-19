using System;
using System.Collections.Generic;

namespace Anvil.Models
{
	/// <summary>The feeds whose refresh the live-feed hub schedules — NowCast's six, then ForeCast's SPC outlooks (the
	/// radar keeps its own scan schedule).</summary>
	public enum LiveFeedKind { Warnings, Discussions, StormReports, Watches, Tropical, StormCells, Outlooks }

	/// <summary>
	/// The ONE table of each live feed's facts: whether a live source carries it, which products wake it, and its
	/// polling interval's default and floor (Settings → Live Data → Polling, the user's numbers, 2026-10-19).
	/// </summary>
	/// <remarks>
	/// ⚠️ A PRODUCT IS A TRIGGER, NEVER THE DATA: a matching product makes the feed run its OWN fetch at once (then a
	/// few re-checks while that source catches up — <see cref="ChaseDelays"/>), so every feed keeps ONE data path and its
	/// merge rules. Products are told apart by the AWIPS id's first three letters (PIL "SVROUN" → "SVR").
	/// ⚠️ Storm cells are on no push feed (IEM's storm attributes come from radar volumes) — always polled.
	/// </remarks>
	public static class LiveFeedKinds
	{
		/// <summary>Settings order (the Polling table's rows).</summary>
		public static IReadOnlyList<LiveFeedKind> All { get; } = new[]
		{
			LiveFeedKind.Warnings, LiveFeedKind.Discussions, LiveFeedKind.StormReports,
			LiveFeedKind.Watches, LiveFeedKind.Tropical, LiveFeedKind.StormCells, LiveFeedKind.Outlooks,
		};

		/// <summary>True = a live source carries this feed's products (its schedule pauses while connected).</summary>
		public static bool IsPushed(LiveFeedKind kind) => kind != LiveFeedKind.StormCells;

		public static string NameOf(LiveFeedKind kind) => kind switch
		{
			LiveFeedKind.Warnings => "Warnings",
			LiveFeedKind.Discussions => "Discussions",
			LiveFeedKind.StormReports => "Storm reports",
			LiveFeedKind.Watches => "Watches",
			LiveFeedKind.Tropical => "Tropical",
			LiveFeedKind.Outlooks => "Outlooks",
			_ => "Storm cells",
		};

		/// <summary>The polling interval when the user hasn't set one.</summary>
		public static TimeSpan DefaultPoll(LiveFeedKind kind) => TimeSpan.FromSeconds(kind switch
		{
			LiveFeedKind.Warnings => 30,
			LiveFeedKind.Discussions => 120,
			LiveFeedKind.StormReports => 120,
			LiveFeedKind.Watches => 120,
			LiveFeedKind.Tropical => 300,
			// Conditional GETs make an unchanged cycle 23 cheap 304s; was a fixed 15 min before live data.
			LiveFeedKind.Outlooks => 300,
			_ => 60,
		});

		/// <summary>The fastest the user may poll this feed.</summary>
		public static TimeSpan MinimumPoll(LiveFeedKind kind) => TimeSpan.FromSeconds(kind switch
		{
			LiveFeedKind.Warnings => 15,
			LiveFeedKind.Discussions => 60,
			LiveFeedKind.StormReports => 60,
			LiveFeedKind.Watches => 60,
			LiveFeedKind.Tropical => 120,
			LiveFeedKind.Outlooks => 120,
			_ => 30,
		});

		/// <summary>The slowest the user may poll any feed.</summary>
		public static readonly TimeSpan MaximumPoll = TimeSpan.FromMinutes(30);

		/// <summary>A stored interval (seconds; null = the default) clamped to the feed's floor and the ceiling.</summary>
		public static TimeSpan PollOf(LiveFeedKind kind, int? seconds)
		{
			if (seconds is not { } s) { return DefaultPoll(kind); }
			var t = TimeSpan.FromSeconds(s);
			if (t < MinimumPoll(kind)) { return MinimumPoll(kind); }
			return t > MaximumPoll ? MaximumPoll : t;
		}

		/// <summary>
		/// After a trigger's own fetch, the feed re-checks after each of these gaps (≈ +10 s, +30 s, +1 min, +2 min):
		/// a product reaches the push feed before it reaches every DATA endpoint (api.weather.gov's CAP, the WWA map
		/// service), so one fetch at the trigger can come back without it. A new trigger restarts the run.
		/// </summary>
		public static IReadOnlyList<TimeSpan> ChaseDelays { get; } = new[]
		{
			TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60),
		};

		/// <summary>
		/// The OUTLOOKS' longer run (≈ +15 s … +20 min): SPC's map files land well after the text product — measured
		/// 2026-10-19, convective GeoJSON 4–6 min after the SWODY*/SWOD48 time, the fire MapServer ~11½ min after FWDDY1.
		/// The run stops early per file: the outlook VM reports each file that changed (<c>LiveFeedChannel.Caught</c>).
		/// </summary>
		public static IReadOnlyList<TimeSpan> OutlookChaseDelays { get; } = new[]
		{
			TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60),
			TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(120),
			TimeSpan.FromSeconds(180), TimeSpan.FromSeconds(240), TimeSpan.FromSeconds(300),
		};

		/// <summary>The re-check run for <paramref name="kind"/>.</summary>
		public static IReadOnlyList<TimeSpan> ChaseDelaysOf(LiveFeedKind kind) =>
			kind == LiveFeedKind.Outlooks ? OutlookChaseDelays : ChaseDelays;

		/// <summary>SPC's outlook text products (AWIPS ids) — each wakes the outlooks, for its own files only
		/// (<c>Services.SpcOutlookCatalog.IdsForProduct</c>).</summary>
		public static IReadOnlyList<string> OutlookProducts { get; } = new[]
		{
			"SWODY1", "SWODY2", "SWODY3", "SWOD48", "FWDDY1", "FWDDY2", "FWDD38",
		};

		/// <summary>
		/// While a live source is connected a pushed feed still re-checks this often — a SAFETY net for what no product
		/// announces (a warning that lapses on its own clock, a product the source never relayed). Not user-set.
		/// </summary>
		public static readonly TimeSpan LiveSafetyInterval = TimeSpan.FromMinutes(10);

		/// <summary>A product in words for Settings' "Last product" — "Severe thunderstorm warning" — from its AWIPS id.</summary>
		public static string DescribeProduct(string awipsId)
		{
			var pil = (awipsId ?? "").Trim().ToUpperInvariant();
			if (pil.StartsWith("SWOMCD", StringComparison.Ordinal)) { return "Mesoscale discussion"; }
			if (pil.StartsWith("FFGMPD", StringComparison.Ordinal)) { return "Precipitation discussion"; }
			switch (pil)
			{
				case "SWODY1": return "Day 1 convective outlook";
				case "SWODY2": return "Day 2 convective outlook";
				case "SWODY3": return "Day 3 convective outlook";
				case "SWOD48": return "Day 4–8 convective outlook";
				case "FWDDY1": return "Day 1 fire weather outlook";
				case "FWDDY2": return "Day 2 fire weather outlook";
				case "FWDD38": return "Day 3–8 fire weather outlook";
			}
			return (pil.Length >= 3 ? pil[..3] : pil) switch
			{
				"TOR" => "Tornado warning",
				"SVR" => "Severe thunderstorm warning",
				"SVS" => "Severe weather statement",
				"FFW" => "Flash flood warning",
				"FFS" => "Flash flood statement",
				"SEL" => "Watch",
				"WOU" => "Watch outline update",
				"WCN" => "Watch county update",
				"WWP" => "Watch probabilities",
				"FFA" => "Flood watch",
				"LSR" => "Local storm report",
				"TCV" => "Tropical watch/warning update",
				"TCP" => "Tropical advisory",
				_ => pil,
			};
		}

		/// <summary>The feed a product wakes, from its AWIPS id ("SVROUN", "SWOMCD"), or null when it wakes none.</summary>
		public static LiveFeedKind? KindOfProduct(string? awipsId)
		{
			if (string.IsNullOrWhiteSpace(awipsId)) { return null; }
			var pil = awipsId.Trim().ToUpperInvariant();
			// The two discussion PILs are whole six-letter ids (SPC MCD, WPC MPD).
			if (pil.StartsWith("SWOMCD", StringComparison.Ordinal) || pil.StartsWith("FFGMPD", StringComparison.Ordinal))
			{
				return LiveFeedKind.Discussions;
			}
			// SPC's outlooks: whole ids (SWODY1…FWDD38) — never the "SWO" prefix, which SWOMCD shares.
			foreach (var outlook in OutlookProducts)
			{
				if (pil == outlook) { return LiveFeedKind.Outlooks; }
			}
			if (pil.Length < 3) { return null; }
			return pil[..3] switch
			{
				// Tornado / severe thunderstorm / flash flood warnings and their statements (updates, cancels, expiries).
				"TOR" or "SVR" or "SVS" or "FFW" or "FFS" => LiveFeedKind.Warnings,
				// SPC watch (SEL), its outline + county updates (WOU, WCN), watch probabilities (WWP); flood watches (FFA).
				"SEL" or "WOU" or "WCN" or "WWP" or "FFA" => LiveFeedKind.Watches,
				"LSR" => LiveFeedKind.StormReports,
				// Tropical watches/warnings by zone (TCV) and the public advisory (TCP) that carries a change.
				"TCV" or "TCP" => LiveFeedKind.Tropical,
				_ => null,
			};
		}
	}
}
