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
		private readonly Func<Task> _clear;

		internal StorageItemViewModel(StorageKind kind, string name, string hint, string folder, Func<Task> clear,
			bool confirmClear,
			IReadOnlyList<StorageLimitChoice>? maxChoices, StorageLimitChoice? max, Action<long>? setMax,
			IReadOnlyList<StorageLimitChoice>? keepChoices, StorageLimitChoice? keep, Action<long>? setKeep)
		{
			Kind = kind;
			Name = name;
			Hint = hint;
			Folder = folder;
			_clear = clear;
			ConfirmClear = confirmClear;
			MaxChoices = maxChoices ?? Array.Empty<StorageLimitChoice>();
			_max = max;
			_setMax = setMax;
			KeepChoices = keepChoices ?? Array.Empty<StorageLimitChoice>();
			_keep = keep;
			_setKeep = setKeep;
		}

		public StorageKind Kind { get; }
		public string Name { get; }
		/// <summary>The grey words in the column after the name ("Loops you've loaded"); empty for none.</summary>
		public string Hint { get; }
		/// <summary>The folder the row's folder button opens.</summary>
		public string Folder { get; }
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
			if (_isClearing) return;
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
