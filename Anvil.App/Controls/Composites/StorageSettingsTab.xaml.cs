using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.ViewModels;

namespace Anvil.Controls.Composites
{
	/// <summary>
	/// Settings → Storage (see the XAML header): Disk usage, Map, Offline map and Online map — each its own section control — over
	/// the coordinator <see cref="MapViewModel"/> (<see cref="MapViewModel.Storage"/> + <see cref="MapViewModel.BasemapSetup"/>).
	/// </summary>
	public sealed partial class StorageSettingsTab : UserControl, Primitives.ISettingsPage
	{
		public StorageSettingsTab()
		{
			InitializeComponent();
			// The map files can change outside this page (the setup window, Explorer): re-read them on show.
			Loaded += (_, _) => ViewModel?.BasemapSetup.Refresh();
			RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => { if (Visibility == Visibility.Visible) ViewModel?.BasemapSetup.Refresh(); });
		}

		/// <summary>The coordinator view model; bound from the host.</summary>
		public MapViewModel ViewModel
		{
			get => (MapViewModel)GetValue(ViewModelProperty);
			set => SetValue(ViewModelProperty, value);
		}

		public static readonly DependencyProperty ViewModelProperty =
			DependencyProperty.Register(nameof(ViewModel), typeof(MapViewModel), typeof(StorageSettingsTab), new PropertyMetadata(null));

		public string ResetAllToolTip => "Every limit and the Offline map choices back to their DEFAULT columns (asks first). No map file is deleted";

		public async void ResetAll()
		{
			if (ViewModel is { } vm && await Anvil.Dialogs.ConfirmResetDialog.ConfirmAsync(this, "Reset all Storage settings?",
				"Every Max size and Keep for, and the Offline map's area, detail and folder, go back to their defaults — the DEFAULT " +
				"column on this page. No map file is deleted and your map key is kept."))
			{
				vm.Storage.RestoreDefaults();
				vm.BasemapSetup.RestoreOfflineDefaults();
			}
		}
	}
}
