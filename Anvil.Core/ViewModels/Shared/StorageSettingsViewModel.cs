using System.Collections.Generic;
using System.Threading.Tasks;
using Anvil.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Anvil.ViewModels
{
	/// <summary>
	/// The App Settings "Storage" section — the settings service's first real consumer. Surfaces the
	/// on-disk radar volume cache: a live size readout, a "Clear now" action, and a PERSISTED size cap
	/// (<see cref="AppSettings.RadarCacheMaxGb"/>) that also drives <c>Level2RadarService</c>'s startup
	/// sweep. Writing the cap goes straight to <see cref="ISettingsService.Settings"/>, which auto-saves.
	///
	/// A subsystem VM on <see cref="MapViewModel"/> (sibling of Radar/Watches/etc.); the App Settings card
	/// binds <c>ViewModel.Storage.*</c>. ⚠️ Its async methods drive WinUI-bound properties, so they must be
	/// awaited on the UI thread (no <c>ConfigureAwait(false)</c>) — the service does its own off-threading.
	/// </summary>
	public sealed class StorageSettingsViewModel : ObservableObject
	{
		private readonly ILevel2RadarService _radarService;
		private readonly ISettingsService _settings;

		public StorageSettingsViewModel(ILevel2RadarService radarService, ISettingsService settings)
		{
			_radarService = radarService;
			_settings = settings;
		}

		/// <summary>The cap choices offered in the combo (GB). The persisted value should be one of these.</summary>
		public IReadOnlyList<int> CacheLimitOptions { get; } = new[] { 2, 5, 10, 20, 50 };

		/// <summary>The persisted cache size cap (GB). Two-way bound to the combo; the setter writes through
		/// to <see cref="AppSettings.RadarCacheMaxGb"/>, which the settings service auto-persists. Takes
		/// effect on the next launch's sweep (the cap is enforced at startup).</summary>
		public int CacheLimitGb
		{
			get => _settings.Settings.RadarCacheMaxGb;
			set
			{
				if (_settings.Settings.RadarCacheMaxGb == value) return;
				_settings.Settings.RadarCacheMaxGb = value;
				OnPropertyChanged();
			}
		}

		private string _cacheSizeText = "…";
		/// <summary>Human-readable current cache size (e.g. "4.8 GB"), refreshed when the card opens and
		/// after a clear.</summary>
		public string CacheSizeText
		{
			get => _cacheSizeText;
			private set => SetProperty(ref _cacheSizeText, value);
		}

		private bool _isClearing;
		/// <summary>True while a clear is in flight — the "Clear now" button binds <see cref="CanClear"/> to
		/// disable itself so a second clear can't overlap.</summary>
		public bool IsClearing
		{
			get => _isClearing;
			private set { if (SetProperty(ref _isClearing, value)) OnPropertyChanged(nameof(CanClear)); }
		}

		public bool CanClear => !_isClearing;

		/// <summary>Recomputes <see cref="CacheSizeText"/> from the on-disk cache (off-thread in the service).</summary>
		public async Task RefreshCacheSizeAsync()
		{
			CacheSizeText = "Calculating…";
			try
			{
				long bytes = await _radarService.GetCacheSizeBytesAsync();
				CacheSizeText = FormatBytes(bytes);
			}
			catch
			{
				CacheSizeText = "—";
			}
		}

		/// <summary>Deletes the whole volume cache, then refreshes the readout. No-op if already clearing.</summary>
		public async Task ClearCacheAsync()
		{
			if (_isClearing) return;
			IsClearing = true;
			try
			{
				await _radarService.ClearCacheAsync();
				await RefreshCacheSizeAsync();
			}
			finally
			{
				IsClearing = false;
			}
		}

		private static string FormatBytes(long bytes)
		{
			if (bytes <= 0) return "0 MB";
			double mb = bytes / 1024.0 / 1024.0;
			return mb >= 1024 ? $"{mb / 1024.0:0.0} GB" : $"{mb:0} MB";
		}
	}
}
