using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
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
	/// showing this at launch, the file list, free space, the in-use line, the delete QUESTION. What does NOT yet: every
	/// action marked <c>BASEMAP-SETUP: NOT BUILT</c> — key test, use online, download / cancel / retry, use / delete /
	/// show a file, the folder + file pickers. Grep the sentinel to find them.
	/// </remarks>
	public sealed class BasemapSetupViewModel : ObservableObject
	{
		private readonly ISettingsService _settings;

		public BasemapSetupViewModel(ISettingsService settings, IRadarSiteProvider radarSites)
		{
			_settings = settings;
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
		/// until the user ticks "Stop showing this at launch".</summary>
		public bool ShouldShowAtLaunch => _settings.Settings.ShowBasemapSetupAtLaunch && !HasUsableMap;

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
				if (SetProperty(ref _step, value)) OnPropertyChanged(nameof(StepIndex));
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

		/// <summary>Download needs at least one area ticked; the online route is always pressable.</summary>
		public bool CanRunPrimaryAction => _isOnlineChosen || HasAnyArea;

		/// <summary>The main button. BASEMAP-SETUP: NOT BUILT — online = test + switch on, offline = start the download.</summary>
		public void RunPrimaryAction() { }

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

		/// <summary>The file a download would make now (Settings → Basemap's File line); "—" with nothing ticked.</summary>
		public string PlannedFileName => HasAnyArea
			? BasemapFileNames.NextFileName(IsFullDetail, CheckedAreas, ExistingMapFileNames())
			: "—";

		/// <summary>Said under the File line when the same options are already downloaded — ALWAYS kept beside (the user's call).</summary>
		public string DuplicateNote => HasAnyArea && BasemapFileNames.TryParse(PlannedFileName)?.Copy > 1
			? "You already have this map. This one is kept beside it."
			: "";

		private void OnAreasChanged()
		{
			OnPropertyChanged(nameof(HasAnyArea));
			OnPropertyChanged(nameof(CanRunPrimaryAction));
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

		/// <summary>Settings → Basemap's Download. BASEMAP-SETUP: NOT BUILT — runs in the background there; Anvil keeps its
		/// current map until the user switches.</summary>
		public void StartDownload() { }

		/// <summary>BASEMAP-SETUP: NOT BUILT.</summary>
		public void CancelDownload() => Step = BasemapSetupStep.Choose;

		/// <summary>BASEMAP-SETUP: NOT BUILT — resume from what's on disk.</summary>
		public void RetryDownload() { }

		// The download's readout. BASEMAP-SETUP: NOT BUILT — nothing feeds these yet; they show their resting values.
		public double DownloadPercent { get; private set; }
		public string DownloadPercentText => $"{DownloadPercent:0}%";
		public string DownloadAmountText { get; private set; } = "Starting…";
		public string TimeLeftText { get; private set; } = "";
		public string FailureText { get; private set; } = "Your connection dropped. What's downloaded is kept.";

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

		/// <summary>The map files Anvil knows. Today: the offline file in the current folder, if there is one.</summary>
		public ObservableCollection<BasemapFileRow> Files { get; } = new();

		/// <summary>Empty list → the "No map files yet" line.</summary>
		public bool HasNoFiles => Files.Count == 0;

		/// <summary>Re-read the folder and the source (the tab calls this when it shows).</summary>
		public void Refresh()
		{
			Files.Clear();
			var onlineActive = _settings.Settings.UseOnlineTiles && OnlineTileUrls.IsUsable(_settings.Settings.OnlineTilesUrl);
			BasemapFileRow? offline = null;
			if (_settings.MapDataFilePresent())
			{
				var path = Path.Combine(_settings.MapDataFolder, _settings.MapDataFileName);
				try
				{
					var info = new FileInfo(path);
					var details = $"{GigabytesText(info.Length)} · {info.LastWriteTime.ToString("d MMM yyyy", CultureInfo.CurrentCulture)} · {info.Name}";
					offline = new BasemapFileRow(path, "Offline map", details, isInUse: !onlineActive);
					Files.Add(offline);
				}
				catch (IOException) { }
				catch (UnauthorizedAccessException) { }
			}

			// Downloaded maps in the save folder, titled from their names ("Standard · Lower 48 states, Alaska"). None is
			// drawn yet — opening a downloaded map is BASEMAP-SETUP: NOT BUILT.
			foreach (var name in ExistingMapFileNames().OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
			{
				if (BasemapFileNames.TryParse(name) is not { } options) continue;
				var path = Path.Combine(_saveFolder, name);
				try
				{
					var info = new FileInfo(path);
					var details = $"{GigabytesText(info.Length)} · {info.LastWriteTime.ToString("d MMM yyyy", CultureInfo.CurrentCulture)} · {name}";
					Files.Add(new BasemapFileRow(path, options.Description, details, isInUse: false));
				}
				catch (IOException) { }
				catch (UnauthorizedAccessException) { }
			}

			InUseText = onlineActive ? "Online map · Protomaps"
				: offline is not null ? $"Offline map · {_settings.MapDataFileName}"
				: "No map. Radar and overlays draw over a blank background.";

			OnPropertyChanged(nameof(HasNoFiles));
			OnPropertyChanged(nameof(HasUsableMap));
			OnPropertyChanged(nameof(FreeSpaceText));
			RaiseChoice(); // the next free file name depends on what's in the folder
		}

		/// <summary>BASEMAP-SETUP: NOT BUILT — make <paramref name="row"/> the map Anvil draws.</summary>
		public void UseFile(BasemapFileRow row) { }

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
					Refresh();
					break;
			}
		}

		// Explorer's convention (GiB, labelled GB), so a size here matches what the user sees in File Explorer.
		private static string GigabytesText(long bytes) =>
			string.Create(CultureInfo.CurrentCulture, $"{bytes / (1024.0 * 1024 * 1024):0.0} GB");
	}
}
