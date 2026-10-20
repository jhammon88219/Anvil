using CommunityToolkit.Mvvm.ComponentModel;

namespace Anvil.ViewModels
{
	/// <summary>
	/// One map file in Settings → Basemap → Your map files: what it is, how big, and whether Anvil is drawing it.
	/// The list is one file, maybe two (the user's estimate) — a row per file, no paging.
	/// </summary>
	public sealed class BasemapFileRow : ObservableObject
	{
		public BasemapFileRow(string path, string name, string details, bool isInUse)
		{
			Path = path;
			Name = name;
			Details = details;
			IsInUse = isInUse;
		}

		/// <summary>The file's full path (the row's tooltip, and what Use / Show in folder / Delete act on).</summary>
		public string Path { get; }

		/// <summary>The row's title, e.g. "Offline map".</summary>
		public string Name { get; }

		/// <summary>The line under the title: size · date · file name.</summary>
		public string Details { get; }

		/// <summary>Whether this is the map Anvil draws now — the row's "In use" tag; it gets no Use button.</summary>
		public bool IsInUse { get; }

		/// <summary>Not in use → the row offers Use.</summary>
		public bool CanUse => !IsInUse;

		private bool _isConfirmingDelete;
		/// <summary>The trash button was pressed: the row swaps to its inline "Delete …? Keep / Delete" question.</summary>
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

		/// <summary>The confirm question. Deleting the map in use is ALLOWED (the user's call) — with this warning.</summary>
		public string DeleteQuestion => IsInUse
			? $"Delete {Name}? Anvil is using it, so it will have no map until you pick another."
			: $"Delete {Name}?";
	}
}
