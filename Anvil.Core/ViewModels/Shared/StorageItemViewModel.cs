using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Anvil.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Anvil.ViewModels
{
	/// <summary>
	/// ONE row of Settings → Storage (<c>StorageManagerSettingsConsoleDataTableRow</c>) and its usage-bar segment:
	/// a kind's size, fill level, Max size / Keep for choices, its Clear action and folder. Built by
	/// <see cref="StorageSettingsViewModel"/>, which owns the measuring and the file work.
	/// </summary>
	public sealed class StorageItemViewModel : ObservableObject
	{
		private readonly Action<long>? _setMax;
		private readonly Action<long>? _setKeep;
		private readonly Func<Task>? _clear;

		internal StorageItemViewModel(StorageKind kind, string name, string hint, string folder, Func<Task>? clear,
			bool confirmClear,
			IReadOnlyList<StorageLimitChoice>? maxChoices, StorageLimitChoice? max, Action<long>? setMax,
			IReadOnlyList<StorageLimitChoice>? keepChoices, StorageLimitChoice? keep, Action<long>? setKeep,
			long? defaultMax = null, long? defaultKeep = null, string noKeepText = "Always kept")
		{
			Kind = kind;
			Name = name;
			Hint = hint;
			_folder = folder;
			_clear = clear;
			ConfirmClear = confirmClear;
			MaxChoices = maxChoices ?? Array.Empty<StorageLimitChoice>();
			_max = max;
			_setMax = setMax;
			KeepChoices = keepChoices ?? Array.Empty<StorageLimitChoice>();
			_keep = keep;
			_setKeep = setKeep;
			_defaultMax = defaultMax;
			_defaultKeep = defaultKeep;
			NoKeepText = noKeepText;
		}

		public StorageKind Kind { get; }
		public string Name { get; }
		/// <summary>The grey words in the column after the name ("Loops you've loaded"); empty for none.</summary>
		public string Hint { get; }

		private string _folder;
		/// <summary>The folder the row's folder button opens. (Maps: set when the map folder is known — AttachMaps.)</summary>
		public string Folder
		{
			get => _folder;
			internal set => SetProperty(ref _folder, value);
		}

		/// <summary>False = no Clear button (Maps: files are deleted one by one in the Map card).</summary>
		public bool HasClear => _clear is not null;

		/// <summary>The Keep for cell when there is no dropdown: "Always kept" (usage), or "" = a hairline (Maps).</summary>
		public string NoKeepText { get; }

		// ── DEFAULT (the card's DEFAULT column + its Restore defaults) ──
		private readonly long? _defaultMax, _defaultKeep;

		/// <summary>"5 GB · 5 days" — the limits a first run gets; "" when the kind has none.</summary>
		public string DefaultText
		{
			get
			{
				var max = _defaultMax is { } m ? LabelOf(MaxChoices, m) : null;
				var keep = _defaultKeep is { } k ? LabelOf(KeepChoices, k) : null;
				return max is not null && keep is not null ? $"{max} · {keep}" : max ?? keep ?? "";
			}
		}

		private static string? LabelOf(IReadOnlyList<StorageLimitChoice> list, long value)
		{
			foreach (var c in list) if (c.Value == value) return c.Label;
			return null;
		}

		/// <summary>Both limits back to <see cref="DefaultText"/>'s values (through the setters, so the owner sweeps).</summary>
		internal void RestoreDefaults()
		{
			if (_defaultMax is { } m) foreach (var c in MaxChoices) if (c.Value == m) { SelectedMax = c; break; }
			if (_defaultKeep is { } k) foreach (var c in KeepChoices) if (c.Value == k) { SelectedKeep = c; break; }
		}
		public string FolderToolTip => $"Open the {Name.ToLowerInvariant()} folder";

		/// <summary>Usage history asks first (it's your record, not a cache); the rest clear at once.</summary>
		public bool ConfirmClear { get; }

		private long _sizeBytes;
		/// <summary>Bytes on disk (the bar's segment weight).</summary>
		public long SizeBytes
		{
			get => _sizeBytes;
			private set => SetProperty(ref _sizeBytes, value);
		}

		private string _sizeText = "…";
		public string SizeText
		{
			get => _sizeText;
			private set => SetProperty(ref _sizeText, value);
		}

		private string _shareText = "";
		/// <summary>Share of everything Anvil keeps ("85%", "&lt;1%") — the bar's legend.</summary>
		public string ShareText
		{
			get => _shareText;
			private set => SetProperty(ref _shareText, value);
		}

		private bool _isHovered, _isDimmed;
		/// <summary>The pointer is over this row (set by the table row) — the parent dims every OTHER bar segment.</summary>
		public bool IsHovered
		{
			get => _isHovered;
			set => SetProperty(ref _isHovered, value);
		}

		/// <summary>Another row is hovered: this kind's bar segment dims (StorageSettingsViewModel sets it).</summary>
		public bool IsDimmed
		{
			get => _isDimmed;
			internal set => SetProperty(ref _isDimmed, value);
		}

		private StorageFill _fill;
		/// <summary>White / yellow / red — the row's VisualState.</summary>
		public StorageFill Fill
		{
			get => _fill;
			private set => SetProperty(ref _fill, value);
		}

		// ── Max size ──
		public IReadOnlyList<StorageLimitChoice> MaxChoices { get; }
		public bool HasMax => MaxChoices.Count > 0;
		/// <summary>False = a limit shown greyed with its one fixed value (none today — every limit is a setting).</summary>
		public bool IsMaxEditable => _setMax is not null;

		private StorageLimitChoice? _max;
		public StorageLimitChoice? SelectedMax
		{
			get => _max;
			set
			{
				if (value is null || value == _max || _setMax is null) return; // a ComboBox clears to null while re-templating
				_max = value;
				OnPropertyChanged();
				_setMax(value.Value);
			}
		}

		// ── Keep for ──
		public IReadOnlyList<StorageLimitChoice> KeepChoices { get; }
		public bool HasKeep => KeepChoices.Count > 0;
		public bool IsKeepEditable => _setKeep is not null;

		private StorageLimitChoice? _keep;
		public StorageLimitChoice? SelectedKeep
		{
			get => _keep;
			set
			{
				if (value is null || value == _keep || _setKeep is null) return;
				_keep = value;
				OnPropertyChanged();
				_setKeep(value.Value);
			}
		}

		// ── Clear ──
		private bool _isClearing;
		public bool CanClear => !_isClearing;

		/// <summary>Delete this kind's files, then the owner re-measures. A second click while one runs is ignored.</summary>
		public async Task ClearAsync()
		{
			if (_isClearing || _clear is null) return;
			_isClearing = true;
			OnPropertyChanged(nameof(CanClear));
			try { await _clear(); }
			finally
			{
				_isClearing = false;
				OnPropertyChanged(nameof(CanClear));
			}
		}

		/// <summary>Set by the owner after measuring every kind (share needs the total).</summary>
		internal void ApplySize(long bytes, long total)
		{
			SizeBytes = bytes;
			SizeText = StorageLimits.FormatBytes(bytes);
			ShareText = StorageLimits.FormatShare(bytes, total);
			Fill = StorageLimits.FillOf(bytes, HasMax ? _max?.Value : null);
		}

		/// <summary>A failed measure: a dash, never a stale number.</summary>
		internal void ApplyUnknown()
		{
			SizeText = "—";
			ShareText = "";
			Fill = StorageFill.Low;
		}
	}
}
