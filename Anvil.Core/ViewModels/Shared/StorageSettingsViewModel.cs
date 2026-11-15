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
	/// ⚠️ Limits are PERSISTED (<see cref="AppSettings"/>): radar volume Max size = <c>RadarCacheMaxGb</c> (enforced by
	/// the engine's own sweeps); weather data + logs = <c>WeatherData*</c> / <c>Logs*</c>, applied here at launch
	/// (<see cref="SweepAtLaunch"/>) and on every change. The three greyed engine limits are read-only mirrors
	/// (<see cref="StorageLimits.RadarVolumeKeepFixed"/> etc.).
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

			var radarMax = StorageLimits.Resolve(StorageLimits.RadarVolumeMax, (long)Math.Max(1, _settings.RadarCacheMaxGb) * StorageLimits.GB, days: false);
			RadarVolumes = new StorageItemViewModel(StorageKind.RadarVolumes, "Radar volumes", "— loops you've loaded",
				storage.FolderOf(StorageKind.RadarVolumes), ClearRadarVolumesAsync, confirmClear: false,
				radarMax.List, radarMax.Pick, v => { _settings.RadarCacheMaxGb = (int)(v / StorageLimits.GB); _ = RefreshAsync(); },
				new[] { StorageLimits.RadarVolumeKeepFixed }, StorageLimits.RadarVolumeKeepFixed, null);

			RadarDiagnostics = new StorageItemViewModel(StorageKind.RadarDiagnostics, "Radar diagnostics", "— troubleshooting records",
				storage.FolderOf(StorageKind.RadarDiagnostics), () => ClearKindAsync(StorageKind.RadarDiagnostics), confirmClear: false,
				new[] { StorageLimits.DiagnosticsMaxFixed }, StorageLimits.DiagnosticsMaxFixed, null,
				new[] { StorageLimits.DiagnosticsKeepFixed }, StorageLimits.DiagnosticsKeepFixed, null);

			var wMax = StorageLimits.Resolve(StorageLimits.WeatherDataMax, (long)_settings.WeatherDataMaxMb * StorageLimits.MB, days: false);
			var wKeep = StorageLimits.Resolve(StorageLimits.WeatherDataKeep, _settings.WeatherDataKeepDays, days: true);
			WeatherData = new StorageItemViewModel(StorageKind.WeatherData, "Weather data", "— outlooks, warnings, reports, cells",
				storage.FolderOf(StorageKind.WeatherData), () => ClearKindAsync(StorageKind.WeatherData), confirmClear: false,
				wMax.List, wMax.Pick, v => { _settings.WeatherDataMaxMb = (int)(v / StorageLimits.MB); _ = SweepThenRefreshAsync(StorageKind.WeatherData); },
				wKeep.List, wKeep.Pick, v => { _settings.WeatherDataKeepDays = (int)v; _ = SweepThenRefreshAsync(StorageKind.WeatherData); });

			var lMax = StorageLimits.Resolve(StorageLimits.LogsMax, (long)_settings.LogsMaxMb * StorageLimits.MB, days: false);
			var lKeep = StorageLimits.Resolve(StorageLimits.LogsKeep, _settings.LogsKeepDays, days: true);
			Logs = new StorageItemViewModel(StorageKind.Logs, "Logs", "",
				storage.FolderOf(StorageKind.Logs), () => ClearKindAsync(StorageKind.Logs), confirmClear: false,
				lMax.List, lMax.Pick, v => { _settings.LogsMaxMb = (int)(v / StorageLimits.MB); _ = SweepThenRefreshAsync(StorageKind.Logs); },
				lKeep.List, lKeep.Pick, v => { _settings.LogsKeepDays = (int)v; _ = SweepThenRefreshAsync(StorageKind.Logs); });

			UsageHistory = new StorageItemViewModel(StorageKind.UsageHistory, "Your usage history", "— Atlas “Your use”",
				storage.FolderOf(StorageKind.UsageHistory), ClearUsageAsync, confirmClear: true,
				null, null, null, null, null, null);

			Items = new[] { RadarVolumes, RadarDiagnostics, WeatherData, Logs, UsageHistory };
		}

		public StorageItemViewModel RadarVolumes { get; }
		public StorageItemViewModel RadarDiagnostics { get; }
		public StorageItemViewModel WeatherData { get; }
		public StorageItemViewModel Logs { get; }
		public StorageItemViewModel UsageHistory { get; }

		/// <summary>The rows in display order.</summary>
		public IReadOnlyList<StorageItemViewModel> Items { get; }

		/// <summary>Anvil's root folder — the page's "Open Anvil's folder".</summary>
		public string RootFolder => StorageService.AnvilRoot;

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

		private Task<long> MeasureOneAsync(StorageKind kind) =>
			kind == StorageKind.RadarVolumes ? _radar.GetCacheSizeBytesAsync() : _storage.MeasureAsync(kind);

		private async Task SweepThenRefreshAsync(StorageKind kind)
		{
			var (max, keep) = kind == StorageKind.WeatherData
				? ((long)_settings.WeatherDataMaxMb * StorageLimits.MB, _settings.WeatherDataKeepDays)
				: ((long)_settings.LogsMaxMb * StorageLimits.MB, _settings.LogsKeepDays);
			try { await _storage.SweepAsync(kind, max, TimeSpan.FromDays(keep)); }
			catch { /* best effort — the readout below still tells the truth */ }
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
