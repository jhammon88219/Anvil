using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Anvil.Models;
using Anvil.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Anvil.ViewModels
{
	/// <summary>
	/// Settings → Storage (<c>StorageManagerSettingsConsole</c>): what Anvil keeps on disk, as five
	/// <see cref="StorageItemViewModel"/> rows in a FIXED order (= the XAML's declared rows and bar segments) plus the
	/// total. Measuring and file work go through <see cref="IStorageService"/>; radar VOLUMES through
	/// <see cref="ILevel2RadarService"/> (the sealed engine owns that cache, its clear and its sweep).
	/// </summary>
	/// <remarks>
	/// A subsystem VM on <see cref="MapViewModel"/>. ⚠️ Its async methods drive WinUI-bound properties, so they are
	/// awaited on the UI thread (no <c>ConfigureAwait(false)</c>) — the services do their own off-threading.
	/// ⚠️ Limits are PERSISTED (<see cref="AppSettings"/>): radar volumes <c>RadarCache*</c> + diagnostics
	/// <c>RadarDiagnostics*</c> are applied by the ENGINE (Level2RadarService reads them live and re-sweeps on change);
	/// weather data + logs = <c>WeatherData*</c> / <c>Logs*</c>, applied here at launch (<see cref="SweepAtLaunch"/>) and
	/// on every change.
	/// </remarks>
	public sealed class StorageSettingsViewModel : ObservableObject
	{
		private readonly ILevel2RadarService _radar;
		private readonly IStorageService _storage;
		private readonly AppSettings _settings;
		private Action? _clearSiteUsage;
		private int _refreshVersion;

		public StorageSettingsViewModel(ILevel2RadarService radarService, ISettingsService settings, IStorageService storage)
		{
			_radar = radarService;
			_storage = storage;
			_settings = settings.Settings;
			var fresh = new AppSettings(); // the DEFAULT column = what a first run gets

			// MAPS (2026-11-16): every map file Anvil knows (in use, downloaded, added from anywhere — AttachMaps). No Clear and no
			// limits: maps are deleted one file at a time in Storage's Map card.
			Maps = new StorageItemViewModel(StorageKind.Maps, "Maps", "Offline map files",
				StorageService.AnvilRoot, clear: null, confirmClear: false,
				null, null, null, null, null, null, noKeepText: "");

			// Radar volumes + diagnostics: the ENGINE applies these (Level2RadarService re-sweeps on the setting change), so
			// this side only re-measures a moment later.
			var radarMax = StorageLimits.Resolve(StorageLimits.RadarVolumeMax, (long)Math.Max(1, _settings.RadarCacheMaxGb) * StorageLimits.GB, days: false);
			var radarKeep = StorageLimits.Resolve(StorageLimits.RadarVolumeKeep, Math.Max(1, _settings.RadarCacheKeepDays), days: true);
			RadarVolumes = new StorageItemViewModel(StorageKind.RadarVolumes, "Radar volumes", "Loops you've loaded",
				storage.FolderOf(StorageKind.RadarVolumes), ClearRadarVolumesAsync, confirmClear: false,
				radarMax.List, radarMax.Pick, v => { _settings.RadarCacheMaxGb = (int)(v / StorageLimits.GB); _ = RefreshAfterEngineSweepAsync(); },
				radarKeep.List, radarKeep.Pick, v => { _settings.RadarCacheKeepDays = (int)v; _ = RefreshAfterEngineSweepAsync(); },
				(long)fresh.RadarCacheMaxGb * StorageLimits.GB, fresh.RadarCacheKeepDays);

			var diagMax = StorageLimits.Resolve(StorageLimits.DiagnosticsMax, (long)Math.Max(1, _settings.RadarDiagnosticsMaxGb) * StorageLimits.GB, days: false);
			var diagKeep = StorageLimits.Resolve(StorageLimits.DiagnosticsKeep, Math.Max(1, _settings.RadarDiagnosticsKeepDays), days: true);
			RadarDiagnostics = new StorageItemViewModel(StorageKind.RadarDiagnostics, "Radar diagnostics", "Troubleshooting records",
				storage.FolderOf(StorageKind.RadarDiagnostics), () => ClearKindAsync(StorageKind.RadarDiagnostics), confirmClear: false,
				diagMax.List, diagMax.Pick, v => { _settings.RadarDiagnosticsMaxGb = (int)(v / StorageLimits.GB); _ = RefreshAfterEngineSweepAsync(); },
				diagKeep.List, diagKeep.Pick, v => { _settings.RadarDiagnosticsKeepDays = (int)v; _ = RefreshAfterEngineSweepAsync(); },
				(long)fresh.RadarDiagnosticsMaxGb * StorageLimits.GB, fresh.RadarDiagnosticsKeepDays);

			var wMax = StorageLimits.Resolve(StorageLimits.WeatherDataMax, (long)_settings.WeatherDataMaxMb * StorageLimits.MB, days: false);
			var wKeep = StorageLimits.Resolve(StorageLimits.WeatherDataKeep, _settings.WeatherDataKeepDays, days: true);
			WeatherData = new StorageItemViewModel(StorageKind.WeatherData, "Weather data", "Outlooks, warnings, reports, cells",
				storage.FolderOf(StorageKind.WeatherData), () => ClearKindAsync(StorageKind.WeatherData), confirmClear: false,
				wMax.List, wMax.Pick, v => { _settings.WeatherDataMaxMb = (int)(v / StorageLimits.MB); _ = SweepThenRefreshAsync(StorageKind.WeatherData); },
				wKeep.List, wKeep.Pick, v => { _settings.WeatherDataKeepDays = (int)v; _ = SweepThenRefreshAsync(StorageKind.WeatherData); },
				(long)fresh.WeatherDataMaxMb * StorageLimits.MB, fresh.WeatherDataKeepDays);

			var lMax = StorageLimits.Resolve(StorageLimits.LogsMax, (long)_settings.LogsMaxMb * StorageLimits.MB, days: false);
			var lKeep = StorageLimits.Resolve(StorageLimits.LogsKeep, _settings.LogsKeepDays, days: true);
			Logs = new StorageItemViewModel(StorageKind.Logs, "Logs", "",
				storage.FolderOf(StorageKind.Logs), () => ClearKindAsync(StorageKind.Logs), confirmClear: false,
				lMax.List, lMax.Pick, v => { _settings.LogsMaxMb = (int)(v / StorageLimits.MB); _ = SweepThenRefreshAsync(StorageKind.Logs); },
				lKeep.List, lKeep.Pick, v => { _settings.LogsKeepDays = (int)v; _ = SweepThenRefreshAsync(StorageKind.Logs); },
				(long)fresh.LogsMaxMb * StorageLimits.MB, fresh.LogsKeepDays);

			UsageHistory = new StorageItemViewModel(StorageKind.UsageHistory, "Your usage history", "Atlas “Your use”",
				storage.FolderOf(StorageKind.UsageHistory), ClearUsageAsync, confirmClear: true,
				null, null, null, null, null, null);

			Items = new[] { Maps, RadarVolumes, RadarDiagnostics, WeatherData, Logs, UsageHistory };

			// BRUSHING (2026-11-18; flipped 2026-11-19, the user's call): the bar rests DULL — every segment dimmed — and only the
			// kind whose table row is hovered lights up. A full-colour bar at rest read as "everything highlighted".
			foreach (var item in Items)
			{
				item.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(StorageItemViewModel.IsHovered)) { ApplyHover(); } };
			}
		}

		private void ApplyHover()
		{
			StorageItemViewModel? hovered = null;
			foreach (var item in Items) { if (item.IsHovered) { hovered = item; } }
			foreach (var item in Items) { item.IsDimmed = !ReferenceEquals(item, hovered); }
		}

		public StorageItemViewModel Maps { get; }
		public StorageItemViewModel RadarVolumes { get; }
		public StorageItemViewModel RadarDiagnostics { get; }
		public StorageItemViewModel WeatherData { get; }
		public StorageItemViewModel Logs { get; }
		public StorageItemViewModel UsageHistory { get; }

		/// <summary>The rows in display order.</summary>
		public IReadOnlyList<StorageItemViewModel> Items { get; }

		private string _totalText = "…";
		/// <summary>The usage bar's big number: every row added up.</summary>
		public string TotalText
		{
			get => _totalText;
			private set => SetProperty(ref _totalText, value);
		}

		/// <summary>Usage history's clear also forgets the LIVE site-usage record (SiteUsageTracker owns it, built after
		/// this VM) — MapViewModel hands it in.</summary>
		internal void AttachSiteUsage(Action clearAll) => _clearSiteUsage = clearAll;

		private Func<IReadOnlyList<string>>? _mapFiles;
		private Func<string>? _mapFolder;

		/// <summary>The Maps row: the map files to measure + the folder its button opens (BasemapSetupViewModel owns both —
		/// MapViewModel hands them in, like <see cref="AttachSiteUsage"/>). Both re-read at every refresh (the folder can move).</summary>
		internal void AttachMaps(Func<IReadOnlyList<string>> mapFiles, Func<string> folder)
		{
			_mapFiles = mapFiles;
			_mapFolder = folder;
			Maps.Folder = folder();
		}

		/// <summary>The Disk usage card's Restore defaults: every limit back to the DEFAULT column (sweeps follow the setters).</summary>
		public void RestoreDefaults()
		{
			foreach (var item in Items) item.RestoreDefaults();
		}

		// The paths are read on the UI thread (the settings + folder list), then sized off it.
		private Task<long> MeasureMapsAsync()
		{
			var paths = _mapFiles?.Invoke() ?? Array.Empty<string>();
			return Task.Run(() =>
			{
				long total = 0;
				foreach (var p in paths)
				{
					try { total += new System.IO.FileInfo(p).Length; } catch { /* gone or unreadable: not counted */ }
				}
				return total;
			});
		}

		/// <summary>Weather data + logs limits, once per launch, off the startup path. (Radar volumes are swept by the
		/// engine itself.)</summary>
		public void SweepAtLaunch()
		{
			_ = _storage.SweepAsync(StorageKind.WeatherData, (long)_settings.WeatherDataMaxMb * StorageLimits.MB, TimeSpan.FromDays(_settings.WeatherDataKeepDays));
			_ = _storage.SweepAsync(StorageKind.Logs, (long)_settings.LogsMaxMb * StorageLimits.MB, TimeSpan.FromDays(_settings.LogsKeepDays));
		}

		/// <summary>Re-measure every row (off-thread in the services). A newer refresh wins over a slower older one.</summary>
		public async Task RefreshAsync()
		{
			var version = ++_refreshVersion;
			if (_mapFolder is not null) { Maps.Folder = _mapFolder(); }
			var sizes = new long?[Items.Count];
			var tasks = new Task[Items.Count];
			for (int i = 0; i < Items.Count; i++)
			{
				int at = i;
				tasks[i] = MeasureOneAsync(Items[at].Kind).ContinueWith(t => sizes[at] = t.IsCompletedSuccessfully ? t.Result : null,
					TaskScheduler.Default);
			}
			await Task.WhenAll(tasks);
			if (version != _refreshVersion) return;

			long total = 0;
			foreach (var s in sizes) total += s ?? 0;
			for (int i = 0; i < Items.Count; i++)
			{
				if (sizes[i] is { } b) Items[i].ApplySize(b, total);
				else Items[i].ApplyUnknown();
			}
			TotalText = StorageLimits.FormatBytes(total);
		}

		private Task<long> MeasureOneAsync(StorageKind kind) => kind switch
		{
			StorageKind.RadarVolumes => _radar.GetCacheSizeBytesAsync(),
			StorageKind.Maps => MeasureMapsAsync(),
			_ => _storage.MeasureAsync(kind),
		};

		private async Task SweepThenRefreshAsync(StorageKind kind)
		{
			var (max, keep) = kind == StorageKind.WeatherData
				? ((long)_settings.WeatherDataMaxMb * StorageLimits.MB, _settings.WeatherDataKeepDays)
				: ((long)_settings.LogsMaxMb * StorageLimits.MB, _settings.LogsKeepDays);
			try { await _storage.SweepAsync(kind, max, TimeSpan.FromDays(keep)); }
			catch { /* best effort — the readout below still tells the truth */ }
			await RefreshAsync();
		}

		// The engine's sweep runs on its own thread with no completion signal (adding one = widening the sealed
		// interface), so the readout re-measures once after a short wait — the colour updates at once anyway.
		private async Task RefreshAfterEngineSweepAsync()
		{
			await RefreshAsync();
			await Task.Delay(1500);
			await RefreshAsync();
		}

		private async Task ClearRadarVolumesAsync()
		{
			await _radar.ClearCacheAsync();
			await RefreshAsync();
		}

		private async Task ClearKindAsync(StorageKind kind)
		{
			try { await _storage.ClearAsync(kind); }
			catch { /* best effort */ }
			await RefreshAsync();
		}

		private async Task ClearUsageAsync()
		{
			_clearSiteUsage?.Invoke(); // UI thread: SiteUsageStore is UI-thread only
			await ClearKindAsync(StorageKind.UsageHistory);
		}
	}
}
