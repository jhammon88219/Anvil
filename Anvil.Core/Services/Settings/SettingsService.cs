using System;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Anvil.Services
{
	/// <summary>
	/// Default <see cref="ISettingsService"/>. Persists <see cref="AppSettings"/> as JSON under
	/// <c>%LocalAppData%\Anvil\Settings\AppSettings.json</c> with auto-save on change (debounced 500 ms),
	/// load-on-construct, and defaults-on-missing/corrupt.
	///
	/// Ported from the AppBase starter template. It's pure net8.0 (<see cref="System.Text.Json"/> +
	/// <see cref="Environment"/> paths + a plain file), so — unlike the old WinRT <c>LocalSettings</c>-backed
	/// version it replaces — it lives in <c>Anvil.Core</c> rather than <c>Anvil.App</c>: settings are no
	/// longer a platform seam.
	/// </summary>
	public sealed class SettingsService : ISettingsService
	{
		private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

		public string MapDataFileName => "usa_full.pmtiles";

		private readonly ILogger<SettingsService> _logger;
		private readonly string _settingsFilePath;
		private readonly object _lock = new();
		private AppSettings _settings;
		private Timer? _debounceTimer;

		/// <param name="logger">Injected by the container (Serilog-backed).</param>
		/// <param name="settingsDirectory">Override the settings folder — TESTS ONLY. Null (the DI default)
		/// uses <c>%LocalAppData%\Anvil\Settings</c>.</param>
		public SettingsService(ILogger<SettingsService> logger, string? settingsDirectory = null)
		{
			_logger = logger;
			_settings = new AppSettings();
			_settings.PropertyChanged += OnSettingsPropertyChanged;

			var dir = settingsDirectory ?? Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
				"Anvil", "Settings");
			Directory.CreateDirectory(dir);
			_settingsFilePath = Path.Combine(dir, "AppSettings.json");

			LoadSettings();
			_logger.LogInformation("SettingsService initialized. Path: {Path}", _settingsFilePath);
		}

		public AppSettings Settings => _settings;

		public string MapDataFolder
		{
			get
			{
				var saved = _settings.MapDataFolder;
				return string.IsNullOrWhiteSpace(saved) ? ResolveDefaultFolder() : saved;
			}
		}

		public bool MapDataFilePresent(string? folder = null)
		{
			var f = folder ?? MapDataFolder;
			try
			{
				return !string.IsNullOrWhiteSpace(f) && File.Exists(Path.Combine(f, MapDataFileName));
			}
			catch
			{
				return false;
			}
		}

		public Task ResetToDefaultsAsync()
		{
			lock (_lock)
			{
				_settings.PropertyChanged -= OnSettingsPropertyChanged;
				_settings = new AppSettings();
				_settings.PropertyChanged += OnSettingsPropertyChanged;
			}
			_logger.LogInformation("Settings reset to defaults");
			return SaveAsync();
		}

		// ── persistence ──

		private void LoadSettings()
		{
			lock (_lock)
			{
				if (!File.Exists(_settingsFilePath))
				{
					_logger.LogInformation("No settings file found. Using defaults.");
					return;
				}

				try
				{
					var json = File.ReadAllText(_settingsFilePath);
					var loaded = JsonSerializer.Deserialize<AppSettings>(json);
					if (loaded is not null)
					{
						_settings.PropertyChanged -= OnSettingsPropertyChanged;
						_settings = loaded;
						_settings.PropertyChanged += OnSettingsPropertyChanged;
						_logger.LogInformation("Settings loaded from file.");
					}
				}
				catch (JsonException ex) { _logger.LogWarning(ex, "Settings file corrupted. Using defaults."); }
				catch (IOException ex) { _logger.LogWarning(ex, "Could not read settings file. Using defaults."); }
			}
		}

		private async Task SaveAsync()
		{
			try
			{
				string json;
				lock (_lock) { json = JsonSerializer.Serialize(_settings, SerializerOptions); }

				await Task.Run(() =>
				{
					lock (_lock)
					{
						File.WriteAllText(_settingsFilePath, json);
						_logger.LogInformation("Settings saved to file.");
					}
				}).ConfigureAwait(false);
			}
			catch (IOException ex) { _logger.LogError(ex, "Failed to save settings to file."); }
			catch (JsonException ex) { _logger.LogError(ex, "Failed to serialize settings."); }
		}

		// Any setting change schedules a SINGLE debounced write, so a burst of edits (e.g. a slider drag)
		// collapses into one save rather than hammering the disk.
		private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
		{
			_debounceTimer?.Dispose();
			_debounceTimer = new Timer(_ => _ = SaveAsync(), null, 500, Timeout.Infinite);
		}

		// Runtime-resolved default — NEVER a hardcoded user path (which would leak the username into source
		// and only work on one machine). Picks the first candidate folder that actually holds the basemap
		// file (so an OneDrive-redirected Desktop is handled), else falls back to Desktop. Carried over
		// verbatim from the previous LocalSettings-backed service.
		private string ResolveDefaultFolder()
		{
			var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
			var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
			var candidates = new[]
			{
				desktop,
				string.IsNullOrEmpty(profile) ? string.Empty : Path.Combine(profile, "OneDrive", "Desktop"),
				string.IsNullOrEmpty(profile) ? string.Empty : Path.Combine(profile, "Desktop"),
			};

			foreach (var c in candidates)
			{
				try
				{
					if (!string.IsNullOrEmpty(c) && File.Exists(Path.Combine(c, MapDataFileName)))
					{
						return c;
					}
				}
				catch
				{
					// Ignore an inaccessible candidate and try the next.
				}
			}

			return string.IsNullOrEmpty(desktop) ? profile : desktop;
		}
	}
}
