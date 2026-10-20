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
	/// ⚠️ UI FIRST (2026-10-20, the user's call: "get the UI in, then slowly make it work"). What WORKS: moving between
	/// the window's steps, the card choice, the area list + its radar counts + size total, the planned file name, Stop
	/// showing this at launch, the file list, free space, the in-use line, the delete QUESTION, and the DOWNLOAD itself
	/// (<see cref="IBasemapDownloadService"/>: start, progress, cancel, retry/resume). What does NOT yet: every action marked
	/// <c>BASEMAP-SETUP: NOT BUILT</c> — key test, use online, DRAWING a downloaded map, use / delete / show a file, the
	/// folder + file pickers. Grep the sentinel to find them.
	/// </remarks>
	public sealed class BasemapSetupViewModel : ObservableObject
	{
		private readonly ISettingsService _settings;
		private readonly IBasemapDownloadService? _downloads;

		/// <param name="downloads">The offline map download; null (tests) = Download stays greyed.</param>
		public BasemapSetupViewModel(ISettingsService settings, IRadarSiteProvider radarSites, IBasemapDownloadService? downloads = null)
		{
			_settings = settings;
			_downloads = downloads;
			_saveFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Anvil Maps");

			// The Area checklist. Radar counts = NEXRAD only (the user's call): operational, not retired, inside the area.
			var nexrad = radarSites.GetSites().Where(s => s.Class == RadarSiteClass.Operational && s.RetiredOn is null).ToList();
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

		/// <summary>Download needs at least one area ticked and no download already running; the online route is always pressable.</summary>
		public bool CanRunPrimaryAction => _isOnlineChosen || (HasAnyArea && !IsDownloading);

		/// <summary>The main button: offline = start the download (the window moves to its Downloading step).
		/// Online = BASEMAP-SETUP: NOT BUILT (test the key + switch on).</summary>
		public void RunPrimaryAction()
		{
			if (!_isOnlineChosen) StartDownload();
		}

		// ── Online ──────────────────────────────────────────────────────────────────────────────────────────

		/// <summary>Where a free key comes from (the card's "Open protomaps.com" button).</summary>
		public const string KeySignupUrl = "https://protomaps.com/account";

		private string _mapKey = "";
		/// <summary>The pasted key. BASEMAP-SETUP: NOT BUILT — not saved anywhere yet.</summary>
		public string MapKey
		{
			get => _mapKey;
			set => SetProperty(ref _mapKey, value ?? "");
		}

		private string _keyMessage = "Free for personal use.";
		/// <summary>The line under the key box: the free-use note, then Test's answer. A failure names the next step —
		/// check the key, or try the offline map.</summary>
		public string KeyMessage
		{
			get => _keyMessage;
			private set => SetProperty(ref _keyMessage, value);
		}

		/// <summary>BASEMAP-SETUP: NOT BUILT — fetch one tile with the key; Works, or "That key didn't work…" /
		/// "Couldn't reach the map service…", each pointing at the offline map.</summary>
		public void TestKey() { }

		/// <summary>Settings → Basemap's "Use online map". BASEMAP-SETUP: NOT BUILT — save the key into the tile URL, switch on.</summary>
		public void UseOnlineMap() { }

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
		/// <summary>Where a download goes. BASEMAP-SETUP: NOT BUILT — Browse… (a folder picker needs the window's HWND).</summary>
		public string SaveFolder
		{
			get => _saveFolder;
			set
			{
				if (SetProperty(ref _saveFolder, value ?? ""))
				{
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
		// window follows it through its steps (Downloading → Ready | Failed); the tab shows the same readout on one line.
		// ⚠️ Anvil does not DRAW a downloaded map yet — opening one is BASEMAP-SETUP: NOT BUILT (it still reads usa_full).

		private CancellationTokenSource? _downloadCancel;
		private List<BasemapArea> _downloadAreas = new();
		private bool _downloadFull;
		private bool _rateStarted;
		private DateTime _rateStartUtc;
		private ulong _rateStartBytes;
		private BasemapDownloadProgress _lastProgress;

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
			_rateStarted = false;
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
				Step = BasemapSetupStep.Ready;
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

		private void ShowProgress(BasemapDownloadProgress p)
		{
			_lastProgress = p;
			switch (p.Phase)
			{
				case BasemapDownloadPhase.FindingBuild:
					SetReadout(0, "Finding the newest map…", "");
					break;
				case BasemapDownloadPhase.Planning:
					SetReadout(0, "Working out what to download…", "");
					break;
				case BasemapDownloadPhase.Downloading:
					if (!_rateStarted)
					{
						_rateStarted = true;
						_rateStartUtc = DateTime.UtcNow;
						_rateStartBytes = p.BytesDone; // a resume starts its clock where it picked up
					}
					SetReadout(Percent(p), $"{BytesWords(p.BytesDone)} of {BytesWords(p.BytesTotal)}", TimeLeftWords(p));
					break;
				case BasemapDownloadPhase.Finishing:
					SetReadout(100, "Finishing…", "");
					break;
			}
		}

		private void SetReadout(double percent, string amount, string timeLeft)
		{
			DownloadPercent = percent;
			DownloadAmountText = amount;
			TimeLeftText = timeLeft;
			OnPropertyChanged(nameof(DownloadPercent));
			OnPropertyChanged(nameof(DownloadPercentText));
			OnPropertyChanged(nameof(DownloadAmountText));
			OnPropertyChanged(nameof(TimeLeftText));
			OnPropertyChanged(nameof(DownloadLineText));
		}

		private static double Percent(BasemapDownloadProgress p) => p.BytesTotal == 0 ? 0 : 100.0 * p.BytesDone / p.BytesTotal;

		private string TimeLeftWords(BasemapDownloadProgress p)
		{
			var seconds = (DateTime.UtcNow - _rateStartUtc).TotalSeconds;
			var moved = p.BytesDone - _rateStartBytes;
			if (seconds < 3 || moved == 0) return ""; // too early to say
			var left = (p.BytesTotal - p.BytesDone) / (moved / seconds);
			return left < 60 ? "Less than a minute left"
				: left < 3600 ? $"About {Math.Ceiling(left / 60):0} minutes left"
				: $"About {left / 3600:0.0} hours left";
		}

		/// <summary>What went wrong, in plain words, with what to do and what's kept.</summary>
		internal static string FailureWords(Exception ex, BasemapDownloadProgress at) => ex switch
		{
			BasemapNotEnoughSpaceException s =>
				$"There isn't room on this drive: the map needs {BytesWords(s.NeededBytes)} and {BytesWords(s.FreeBytes)} is free. Free some space, or pick a smaller map.",
			_ when at.BytesTotal > 0 =>
				$"Your connection dropped at {BytesWords(at.BytesDone)} of {BytesWords(at.BytesTotal)}. What's downloaded is kept.",
			_ => "Couldn't reach the map service. Check your internet connection, then retry.",
		};

		/// <summary>Bytes as a reader sees them: "450 MB", "4.4 GB" (decimal, like the size estimates).</summary>
		internal static string BytesWords(ulong bytes) => SizeWords(bytes / 1e6);

		/// <summary>The map being downloaded, for the Downloading / Ready / Failed screens ("Standard · Lower 48 states").</summary>
		public string DownloadMapText { get; private set; } = "";

		/// <summary>The finished file (the Ready screen).</summary>
		public string DownloadedPath { get; private set; } = "";

		public double DownloadPercent { get; private set; }
		public string DownloadPercentText => $"{DownloadPercent:0}%";
		public string DownloadAmountText { get; private set; } = "";
		public string TimeLeftText { get; private set; } = "";

		/// <summary>Settings → Basemap's one-line readout while a download runs.</summary>
		public string DownloadLineText => string.Join(" · ", new[] { DownloadMapText, DownloadPercentText, DownloadAmountText, TimeLeftText }
			.Where(s => !string.IsNullOrEmpty(s)));

		private string _failureText = "";
		public string FailureText
		{
			get => _failureText;
			private set => SetProperty(ref _failureText, value);
		}

		// ── An existing file ────────────────────────────────────────────────────────────────────────────────

		private string _existingFilePath = "";
		/// <summary>The file picked on the "Use a map file you already have" screen. BASEMAP-SETUP: NOT BUILT — the
		/// picker, and the three checks (readable map · covers every radar site · detail, size and date).</summary>
		public string ExistingFilePath
		{
			get => _existingFilePath;
			set => SetProperty(ref _existingFilePath, value ?? "");
		}

		/// <summary>BASEMAP-SETUP: NOT BUILT.</summary>
		public void UseExistingFile() { }

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
			OnPropertyChanged(nameof(RestartNote));
			RaiseChoice(); // the next free file name depends on what's in the folder
		}

		// ── which map Anvil opens on ──
		// The page's map host is mapped ONCE, when the main window is built — so a new choice is drawn from the NEXT launch.
		private string? _launchedPath;

		/// <summary>App calls this as the main window is built: the file it maps is the one on screen this session.</summary>
		public void NoteLaunchedMap()
		{
			_launchedPath = InUsePath();
			_settings.Settings.DevPretendNoMap = false; // a dev "new user" launch ends here
			OnPropertyChanged(nameof(RestartNote));
		}

		/// <summary>Under "Anvil is using" when the choice differs from what's on screen: it applies when Anvil next opens.</summary>
		public string RestartNote =>
			_launchedPath is null && InUsePath() is null ? ""
			: !string.Equals(_launchedPath, InUsePath(), StringComparison.OrdinalIgnoreCase) ? "Anvil switches to this map the next time it opens."
			: "";

		/// <summary>Use: make <paramref name="row"/> the map Anvil draws (from the next launch). Turns the online map off —
		/// a file picked here is an offline choice.</summary>
		public void UseFile(BasemapFileRow row)
		{
			_settings.Settings.UseOnlineTiles = false;
			_settings.Settings.BasemapFilePath = row.Path;
			Refresh();
		}

		/// <summary>BASEMAP-SETUP: NOT BUILT — open Explorer on the file.</summary>
		public void ShowFileInFolder(BasemapFileRow row) { }

		/// <summary>Trash pressed: ask first, inline (one row asks at a time).</summary>
		public void AskDelete(BasemapFileRow row)
		{
			foreach (var r in Files) r.IsConfirmingDelete = ReferenceEquals(r, row);
		}

		public void KeepFile(BasemapFileRow row) => row.IsConfirmingDelete = false;

		/// <summary>BASEMAP-SETUP: NOT BUILT — delete the file (allowed for the one in use, after the warning). For now it
		/// only closes the question; nothing is deleted.</summary>
		public void DeleteFile(BasemapFileRow row) => row.IsConfirmingDelete = false;

		/// <summary>BASEMAP-SETUP: NOT BUILT — the tab's "Add a map file…" (a file picker needs the window's HWND).</summary>
		public void AddFile() { }

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
