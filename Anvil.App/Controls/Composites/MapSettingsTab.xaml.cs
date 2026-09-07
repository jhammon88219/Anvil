using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.Models;
using Anvil.Services;
using Anvil.ViewModels;

namespace Anvil.Controls.Composites
{
	/// <summary>
	/// The Settings window's Map tab (see the XAML header): basemap style, and the two halves of the tile
	/// SOURCE — the offline data folder and the online tiles URL. Bound to the coordinator
	/// <see cref="MapViewModel"/>.
	/// </summary>
	public sealed partial class MapSettingsTab : UserControl
	{
		public MapSettingsTab()
		{
			InitializeComponent();
			ImportedList.ItemsSource = ImportedStyles;
		}

		/// <summary>The coordinator view model; bound from the host.</summary>
		public MapViewModel ViewModel
		{
			get => (MapViewModel)GetValue(ViewModelProperty);
			set => SetValue(ViewModelProperty, value);
		}

		public static readonly DependencyProperty ViewModelProperty =
			DependencyProperty.Register(nameof(ViewModel), typeof(MapViewModel), typeof(MapSettingsTab), new PropertyMetadata(null));

		/// <summary>
		/// Raised when Browse… is clicked. ⚠️ The tab does NOT open the picker itself: a WinRT
		/// <c>FolderPicker</c> must be initialized with a window HWND, and a UserControl has no window of
		/// its own — so the host (SettingsWindow, which IS a Window) shows it and calls
		/// <see cref="MapViewModel.SetMapDataFolder"/> with the result. Same shape as the Dev tab's report
		/// events, for the same reason.
		/// </summary>
		public event EventHandler? BrowseMapDataFolderRequested;

		private void OnBrowseMapDataClick(object sender, RoutedEventArgs e) =>
			BrowseMapDataFolderRequested?.Invoke(this, EventArgs.Empty);

		// ===== Style library =====
		// ⚠️ Import BUBBLES for the same reason the basemap folder does: a file picker needs a window
		// HWND and this is a UserControl. MainWindow shows it, then calls back into ImportAsync below with
		// a path — so the LIBRARY work stays here, beside the list that shows the result, and only the
		// picker itself lives up there.
		public event EventHandler? ImportStyleRequested;

		/// <summary>The imported styles, for the removable list. Bundled ones are not shown — they cannot
		/// be removed, so a list of mostly-inert rows would be worse than no list.</summary>
		private ObservableCollection<MapStyle> ImportedStyles { get; } = new();

		/// <summary>What the last import did, or why it could not. Empty until something happens.</summary>
		public string ImportStatus
		{
			get => _importStatus;
			private set { _importStatus = value; Bindings.Update(); }
		}

		private string _importStatus = "";

		private void OnImportStyleClick(object sender, RoutedEventArgs e) =>
			ImportStyleRequested?.Invoke(this, EventArgs.Empty);

		/// <summary>
		/// Copies the chosen style into the library and selects it. Called by MainWindow once its picker has
		/// produced a path.
		/// </summary>
		/// <remarks>
		/// ⚠️ A FAILED IMPORT IS A MESSAGE, NOT A THROW. The reasons a user can act on — not JSON, no
		/// layers, no sources — are all InvalidDataException from the library, whose text is written to be
		/// read. A picker that silently does nothing is the worst version of this.
		/// ⚠️ It SELECTS the import too. Importing something and then having to find it in a combo is a
		/// step nobody wants, and selecting it is also the only way to see whether it renders.
		/// </remarks>
		public async Task ImportAsync(IMapStyleLibrary library, string path)
		{
			try
			{
				var style = await library.ImportAsync(path);
				ViewModel?.RefreshStyles();
				RefreshImported(library);

				// Re-resolve by id: RefreshStyles rebuilt the list, so the instance ImportAsync returned is
				// not the one the combo is now holding.
				var added = ViewModel?.AvailableStyles.FirstOrDefault(x => x.Id == style.Id);
				if (added is not null && ViewModel is not null)
				{
					ViewModel.SelectedStyle = added;
				}

				ImportStatus = $"Imported \"{style.DisplayName}\" and selected it.";
			}
			catch (Exception ex)
			{
				ImportStatus = ex is InvalidDataException or FileNotFoundException
					? ex.Message
					: "That style could not be imported.";
			}
		}

		private void OnRemoveStyleClick(object sender, RoutedEventArgs e)
		{
			if (sender is not Button button || button.Tag is not string fileName || Library is null) return;

			Library.Remove(fileName);
			// ⚠️ RefreshStyles falls the SELECTION back to the theme's style when the removed one was
			// active — otherwise the map would be pointed at a file that no longer exists.
			ViewModel?.RefreshStyles();
			RefreshImported(Library);
			ImportStatus = "Removed.";
		}

		/// <summary>The library, handed in by the host so this control can enumerate and remove.</summary>
		public IMapStyleLibrary? Library
		{
			get => _library;
			set { _library = value; if (value is not null) RefreshImported(value); }
		}

		private IMapStyleLibrary? _library;

		private void RefreshImported(IMapStyleLibrary library)
		{
			ImportedStyles.Clear();
			foreach (var style in library.GetStyles())
			{
				ImportedStyles.Add(style);
			}
		}

		/// <summary>
		/// The offline-source status line: whether the archive is present, plus the restart note once the
		/// folder has been changed this session. ⚠️ An x:Bind FUNCTION rather than a VM property because it
		/// composes two VM values — keeping it here means the VM does not have to model "what the view is
		/// currently saying", and both inputs re-evaluate it on their own change.
		/// </summary>
		public string MapDataLine(string status, bool changed) =>
			changed ? status + "  Restart Anvil to load the basemap from this folder." : status;
	}
}
