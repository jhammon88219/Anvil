using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using Anvil.Models;
using Anvil.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Anvil.ViewModels
{
	/// <summary>
	/// Getting Anvil a MAP: the map setup window shown before launch (<c>Controls/Basemaps/BasemapSetupWindow</c>) and
	/// Settings → Basemap (<c>Controls/Basemaps/BasemapSettingsTab</c>) — one instance (an app DI singleton) behind both.
	/// Two routes: an ONLINE map (the user's free Protomaps key) or an OFFLINE map (a download Anvil extracts from
	/// Protomaps' daily planet build, Standard or Full detail).
	/// </summary>
	/// <remarks>
	/// ⚠️ UI FIRST (2026-10-20, the user's call: "get the UI in, then slowly make it work"). What WORKS: moving between
	/// the window's steps, the card choice, Stop showing this at launch, the file list (read from the current offline
	/// folder), free space, the in-use line, the delete QUESTION. What does NOT yet: every action marked
	/// <c>BASEMAP-SETUP: NOT BUILT</c> — key test, use online, download / cancel / retry, use / delete / show a file,
	/// the folder + file pickers. Grep the sentinel to find them.
	/// Sizes (measured 2026-10-20 by a pmtiles dry run over a box per radar site): level 12 ≈ 2.4 GB, 13 ≈ 5.1 GB,
	/// 15 ≈ 22 GB. Standard = 13 for now (12 vs 13 not settled). Canada isn't measured — its box is a later add.
	/// </remarks>
	public sealed class BasemapSetupViewModel : ObservableObject
	{
		private readonly ISettingsService _settings;

		public BasemapSetupViewModel(ISettingsService settings)
		{
			_settings = settings;
			_saveFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Anvil Maps");
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
				if (SetProperty(ref _detailIndex, v))
				{
					OnPropertyChanged(nameof(DetailSizeText));
					OnPropertyChanged(nameof(DetailName));
				}
			}
		}

		/// <summary>"Standard" / "Full" — the download and ready screens name it.</summary>
		public string DetailName => DetailLabels[_detailIndex];

		/// <summary>What the picked detail shows and costs, under the picker.</summary>
		public string DetailSizeText => _detailIndex == 0
			? "Streets at city zoom · about 5 GB"
			: "Every street and building · about 22 GB";

		private bool _includeCanada;
		/// <summary>Add Canada to the download (for Canadian radars later). Off by default. BASEMAP-SETUP: NOT BUILT — no
		/// Canada box yet, and its size isn't measured.</summary>
		public bool IncludeCanada
		{
			get => _includeCanada;
			set => SetProperty(ref _includeCanada, value);
		}

		private string _saveFolder;
		/// <summary>Where a download goes. BASEMAP-SETUP: NOT BUILT — Browse… (a folder picker needs the window's HWND).</summary>
		public string SaveFolder
		{
			get => _saveFolder;
			set
			{
				if (SetProperty(ref _saveFolder, value ?? "")) OnPropertyChanged(nameof(FreeSpaceText));
			}
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

			InUseText = onlineActive ? "Online map · Protomaps"
				: offline is not null ? $"Offline map · {_settings.MapDataFileName}"
				: "No map. Radar and overlays draw over a blank background.";

			OnPropertyChanged(nameof(HasNoFiles));
			OnPropertyChanged(nameof(HasUsableMap));
			OnPropertyChanged(nameof(FreeSpaceText));
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
