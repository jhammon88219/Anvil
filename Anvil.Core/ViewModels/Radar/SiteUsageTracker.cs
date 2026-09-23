using System;
using System.Collections.Generic;
using System.Linq;
using Anvil.Models;
using Anvil.Services;

namespace Anvil.ViewModels
{
	/// <summary>One loop the user asked for — see <see cref="RadarViewModel.SiteLoaded"/>.</summary>
	public sealed record SiteLoad(RadarSite Site, bool Replay);

	/// <summary>
	/// Decides WHEN usage happened and tells <see cref="SiteUsageStore"/>: counts each
	/// <see cref="RadarViewModel.SiteLoaded"/>, and runs the SITE-HOURS clock for the loaded site. Built by the
	/// <see cref="MapViewModel"/> coordinator; the Radar Atlas reads it back.
	/// </summary>
	/// <remarks>
	/// THE CLOCK: starts on a load, stops when the selection moves off that site (another site, "None", a mode
	/// switch), PAUSES while the main window is minimized (<see cref="SetMinimized"/>, pushed by MainWindow),
	/// and banks what it has on <see cref="Shutdown"/>. ⚠️ Order matters on a live pick: the
	/// SelectedRadarOption change (stop the old clock) is raised BEFORE SiteLoaded (start the new one).
	/// ⚠️ A running stretch lives only in memory until it's banked — on a selection change, a minimize, a
	/// clear, shutdown, or a checkpoint every <see cref="CheckpointEvery"/> (so a crash costs at most that).
	/// The wording that explains this rule is RadarGlossary.SiteHours — change both.
	/// </remarks>
	public sealed class SiteUsageTracker
	{
		/// <summary>How long a running stretch may go unbanked. Checked when the loop lands a frame.</summary>
		internal static readonly TimeSpan CheckpointEvery = TimeSpan.FromMinutes(5);

		private readonly SiteUsageStore _store;
		private readonly Func<DateTimeOffset> _now;
		private string? _currentId;          // the site on the clock (null = none)
		private DateTimeOffset? _runningSince; // null = paused (minimized) or nothing on the clock
		private bool _minimized;

		public SiteUsageTracker(RadarViewModel radar, SiteUsageStore store)
			: this(store, null)
		{
			radar.SiteLoaded += (_, load) => OnSiteLoaded(load.Site.Id, load.Replay);
			radar.PropertyChanged += (_, e) =>
			{
				if (e.PropertyName == nameof(RadarViewModel.SelectedRadarOption))
				{
					OnSelectionChanged(radar.SelectedRadarOption?.Site?.Id);
				}
				else if (e.PropertyName == nameof(RadarViewModel.NewestLoadedFrameTime))
				{
					OnFrameLanded();
				}
			};
		}

		/// <summary>TESTS ONLY (plus the public ctor) — the clock logic without a radar VM; drive it through
		/// <see cref="OnSiteLoaded"/> / <see cref="OnSelectionChanged"/> / <see cref="OnFrameLanded"/>.</summary>
		internal SiteUsageTracker(SiteUsageStore store, Func<DateTimeOffset>? clock)
		{
			_store = store;
			_now = clock ?? (() => DateTimeOffset.UtcNow);
			_store.Changed += (_, id) => Changed?.Invoke(this, id);
		}

		/// <summary>Raised after any recorded change, with the site ICAO — or null for "every site".</summary>
		public event EventHandler<string?>? Changed;

		// A loop the user asked for. A reload of the SAME site (a new replay window) banks and restarts; a new
		// site took over from the selection change already.
		internal void OnSiteLoaded(string siteId, bool replay)
		{
			Bank(keepRunning: false);
			_store.RecordLoad(siteId, replay, _now());
			_currentId = siteId;
			_runningSince = _minimized ? null : _now();
		}

		// The selection moved. Off the clocked site → bank and stop (another site's load starts it again).
		internal void OnSelectionChanged(string? siteId)
		{
			if (!string.Equals(siteId, _currentId, StringComparison.OrdinalIgnoreCase))
			{
				Bank(keepRunning: false);
				_currentId = null;
			}
		}

