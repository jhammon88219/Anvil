using CommunityToolkit.Mvvm.ComponentModel;

namespace Anvil.ViewModels
{
	/// <summary>
	/// One map file in Settings → Storage → Which map to use: what it is (a SHORT name + a detail tag), how big, when it was
	/// saved, and whether Anvil is drawing it. Every value sits in its own column (2026-11-19); the file name lives in the tooltip.
	/// </summary>
	public sealed class BasemapFileRow : ObservableObject
	{
		public BasemapFileRow(string path, string name, string detail, string sizeText, string dateText, string tooltip, bool isInUse)
		{
			Path = path;
			Name = name;
			Detail = detail;
			SizeText = sizeText;
			DateText = dateText;
			Tooltip = tooltip;
			IsInUse = isInUse;
		}

		/// <summary>The file's full path (what Use / Show in folder / Delete act on).</summary>
		public string Path { get; }

		/// <summary>The SHORT name: "Lower 48 + Alaska", "Lower 48 + 3 more", "Older USA map".</summary>
		public string Name { get; }

		/// <summary>"Standard" / "Full" (the DETAIL tag), or "" for a file whose name doesn't say.</summary>
		public string Detail { get; }

		public bool HasDetail => Detail.Length > 0;

		/// <summary>"5.0 GB" (Explorer's units).</summary>
		public string SizeText { get; }

		/// <summary>When the file was saved, in the user's date format.</summary>
		public string DateText { get; }

		/// <summary>Every area by its full name, then the file's path.</summary>
		public string Tooltip { get; }

		/// <summary>Whether this is the map Anvil draws now — its radio is on.</summary>
		public bool IsInUse { get; }

		private bool _isConfirmingDelete;
		/// <summary>The trash button was pressed: the row swaps to its one-line "Delete … · frees … [Keep] [Delete]".</summary>
		public bool IsConfirmingDelete
		{
			get => _isConfirmingDelete;
			set
			{
				if (SetProperty(ref _isConfirmingDelete, value)) OnPropertyChanged(nameof(IsShowingDetails));
			}
		}

		/// <summary>The normal row face (the opposite of <see cref="IsConfirmingDelete"/>).</summary>
		public bool IsShowingDetails => !_isConfirmingDelete;

		/// <summary>The confirm line. Deleting the map in use is ALLOWED (the user's call) — with this warning.</summary>
		public string DeleteQuestion => IsInUse
			? $"Delete {Name} · frees {SizeText} · Anvil draws no map until you pick another"
			: $"Delete {Name} · frees {SizeText}";
	}
}
