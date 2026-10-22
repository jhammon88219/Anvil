using System;
using System.Collections.Generic;
using System.Linq;
using Anvil.Models;

namespace Anvil.ViewModels
{
	/// <summary>One warning of the day: the place to fly to, whether it is still in effect, and when it ended.</summary>
	/// <param name="Target">Sent = when the EVENT was first issued (IEM's, when known) — not the latest statement's.</param>
	/// <param name="EndedAt">Null while in effect, or for one that left the feed before its end time with no time known
	/// (it was cancelled before this session saw it).</param>
	public sealed record WarningDayEntry(WarningTarget Target, bool InEffect, DateTimeOffset? EndedAt);

	/// <summary>
	/// The NowCast Warnings section's DAY LIST: every TO/SV/FF warning of the convective day (12Z→now), in effect or
	/// ended. ⚠️ TWO SOURCES, on purpose (a forecaster wants it CURRENT): IN EFFECT = CAP's active set, the same one the
	/// map + counts draw, refreshed every live cycle; ENDED = IEM's day so far (<see cref="Services.IPastAlertService.FetchWarningDayAsync"/>,
	/// minutes behind), plus every warning this session SAW in CAP and then lost — so a warning that ends moves to
	/// "ended" at CAP speed, not at IEM's next check. Joined on <see cref="WarningTarget.Key"/>. Pure: the clock is passed in.
	/// </summary>
	public sealed class WarningDayList
	{
		private IReadOnlyList<WarningTarget> _active = Array.Empty<WarningTarget>();
		private IReadOnlyList<WarningTarget> _day = Array.Empty<WarningTarget>();
		private readonly Dictionary<string, (WarningTarget Target, DateTimeOffset LastSeen)> _seen = new();

		private static string KeyOf(WarningTarget t) => t.Key.Length > 0 ? t.Key : t.Id;

		/// <summary>A CAP refresh: the warnings in effect now.</summary>
		public void SetActive(IReadOnlyList<WarningTarget> active, DateTimeOffset now)
		{
			_active = active;
			foreach (var t in active) { _seen[KeyOf(t)] = (t, now); }
		}

		/// <summary>An IEM check: the day's warnings so far.</summary>
		public void SetDay(IReadOnlyList<WarningTarget> day) => _day = day;

		/// <summary>The list, newest issued first. Drops what ended before <paramref name="dayStart"/> (and forgets it).</summary>
		public List<WarningDayEntry> Build(DateTimeOffset dayStart, DateTimeOffset now)
		{
			foreach (var stale in _seen.Where(kv => kv.Value.LastSeen < dayStart).Select(kv => kv.Key).ToList())
			{
				_seen.Remove(stale);
			}
			var day = new Dictionary<string, WarningTarget>();
			foreach (var t in _day) { day.TryAdd(KeyOf(t), t); }

			var entries = new Dictionary<string, WarningDayEntry>();
			// In effect: CAP's (place + tier are CAP's), issued = the event's FIRST issue when IEM knows it.
			foreach (var t in _active.OrderByDescending(t => t.Sent))
			{
				var key = KeyOf(t);
				if (entries.ContainsKey(key)) { continue; }
				var issued = day.TryGetValue(key, out var d) ? d.Sent : t.Sent;
				entries[key] = new WarningDayEntry(t with { Sent = issued }, true, null);
			}
			// Ended, seen this session: CAP's last copy; ended at its end time, or when it left the feed if that was sooner.
			foreach (var (key, (t, lastSeen)) in _seen)
			{
				if (entries.ContainsKey(key)) { continue; }
				var issued = day.TryGetValue(key, out var d) ? d.Sent : t.Sent;
				var ends = t.Ends != default && t.Ends <= lastSeen ? t.Ends : lastSeen;
				entries[key] = new WarningDayEntry(t with { Sent = issued }, false, ends);
			}
			// Ended before this session (IEM only).
			foreach (var (key, t) in day)
			{
				if (entries.ContainsKey(key)) { continue; }
				var ended = t.Ends != default && t.Ends <= now ? t.Ends : (DateTimeOffset?)null;
				if (ended is { } e && e < dayStart) { continue; }
				entries[key] = new WarningDayEntry(t, false, ended);
			}
			return entries.Values
				.OrderByDescending(x => x.Target.Sent)
				.ThenBy(x => KeyOf(x.Target), StringComparer.Ordinal)
				.ToList();
		}
	}
}