		// The loop landed a frame — the cheap moment to checkpoint a long unbanked stretch.
		internal void OnFrameLanded()
		{
			if (_runningSince is { } since && _now() - since >= CheckpointEvery)
			{
				Bank(keepRunning: true);
			}
		}

		/// <summary>The main window was minimized (pause) or restored (resume). Idempotent.</summary>
		public void SetMinimized(bool minimized)
		{
			if (minimized == _minimized) return;
			_minimized = minimized;
			if (minimized)
			{
				Bank(keepRunning: false);
			}
			else if (_currentId is not null)
			{
				_runningSince = _now();
			}
		}

		/// <summary>Bank the running stretch (app closing). The clock is left stopped.</summary>
		public void Shutdown()
		{
			// DIAG (usage persistence): proves the Closed → MapViewModel.Shutdown path reached us.
			Services.RadarDiagnostics.Log("usage", "shutdown", ("site", _currentId ?? "none"),
				("runningSec", _runningSince is { } s ? (int)(_now() - s).TotalSeconds : -1));
			Bank(keepRunning: false);
		}

		// Adds the running stretch to the store. keepRunning = restart the stretch from now (a checkpoint);
		// otherwise the clock stops until something starts it.
		private void Bank(bool keepRunning)
		{
			var now = _now();
			if (_currentId is { } id && _runningSince is { } since)
			{
				_store.AddTimeLoaded(id, now - since, now);
			}
			_runningSince = keepRunning && _currentId is not null && !_minimized ? now : null;
		}

		// ── Read-back (the Atlas) ────────────────────────────────────────────────────────────────

		/// <summary>This site's record, or null if never used.</summary>
		public SiteUsage? Get(string siteId) => _store.Get(siteId);

		/// <summary>Whether this site is the one on the clock right now (the tile's "Now").</summary>
		public bool IsCurrent(string siteId) =>
			string.Equals(siteId, _currentId, StringComparison.OrdinalIgnoreCase);

		/// <summary>Site hours in seconds, INCLUDING the unbanked running stretch for the current site.</summary>
		public double SecondsLoaded(string siteId) =>
			(_store.Get(siteId)?.SecondsLoaded ?? 0) + RunningSeconds(siteId);

		private double RunningSeconds(string siteId) =>
			IsCurrent(siteId) && _runningSince is { } since ? Math.Max(0, (_now() - since).TotalSeconds) : 0;

		/// <summary>How many sites have any usage at all.</summary>
		public int UsedSiteCount => _store.All.Count;

		/// <summary>1-based rank among used sites by site hours (ties broken by loads, then ICAO), or null if
		/// this site is unused.</summary>
		public int? Rank(string siteId)
		{
			if (_store.Get(siteId) is null) return null;
			var order = _store.All
				.OrderByDescending(kv => SecondsLoaded(kv.Key))
				.ThenByDescending(kv => kv.Value.TotalLoads)
				.ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
				.Select(kv => kv.Key)
				.ToList();
			return order.FindIndex(k => string.Equals(k, siteId, StringComparison.OrdinalIgnoreCase)) + 1;
		}

		/// <summary>Totals across every used site — the "clear all" line of the confirmation.</summary>
		public (int Sites, double Seconds, DateTimeOffset? Since) Totals()
		{
			var all = _store.All;
			return (all.Count,
				all.Keys.Sum(SecondsLoaded),
				all.Values.Select(u => u.FirstUsedUtc).Where(t => t is not null).Min());
		}

		// ── Clearing ─────────────────────────────────────────────────────────────────────────────

		/// <summary>Forget one site. If it's on the clock, the clock keeps running from NOW — the time before the
		/// clear must not be re-added when it's next banked.</summary>
		public void Clear(string siteId)
		{
			if (IsCurrent(siteId) && _runningSince is not null) _runningSince = _now();
			_store.Clear(siteId);
		}

		/// <summary>Forget every site (same restart rule for the running clock).</summary>
		public void ClearAll()
		{
			if (_runningSince is not null) _runningSince = _now();
			_store.ClearAll();
		}
	}
}
