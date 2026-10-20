using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Anvil.Models;
using Anvil.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Anvil.ViewModels
{
	/// <summary>
	/// Getting Anvil a MAP: the map setup window shown before launch (<c>Controls/Basemaps/BasemapSetupWindow</c>) and
	/// Settings → Basemap (<c>Controls/Basemaps/BasemapSettingsTab</c>) — one instance (an app DI singleton) behind both.
	/// Two routes: an ONLINE map (the user's free Protomaps key) or an OFFLINE map (a download Anvil extracts from
	/// Protomaps' daily planet build: the ticked AREAS at Standard or Full detail, named by <see cref="BasemapFileNames"/>).
	/// </summary>
	/// <remarks>
	/// Everything WORKS (2026-10-20): the download (<see cref="IBasemapDownloadService"/>), the online key (<see cref="IMapKeyChecker"/>,
	/// saved inside <see cref="AppSettings.OnlineTilesUrl"/>), an existing file (<see cref="BasemapFileCheck"/>), the file list
	/// (Use, Delete — a refused delete finishes next launch —, Add). Every in-use change is written to SETTINGS and drawn live
	/// by whoever follows them (MapViewModel for online, MainWindow for the file) — this VM never touches the map. The pickers
	/// and Explorer are the view's (they need its window).
	/// </remarks>
	public sealed class BasemapSetupViewModel : ObservableObject
	{
		private readonly ISettingsService _settings;
		private readonly IBasemapDownloadService? _downloads;
		private readonly IMapKeyChecker? _keys;
		private readonly List<(double Lat, double Lon)> _nexrad;

		/// <summary>Where downloads go unless the user picks another folder.</summary>
		public static string DefaultSaveFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Anvil Maps");

		/// <param name="downloads">The offline map download; null (tests) = Download stays greyed.</param>
		/// <param name="keys">The map key test; null (tests) = Test / Use online map say the service can't be reached.</param>
		public BasemapSetupViewModel(ISettingsService settings, IRadarSiteProvider radarSites, IBasemapDownloadService? downloads = null,
			IMapKeyChecker? keys = null)
		{
			_settings = settings;
			_downloads = downloads;
			_keys = keys;
			_saveFolder = string.IsNullOrWhiteSpace(settings.Settings.BasemapSaveFolder) ? DefaultSaveFolder : settings.Settings.BasemapSaveFolder;
			_mapKey = ProtomapsKeys.KeyOf(settings.Settings.OnlineTilesUrl);

			// The Area checklist. Radar counts = NEXRAD only (the user's call): operational, not retired, inside the area.
			var nexrad = radarSites.GetSites().Where(s => s.Class == RadarSiteClass.Operational && s.RetiredOn is null).ToList();
			_nexrad = nexrad.Select(s => (s.Latitude, s.Longitude)).ToList();
			Areas = BasemapAreas.All
				.Select(a => new BasemapAreaOption(a, nexrad.Count(s => a.Contains(s.Latitude, s.Longitude)),
					isChecked: a == BasemapAreas.Lower48, OnAreasChanged))
				.ToList();

			_settings.Settings.PropertyChanged += OnSettingsChanged;
			Refresh();
		}

		// ── Launch ──────────────────────────────────────────────────────────────────────────────────────────

		/// <summary>Whether Anvil can draw a map now: an offline file in the folder, or a usable online source switched on.</summary>
		public bool HasUsableMap =>
			_settings.MapDataFilePresent() ||
			(_settings.Settings.UseOnlineTiles && OnlineTileUrls.IsUsable(_settings.Settings.OnlineTilesUrl));

		/// <summary>App.OnLaunched's question: show the map setup window before Anvil? Every launch while there's no map,
		/// until the user ticks "Stop showing this at launch" — or once, on purpose, for a dev "new user" launch.</summary>
		public bool ShouldShowAtLaunch => (_settings.Settings.ShowBasemapSetupAtLaunch && !HasUsableMap) || _settings.Settings.DevPretendNoMap;

		/// <summary>DEV (Settings → Dev → Map setup window): the next launch shows map setup as a new user would.</summary>
		public bool DevPretendNoMap
		{
			get => _settings.Settings.DevPretendNoMap;
			set => _settings.Settings.DevPretendNoMap = value;
		}

		/// <summary>Settings → Basemap → At launch. Persisted (<see cref="AppSettings.ShowBasemapSetupAtLaunch"/>).</summary>
		public bool ShowAtLaunch
		{
			get => _settings.Settings.ShowBasemapSetupAtLaunch;
			set => _settings.Settings.ShowBasemapSetupAtLaunch = value; // raised back through OnSettingsChanged
		}

		/// <summary>The setup window's "Stop showing this at launch" box — the same setting, inverted.</summary>
		public bool StopShowingAtLaunch
		{
			get => !ShowAtLaunch;
			set => ShowAtLaunch = !value;
		}

		// ── The window's step ───────────────────────────────────────────────────────────────────────────────

		private BasemapSetupStep _step;
		public BasemapSetupStep Step
		{
			get => _step;
			set
			{
				if (SetProperty(ref _step, value))
				{
					OnPropertyChanged(nameof(StepIndex));
					OnPropertyChanged(nameof(IsDownloadFailed));
				}
			}
		}

		/// <summary><see cref="Step"/> as an int, for x:Bind (the window's step bodies + the Dev tab's preview picker).</summary>
		public int StepIndex
		{
			get => (int)_step;
			set
			{
				if (Enum.IsDefined(typeof(BasemapSetupStep), value)) Step = (BasemapSetupStep)value;
			}
		}

		public void ShowExistingFile() => Step = BasemapSetupStep.ExistingFile;
		public void BackToChoice() => Step = BasemapSetupStep.Choose;

		// ── The choice (two cards) ──────────────────────────────────────────────────────────────────────────

		private bool _isOnlineChosen = true;
		/// <summary>Which card is picked. Online by default — it's the quickest way to a working map.</summary>
		public bool IsOnlineChosen
		{
			get => _isOnlineChosen;
			set
			{
				if (SetProperty(ref _isOnlineChosen, value))
				{
					OnPropertyChanged(nameof(IsOfflineChosen));
					OnPropertyChanged(nameof(PrimaryActionText));
					OnPropertyChanged(nameof(CanRunPrimaryAction));
				}
			}
		}

		public bool IsOfflineChosen
		{
			get => !_isOnlineChosen;
			set => IsOnlineChosen = !value;
		}

		public void ChooseOnline() => IsOnlineChosen = true;
		public void ChooseOffline() => IsOnlineChosen = false;

		/// <summary>The window's main button, named for what it will do.</summary>
		public string PrimaryActionText => _isOnlineChosen ? "Use online map" : "Download map";

		/// <summary>Download needs at least one area ticked and no download already running; online needs a key and no test running.</summary>
		public bool CanRunPrimaryAction => _isOnlineChosen ? !IsCheckingKey : HasAnyArea && !IsDownloading;

		/// <summary>The main button: offline = start the download (the window moves to its Downloading step); online = test
		/// the key, and if it works, switch the map on and go to Ready.</summary>
		public void RunPrimaryAction()
		{
			if (!_isOnlineChosen)
			{
				StartDownload();
				return;
			}
			_ = UseOnlineAsync(showReady: true);
		}

		// ── Online ──────────────────────────────────────────────────────────────────────────────────────────

		/// <summary>Where a free key comes from (the card's "Open protomaps.com" button).</summary>
		public const string KeySignupUrl = "https://protomaps.com/account";

		private string _mapKey;
		/// <summary>The pasted key (starts as the one in the saved online URL). Saved — inside the tile URL — only once it works.</summary>
		public string MapKey
		{
			get => _mapKey;
			set => SetProperty(ref _mapKey, (value ?? "").Trim());
		}

		private bool _isCheckingKey;
		/// <summary>A key test is running (greys Test / Use online map / the window's button).</summary>
		public bool IsCheckingKey
		{
			get => _isCheckingKey;
			private set
			{
				if (SetProperty(ref _isCheckingKey, value))
				{
					OnPropertyChanged(nameof(CanTestKey));
					OnPropertyChanged(nameof(CanRunPrimaryAction));
				}
			}
		}

		public bool CanTestKey => !_isCheckingKey;

		private string _keyMessage = "Free for personal use.";
		/// <summary>The line under the key box: the free-use note, then Test's answer. A failure names the next step —
		/// check the key, or try the offline map.</summary>
		public string KeyMessage
		{
			get => _keyMessage;
			private set => SetProperty(ref _keyMessage, value);
		}

		/// <summary>Test: fetch one tile with the key and say what happened (a failure points at the offline map too).</summary>
		public void TestKey() => _ = CheckKeyAsync();

		/// <summary>Settings → Basemap's "Use online map": test the key; if it works, save it and draw the online map now.</summary>
		public void UseOnlineMap() => _ = UseOnlineAsync(showReady: false);

		private async Task<bool> CheckKeyAsync()
		{
			if (_isCheckingKey) return false;
			if (_mapKey.Length == 0)
			{
				KeyMessage = "Paste your map key first.";
				return false;
			}
			IsCheckingKey = true;
			KeyMessage = "Checking your key…";
			try
			{
				var result = _keys is null ? MapKeyCheck.Unreachable : await _keys.CheckAsync(_mapKey, CancellationToken.None);
				KeyMessage = KeyWords(result);
				return result == MapKeyCheck.Works;
			}
			finally
			{
				IsCheckingKey = false;
			}
		}

		internal static string KeyWords(MapKeyCheck result) => result switch
		{
			MapKeyCheck.Works => "That key works.",
			MapKeyCheck.Rejected => "That key didn't work. Check you copied all of it — or use the offline map instead.",
			_ => "Couldn't reach the map service. Check your internet connection — or use the offline map instead.",
		};

		private async Task UseOnlineAsync(bool showReady)
		{
			if (!await CheckKeyAsync()) return;
			_settings.Settings.OnlineTilesUrl = ProtomapsKeys.UrlFor(_mapKey);
			_settings.Settings.UseOnlineTiles = true; // MapViewModel follows the setting and re-points the map live
			KeyMessage = "Anvil is using the online map.";
			if (showReady)
			{
				SetReady("Online map · Protomaps", "Streams as you move around the map. Needs internet.");
			}
		}

		// ── Offline ─────────────────────────────────────────────────────────────────────────────────────────

		/// <summary>The detail picker's segments (index = <see cref="DetailIndex"/>).</summary>
		public IReadOnlyList<string> DetailLabels { get; } = new[] { "Standard", "Full" };

		private int _detailIndex;
		/// <summary>0 = Standard, 1 = Full.</summary>
		public int DetailIndex
		{
			get => _detailIndex;
			set
			{
				var v = Math.Clamp(value, 0, 1);
				if (SetProperty(ref _detailIndex, v)) RaiseChoice();
			}
		}

		private bool IsFullDetail => _detailIndex == 1;

		/// <summary>The Area checklist, in <see cref="BasemapAreas.All"/> order. Lower 48 ticked to start.</summary>
		public IReadOnlyList<BasemapAreaOption> Areas { get; }

		private List<BasemapArea> CheckedAreas => Areas.Where(a => a.IsChecked).Select(a => a.Area).ToList();

		/// <summary>At least one area ticked — Download is greyed, and "Tick at least one area." shows, without one.</summary>
		public bool HasAnyArea => Areas.Any(a => a.IsChecked);

		/// <summary>The download as one line, for the downloading / ready / failed screens: "Standard · Lower 48 states".</summary>
		public string ChosenMapText => HasAnyArea
			? new BasemapFileOptions(IsFullDetail, CheckedAreas, 1).Description
			: DetailLabels[_detailIndex];

		/// <summary>Under the detail picker: what it shows · which areas · about how big. Empty with nothing ticked.</summary>
		public string SizeSummaryText
		{
			get
			{
				var areas = CheckedAreas;
				if (areas.Count == 0) return "";
				var megabytes = areas.Sum(a => IsFullDetail ? a.FullMegabytes : a.StandardMegabytes);
				var which = areas.Count == 1 ? areas[0].Name : $"{areas.Count} areas";
				return $"{(IsFullDetail ? "Every street and building" : "Streets at city zoom")} · {which} · about {SizeWords(megabytes)}";
			}
		}

		/// <summary>The file a download would make now (Settings → Basemap's File line); "—" with nothing ticked. An unfinished
		/// download of the same options is RESUMED under its own name, so that name shows.</summary>
		public string PlannedFileName => !HasAnyArea ? "—"
			: UnfinishedFileName() ?? BasemapFileNames.NextFileName(IsFullDetail, CheckedAreas, BasemapDownloadService.TakenFileNames(_saveFolder));

		/// <summary>Said under the File line: an unfinished download carries on; the same options already downloaded are
		/// ALWAYS kept beside (the user's call).</summary>
		public string DuplicateNote =>
			!HasAnyArea ? ""
			: UnfinishedFileName() is not null ? "An unfinished download of this map picks up where it left off."
			: BasemapFileNames.TryParse(PlannedFileName)?.Copy > 1 ? "You already have this map. This one is kept beside it."
			: "";

		private string? UnfinishedFileName() => _downloads?.UnfinishedFileName(_saveFolder, CheckedAreas, IsFullDetail);

		private void OnAreasChanged()
		{
			OnPropertyChanged(nameof(HasAnyArea));
			OnPropertyChanged(nameof(CanRunPrimaryAction));
			OnPropertyChanged(nameof(CanStartDownload));
			RaiseChoice();
		}

		// Everything worked out from the ticked areas + the detail.
		private void RaiseChoice()
		{
			OnPropertyChanged(nameof(ChosenMapText));
			OnPropertyChanged(nameof(SizeSummaryText));
			OnPropertyChanged(nameof(PlannedFileName));
			OnPropertyChanged(nameof(DuplicateNote));
		}

		// "450 MB" under a gigabyte; "4.4 GB" under ten; "19 GB" past that. Decimal units, as the measurements were taken.
		internal static string SizeWords(double megabytes) =>
			megabytes < 1000 ? $"{Math.Max(1, Math.Round(megabytes)).ToString("0", CultureInfo.CurrentCulture)} MB"
			: megabytes < 10000 ? $"{(megabytes / 1000).ToString("0.0", CultureInfo.CurrentCulture)} GB"
			: $"{Math.Round(megabytes / 1000).ToString("0", CultureInfo.CurrentCulture)} GB";

		private string _saveFolder;
		/// <summary>Where a download goes — persisted (<see cref="AppSettings.BasemapSaveFolder"/>); set by Browse… (the view
		/// shows the folder picker — it needs the window). Ignored while a download runs (it's writing into the old one).</summary>
		public string SaveFolder
		{
			get => _saveFolder;
			set
			{
				if (string.IsNullOrWhiteSpace(value) || IsDownloading) return;
				if (SetProperty(ref _saveFolder, value))
				{
					_settings.Settings.BasemapSaveFolder =
						string.Equals(value, DefaultSaveFolder, StringComparison.OrdinalIgnoreCase) ? "" : value;
					OnPropertyChanged(nameof(FreeSpaceText));
					Refresh(); // the folder's maps, and so the next free file name
				}
			}
		}

		// The downloaded maps' names in the save folder (the naming rule's "already taken" set).
		private List<string> ExistingMapFileNames()
		{
			try
			{
				return Directory.Exists(_saveFolder)
					? Directory.EnumerateFiles(_saveFolder, BasemapFileNames.Prefix + "*" + BasemapFileNames.Extension)
						.Select(Path.GetFileName).OfType<string>().ToList()
					: new List<string>();
			}
			catch (IOException) { return new List<string>(); }
			catch (UnauthorizedAccessException) { return new List<string>(); }
		}

		/// <summary>"212 GB free" on the save folder's drive (the folder needn't exist yet).</summary>
		public string FreeSpaceText
		{
			get
			{
				try
				{
					var root = Path.GetPathRoot(_saveFolder);
					if (string.IsNullOrEmpty(root)) return "";
					return $"{GigabytesText(new DriveInfo(root).AvailableFreeSpace)} free";
				}
				catch (Exception)
				{
					return ""; // a drive that's gone or unreadable just shows nothing
				}
			}
		}

		// ── The download ────────────────────────────────────────────────────────────────────────────────────
		// ONE at a time, started from either place (the setup window's Download map, Settings → Basemap's Download). The
		// window follows it through its steps (Downloading → Ready | Failed) with a ROW PER PHASE (DownloadSteps); the tab
		// shows the same on one line + the download's own bar.

		private CancellationTokenSource? _downloadCancel;
		private List<BasemapArea> _downloadAreas = new();
		private bool _downloadFull;
		private BasemapDownloadProgress _lastProgress;
		// Speed = bytes over the last few seconds (a rolling window, so it follows the line, not the whole run's average).
		private readonly Queue<(DateTime At, ulong Bytes)> _speedSamples = new();
		private DateTime _downloadStartUtc;
		private const double SpeedWindowSeconds = 5;

		/// <summary>The download's steps, in <see cref="BasemapDownloadPhase"/> order — one row each in the setup window.</summary>
		public BasemapDownloadStep FindStep { get; } = new("Find the newest map", counts: false);
		public BasemapDownloadStep IndexStep { get; } = new("Read the map's index", counts: true);
		public BasemapDownloadStep PlanStep { get; } = new("Plan your file", counts: true);
		public BasemapDownloadStep DownloadStep { get; } = new("Download", counts: true);
		public BasemapDownloadStep FinishStep { get; } = new("Finish", counts: false);

		private BasemapDownloadStep[] DownloadSteps => new[] { FindStep, IndexStep, PlanStep, DownloadStep, FinishStep };

		private bool _isDownloading;
		/// <summary>A download is running (greys both Download buttons; the tab shows its progress line).</summary>
		public bool IsDownloading
		{
			get => _isDownloading;
			private set
			{
				if (SetProperty(ref _isDownloading, value))
				{
					OnPropertyChanged(nameof(CanRunPrimaryAction));
					OnPropertyChanged(nameof(CanStartDownload));
				}
			}
		}

		/// <summary>The last download stopped (the window's Failed step; Settings → Basemap shows it with Retry).</summary>
		public bool IsDownloadFailed => _step == BasemapSetupStep.Failed;

		/// <summary>Settings → Basemap's Download button.</summary>
		public bool CanStartDownload => HasAnyArea && !IsDownloading && _downloads is not null;

		/// <summary>Download the ticked areas at the picked detail — or carry on an unfinished one of the same options.</summary>
		public void StartDownload()
		{
			if (!CanStartDownload) return;
			_downloadAreas = CheckedAreas;
			_downloadFull = IsFullDetail;
			_ = RunDownloadAsync();
		}

		/// <summary>Retry: the SAME map again, picking up what arrived (the window's Failed step).</summary>
		public void RetryDownload()
		{
			if (IsDownloading || _downloads is null || _downloadAreas.Count == 0) return;
			_ = RunDownloadAsync();
		}

		/// <summary>"Cancel and go back": stop, keep what arrived (the next Download of these options picks it up).</summary>
		public void CancelDownload()
		{
			_downloadCancel?.Cancel();
			Step = BasemapSetupStep.Choose;
		}

		private async Task RunDownloadAsync()
		{
			IsDownloading = true;
			Step = BasemapSetupStep.Downloading;
			DownloadMapText = new BasemapFileOptions(_downloadFull, _downloadAreas, 1).Description;
			OnPropertyChanged(nameof(DownloadMapText));
			_downloadCancel = new CancellationTokenSource();
			_lastProgress = default;
			_lastIndexTotal = 0;
			_speedSamples.Clear();
			foreach (var s in DownloadSteps) s.State = BasemapDownloadStepState.Waiting;
			ShowProgress(new BasemapDownloadProgress(BasemapDownloadPhase.FindingBuild, 0, 0));
			// With no map yet (a new user, or a dev "new user" launch) the finished map becomes Anvil's; otherwise Anvil
			// keeps its current one until the user picks Use (the tab's promise).
			var takeOver = !HasUsableMap || DevPretendNoMap;
			try
			{
				// Progress<T> is created HERE, on the UI thread, so its reports land back on it.
				var path = await _downloads!.DownloadAsync(_saveFolder, _downloadAreas, _downloadFull,
					new Progress<BasemapDownloadProgress>(ShowProgress), _downloadCancel.Token);
				if (takeOver)
				{
					_settings.Settings.UseOnlineTiles = false;
					_settings.Settings.BasemapFilePath = path;
				}
				DownloadedPath = path;
				OnPropertyChanged(nameof(DownloadedPath));
				FinishStep.State = BasemapDownloadStepState.Done;
				FinishStep.Detail = "Done";
				SetReady($"Offline map · {DownloadMapText}", path);
			}
			catch (OperationCanceledException) when (_downloadCancel.IsCancellationRequested)
			{
				// Cancel and go back: the window is already on Choose; what arrived is kept.
			}
			catch (Exception ex)
			{
				FailureText = FailureWords(ex, _lastProgress);
				Step = BasemapSetupStep.Failed;
			}
			finally
			{
				_downloadCancel.Dispose();
				_downloadCancel = null;
				IsDownloading = false;
				Refresh(); // the new file in the list, the next free name
			}
		}

		/// <summary>One report → the step rows: every step before the report's phase is Done, its own is Running with its bar
		/// and words, the rest Waiting. Reports run strictly forward, so a finished step never comes back.</summary>
		private void ShowProgress(BasemapDownloadProgress p)
		{
			var phase = (int)p.Phase;
			var steps = DownloadSteps;
			for (var i = 0; i < steps.Length; i++)
			{
				var state = i < phase ? BasemapDownloadStepState.Done : i == phase ? BasemapDownloadStepState.Running : BasemapDownloadStepState.Waiting;
				if (steps[i].State != state) steps[i].State = state;
			}

			if (p.Phase > BasemapDownloadPhase.FindingBuild)
			{
				FindStep.Detail = p.BuildDay is { } day ? $"{day.ToString("d MMM yyyy", CultureInfo.CurrentCulture)} build" : "Found";
			}
			switch (p.Phase)
			{
				case BasemapDownloadPhase.FindingBuild:
					FindStep.Detail = "Asking Protomaps…";
					break;
				case BasemapDownloadPhase.ReadingIndex:
					// The planner reports one at a time, in order (BasemapExtractPlanner's tally lock).
					if (p.Total > 0) _lastIndexTotal = p.Total;
					IndexStep.Percent = p.Total == 0 ? 0 : 100.0 * p.Done / p.Total;
					IndexStep.Detail = p.Total == 0 ? "Starting…" : $"{p.Done:N0} of {p.Total:N0} sections";
					break;
				case BasemapDownloadPhase.Planning:
					IndexStep.Detail = $"{_lastIndexTotal:N0} sections";
					PlanStep.Percent = 100.0 * p.Done / Math.Max(1, p.Total);
					PlanStep.Detail = p.Done switch
					{
						0 => $"Sorting {TileWords(p.Tiles)} tiles",
						1 => "Building the index",
						_ => "Laying out the download",
					};
					break;
				case BasemapDownloadPhase.Downloading:
					IndexStep.Detail = $"{_lastIndexTotal:N0} sections";
					PlanStep.Detail = $"{TileWords(p.Tiles)} tiles · {AmountWords(p.Total)}";
					DownloadStep.Percent = p.Total == 0 ? 0 : 100.0 * p.Done / p.Total;
					DownloadStep.Detail = string.Join(" · ", new[] { $"{AmountWords(p.Done)} of {AmountWords(p.Total)}", SpeedWords(p.Done), TimeLeftWords(p) }
						.Where(s => s.Length > 0));
					break;
				case BasemapDownloadPhase.Finishing:
					DownloadStep.Detail = AmountWords(p.Total);
					FinishStep.Detail = "Checking and saving";
					break;
			}
			_lastProgress = p;

			DownloadPercent = p.Phase == BasemapDownloadPhase.Downloading ? DownloadStep.Percent : p.Phase > BasemapDownloadPhase.Downloading ? 100 : 0;
			OnPropertyChanged(nameof(DownloadPercent));
			OnPropertyChanged(nameof(DownloadLineText));
		}

		private ulong _lastIndexTotal;

		/// <summary>MB/s over the last few seconds of the download ("94 MB/s"); empty until there's a second of it.</summary>
		private string SpeedWords(ulong bytes)
		{
			var now = DateTime.UtcNow;
			if (_speedSamples.Count == 0) _downloadStartUtc = now;
			_speedSamples.Enqueue((now, bytes));
			while (_speedSamples.Count > 2 && (now - _speedSamples.Peek().At).TotalSeconds > SpeedWindowSeconds) _speedSamples.Dequeue();
			var (at, first) = _speedSamples.Peek();
			var seconds = (now - at).TotalSeconds;
			return seconds < 1 || bytes <= first ? "" : $"{(bytes - first) / 1e6 / seconds:0} MB/s";
		}

		/// <summary>Time left at the recent speed; empty for the first few seconds (too early to say).</summary>
		private string TimeLeftWords(BasemapDownloadProgress p)
		{
			if ((DateTime.UtcNow - _downloadStartUtc).TotalSeconds < 3 || _speedSamples.Count < 2) return "";
			var (at, first) = _speedSamples.Peek();
			var seconds = (DateTime.UtcNow - at).TotalSeconds;
			if (seconds <= 0 || p.Done <= first) return "";
			var left = (p.Total - p.Done) / ((p.Done - first) / seconds);
			var minutes = Math.Ceiling(left / 60);
			return left < 60 ? $"about {Math.Max(5, Math.Ceiling(left / 5) * 5):0} seconds left"
				: left < 3600 ? (minutes == 1 ? "about 1 minute left" : $"about {minutes:0} minutes left")
				: $"about {left / 3600:0.0} hours left";
		}

		/// <summary>A download's amounts, fine enough to see move: "450 MB", "3.10 GB".</summary>
		internal static string AmountWords(ulong bytes) =>
			bytes < 1_000_000_000 ? $"{Math.Round(bytes / 1e6).ToString("0", CultureInfo.CurrentCulture)} MB"
			: $"{(bytes / 1e9).ToString("0.00", CultureInfo.CurrentCulture)} GB";

		/// <summary>A tile count in words: "850,000", "3.06 M".</summary>
		private static string TileWords(ulong tiles) =>
			tiles < 1_000_000 ? tiles.ToString("N0", CultureInfo.CurrentCulture) : $"{(tiles / 1e6).ToString("0.00", CultureInfo.CurrentCulture)} M";

		/// <summary>What went wrong, in plain words, with what to do and what's kept.</summary>
		internal static string FailureWords(Exception ex, BasemapDownloadProgress at) => ex switch
		{
			BasemapNotEnoughSpaceException s =>
				$"There isn't room on this drive: the map needs {BytesWords(s.NeededBytes)} and {BytesWords(s.FreeBytes)} is free. Free some space, or pick a smaller map.",
			_ when at.Phase >= BasemapDownloadPhase.Downloading && at.Total > 0 =>
				$"Your connection dropped at {BytesWords(at.Done)} of {BytesWords(at.Total)}. What's downloaded is kept.",
			_ => "Couldn't reach the map service. Check your internet connection, then retry.",
		};

		/// <summary>Bytes as a reader sees them: "450 MB", "4.4 GB" (decimal, like the size estimates).</summary>
		internal static string BytesWords(ulong bytes) => SizeWords(bytes / 1e6);

		/// <summary>The map being downloaded, for the Downloading / Ready / Failed screens ("Standard · Lower 48 states").</summary>
		public string DownloadMapText { get; private set; } = "";

		/// <summary>The finished file (the Ready screen).</summary>
		public string DownloadedPath { get; private set; } = "";

		/// <summary>The DOWNLOAD step's bar alone (Settings → Basemap's one bar: empty until tile data moves — the user's call).</summary>
		public double DownloadPercent { get; private set; }

		/// <summary>Settings → Basemap's one-line readout while a download runs: "Step 3 of 5 · Plan your file · Building the index".</summary>
		public string DownloadLineText
		{
			get
			{
				var steps = DownloadSteps;
				var i = Array.FindIndex(steps, s => s.State == BasemapDownloadStepState.Running);
				if (i < 0) return "";
				var s = steps[i];
				return string.IsNullOrEmpty(s.Detail) ? $"Step {i + 1} of {steps.Length} · {s.Name}" : $"Step {i + 1} of {steps.Length} · {s.Name} · {s.Detail}";
			}
		}

		private string _failureText = "";
		public string FailureText
		{
			get => _failureText;
			private set => SetProperty(ref _failureText, value);
		}

		// ── An existing file ────────────────────────────────────────────────────────────────────────────────

		private string _existingFilePath = "";
		/// <summary>The file picked on the "Use a map file you already have" screen (the view's Browse… shows the picker).
		/// Setting it runs the three checks.</summary>
		public string ExistingFilePath
		{
			get => _existingFilePath;
			set
			{
				if (SetProperty(ref _existingFilePath, value ?? "")) _ = CheckExistingFileAsync();
			}
		}

		private BasemapFileCheck? _existingCheck;
		/// <summary>The checks' three lines (readable · radars covered · detail, size, date); "" until a file is picked.</summary>
		public string ExistingReadLine => _existingCheck?.ReadLine ?? "";
		public string ExistingCoverLine => _existingCheck?.CoverLine ?? "";
		public string ExistingDetailLine => _existingCheck?.DetailLine ?? "";

		/// <summary>Use this map: a file is picked and passed the checks.</summary>
		public bool CanUseExistingFile => _existingCheck?.CanUse == true;

		private async Task CheckExistingFileAsync()
		{
			var path = _existingFilePath;
			var check = path.Length == 0 ? null : await Task.Run(() => BasemapFileCheck.Inspect(path, _nexrad));
			if (path != _existingFilePath) return; // a later pick superseded this one
			_existingCheck = check;
			OnPropertyChanged(nameof(ExistingReadLine));
			OnPropertyChanged(nameof(ExistingCoverLine));
			OnPropertyChanged(nameof(ExistingDetailLine));
			OnPropertyChanged(nameof(CanUseExistingFile));
		}

		/// <summary>Use this map: it becomes the map Anvil draws (where it sits — never copied), listed under Your map files.</summary>
		public void UseExistingFile()
		{
			if (!CanUseExistingFile) return;
			RememberAddedFile(_existingFilePath);
			_settings.Settings.UseOnlineTiles = false;
			_settings.Settings.BasemapFilePath = _existingFilePath;
			SetReady($"Offline map · {TitleOf(_existingFilePath)}", _existingFilePath);
		}

		// ── Ready ──

		/// <summary>The Ready screen's two lines: which map, and where it comes from (the file, or "streams as you move").</summary>
		public string ReadyMapText { get; private set; } = "";
		public string ReadyDetailText { get; private set; } = "";

		private void SetReady(string map, string detail)
		{
			ReadyMapText = map;
			ReadyDetailText = detail;
			OnPropertyChanged(nameof(ReadyMapText));
			OnPropertyChanged(nameof(ReadyDetailText));
			Step = BasemapSetupStep.Ready;
		}

		// ── Settings → Basemap: what's in use, the files ─────────────────────────────────────────────────────

		private string _inUseText = "";
		/// <summary>The tab's top line: which map Anvil draws now.</summary>
		public string InUseText
		{
			get => _inUseText;
			private set => SetProperty(ref _inUseText, value);
		}

		/// <summary>The map files Anvil knows: <c>usa_full.pmtiles</c> where the older rule finds it, the in-use file, and
		/// every downloaded map in the save folder — each once.</summary>
		public ObservableCollection<BasemapFileRow> Files { get; } = new();

		/// <summary>Empty list → the "No map files yet" line.</summary>
		public bool HasNoFiles => Files.Count == 0;

		// The offline file Anvil draws now (settings' in-use rule), or null when online is on or there's none.
		private string? InUsePath()
		{
			if (_settings.Settings.UseOnlineTiles && OnlineTileUrls.IsUsable(_settings.Settings.OnlineTilesUrl)) return null;
			return _settings.MapDataFilePresent() ? Path.GetFullPath(Path.Combine(_settings.MapDataFolder, _settings.MapDataFileName)) : null;
		}

		/// <summary>A map file's title: a downloaded map's options in words, else "Offline map".</summary>
		private static string TitleOf(string path) => BasemapFileNames.TryParse(path)?.Description ?? "Offline map";

		/// <summary>Re-read the folder and the source (the tab calls this when it shows).</summary>
		public void Refresh()
		{
			Files.Clear();
			var inUse = InUsePath();
			var paths = new List<string>();
			void Consider(string path)
			{
				try
				{
					var full = Path.GetFullPath(path);
					if (File.Exists(full) && !paths.Contains(full, StringComparer.OrdinalIgnoreCase)) paths.Add(full);
				}
				catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { }
			}
			Consider(Path.Combine(_settings.LegacyMapDataFolder, SettingsService.LegacyMapDataFileName));
			if (inUse is not null) Consider(inUse);
			foreach (var name in ExistingMapFileNames().Where(n => BasemapFileNames.TryParse(n) is not null).OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
			{
				Consider(Path.Combine(_saveFolder, name));
			}
			foreach (var added in _settings.Settings.BasemapAddedFiles) Consider(added);
			// A delete Windows refused (the map still held it open) is gone as far as the user is concerned.
			paths.RemoveAll(p => _settings.Settings.BasemapPendingDeletes.Contains(p, StringComparer.OrdinalIgnoreCase));

			foreach (var path in paths)
			{
				try
				{
					var info = new FileInfo(path);
					var details = $"{GigabytesText(info.Length)} · {info.LastWriteTime.ToString("d MMM yyyy", CultureInfo.CurrentCulture)} · {info.Name}";
					Files.Add(new BasemapFileRow(path, TitleOf(path), details,
						isInUse: inUse is not null && string.Equals(path, inUse, StringComparison.OrdinalIgnoreCase)));
				}
				catch (IOException) { }
				catch (UnauthorizedAccessException) { }
			}

			InUseText = _settings.Settings.UseOnlineTiles && OnlineTileUrls.IsUsable(_settings.Settings.OnlineTilesUrl) ? "Online map · Protomaps"
				: inUse is not null ? $"Offline map · {TitleOf(inUse)}"
				: "No map. Radar and overlays draw over a blank background.";

			OnPropertyChanged(nameof(HasNoFiles));
			OnPropertyChanged(nameof(HasUsableMap));
			OnPropertyChanged(nameof(ShouldShowAtLaunch));
			OnPropertyChanged(nameof(FreeSpaceText));
			RaiseChoice(); // the next free file name depends on what's in the folder
		}

		/// <summary>App calls this as Anvil opens: a dev "new user" launch ends here.</summary>
		public void HandOverToAnvil() => _settings.Settings.DevPretendNoMap = false;

		/// <summary>Use: make <paramref name="row"/> the map Anvil draws — now (MainWindow re-points the map). Turns the
		/// online map off: a file picked here is an offline choice.</summary>
		public void UseFile(BasemapFileRow row)
		{
			FilesMessage = "";
			_settings.Settings.UseOnlineTiles = false;
			_settings.Settings.BasemapFilePath = row.Path;
			Refresh();
		}

		/// <summary>Trash pressed: ask first, inline (one row asks at a time).</summary>
		public void AskDelete(BasemapFileRow row)
		{
			foreach (var r in Files) r.IsConfirmingDelete = ReferenceEquals(r, row);
		}

		public void KeepFile(BasemapFileRow row) => row.IsConfirmingDelete = false;

		/// <summary>Delete, after the row's question. The map in use may go (the user's call): Anvil lets go of it first,
		/// so the map turns blank. A file Windows still holds open is deleted the next time Anvil opens instead.</summary>
		public void DeleteFile(BasemapFileRow row)
		{
			row.IsConfirmingDelete = false;
			if (row.IsInUse && string.Equals(_settings.Settings.BasemapFilePath, row.Path, StringComparison.OrdinalIgnoreCase))
			{
				_settings.Settings.BasemapFilePath = ""; // re-points the map away from it (MainWindow) before the delete
			}
			_settings.Settings.BasemapAddedFiles = _settings.Settings.BasemapAddedFiles
				.Where(p => !string.Equals(p, row.Path, StringComparison.OrdinalIgnoreCase)).ToList();
			try
			{
				File.Delete(row.Path);
				FilesMessage = $"Deleted {Path.GetFileName(row.Path)}.";
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				_settings.Settings.BasemapPendingDeletes = _settings.Settings.BasemapPendingDeletes.Append(row.Path).ToList();
				FilesMessage = $"Windows is still using {Path.GetFileName(row.Path)}, so Anvil deletes it the next time it opens.";
			}
			Refresh();
		}

		/// <summary>App, before anything opens a map: finish the deletes Windows refused last session (kept if still refused).</summary>
		public void DeletePendingFiles()
		{
			var left = new List<string>();
			foreach (var path in _settings.Settings.BasemapPendingDeletes)
			{
				try { File.Delete(path); }
				catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { left.Add(path); }
			}
			if (left.Count != _settings.Settings.BasemapPendingDeletes.Count) _settings.Settings.BasemapPendingDeletes = left;
		}

		/// <summary>Add a map file… (the view's picker hands over the path): checked, then listed where it sits — never copied.
		/// It isn't drawn until Use.</summary>
		public async Task AddFileAsync(string path)
		{
			if (string.IsNullOrEmpty(path)) return;
			var check = await Task.Run(() => BasemapFileCheck.Inspect(path, _nexrad));
			if (!check.CanUse)
			{
				FilesMessage = check.ReadLine;
				return;
			}
			RememberAddedFile(path);
			FilesMessage = $"Added {Path.GetFileName(path)} · {check.DetailLine}. {check.CoverLine} Press Use to draw it.";
			Refresh();
		}

		// A file outside the save folder (and not the legacy usa_full) is remembered so the list keeps showing it.
		private void RememberAddedFile(string path)
		{
			var full = Path.GetFullPath(path);
			if (_settings.Settings.BasemapAddedFiles.Contains(full, StringComparer.OrdinalIgnoreCase)) return;
			_settings.Settings.BasemapAddedFiles = _settings.Settings.BasemapAddedFiles.Append(full).ToList();
			_settings.Settings.BasemapPendingDeletes = _settings.Settings.BasemapPendingDeletes
				.Where(p => !string.Equals(p, full, StringComparison.OrdinalIgnoreCase)).ToList();
		}

		private string _filesMessage = "";
		/// <summary>The line under Your map files: what the last Add / Delete did.</summary>
		public string FilesMessage
		{
			get => _filesMessage;
			private set => SetProperty(ref _filesMessage, value);
		}

		// ── plumbing ────────────────────────────────────────────────────────────────────────────────────────

		private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
		{
			switch (e.PropertyName)
			{
				case nameof(AppSettings.ShowBasemapSetupAtLaunch):
					OnPropertyChanged(nameof(ShowAtLaunch));
					OnPropertyChanged(nameof(StopShowingAtLaunch));
					OnPropertyChanged(nameof(ShouldShowAtLaunch));
					break;
				case nameof(AppSettings.UseOnlineTiles):
				case nameof(AppSettings.OnlineTilesUrl):
				case nameof(AppSettings.MapDataFolder):
				case nameof(AppSettings.BasemapFilePath):
					Refresh();
					break;
				case nameof(AppSettings.DevPretendNoMap):
					OnPropertyChanged(nameof(ShouldShowAtLaunch));
					OnPropertyChanged(nameof(DevPretendNoMap));
					break;
			}
		}

		// Explorer's convention (GiB, labelled GB), so a size here matches what the user sees in File Explorer.
		private static string GigabytesText(long bytes) =>
			string.Create(CultureInfo.CurrentCulture, $"{bytes / (1024.0 * 1024 * 1024):0.0} GB");
	}
}
