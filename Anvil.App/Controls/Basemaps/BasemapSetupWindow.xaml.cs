using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.ViewModels;

namespace Anvil.Controls.Basemaps
{
	/// <summary>
	/// The map setup window's content (see the XAML header). Hosted by <c>BasemapSetupHost</c>; asks to open Anvil through
	/// <see cref="OpenAnvilRequested"/> ("Continue without a map", or Open Anvil once the map is ready).
	/// </summary>
	public sealed partial class BasemapSetupWindow : UserControl
	{
		public BasemapSetupWindow(BasemapSetupViewModel viewModel)
		{
			ViewModel = viewModel; // ⚠️ before InitializeComponent, or x:Bind silently breaks
			InitializeComponent();
		}

		public BasemapSetupViewModel ViewModel { get; }

		/// <summary>The user is done here: close this window and open Anvil.</summary>
		public event EventHandler? OpenAnvilRequested;

		private void OnOpenAnvilClick(object sender, RoutedEventArgs e) => OpenAnvilRequested?.Invoke(this, EventArgs.Empty);

		/// <summary>Which step body shows: visible when the window is on <paramref name="mine"/>.</summary>
		public Visibility StepVisibility(int step, int mine) => step == mine ? Visibility.Visible : Visibility.Collapsed;

		public string ExistingFileText(string path) => string.IsNullOrEmpty(path) ? "No file picked yet" : path;
	}
}
