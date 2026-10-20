using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
	}
}
