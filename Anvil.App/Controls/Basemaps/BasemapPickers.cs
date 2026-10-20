using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Windows.Storage.Pickers;

namespace Anvil.Controls.Basemaps
{
	/// <summary>
	/// Map setup's pickers + Explorer, shown from whichever window the control sits in (the setup window before Anvil
	/// exists, or Settings). A WinRT picker must be initialised with a window handle; a UserControl finds its own
	/// through its XamlRoot, so nothing has to bubble up to MainWindow (the Map tab's older route).
	/// </summary>
	internal static class BasemapPickers
	{
		/// <summary>A folder, or null if cancelled (or the control isn't in a window yet).</summary>
		public static async Task<string?> PickFolderAsync(UIElement owner)
		{
			var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
			picker.FileTypeFilter.Add("*"); // required: the picker throws on show with an empty filter list
			if (!Initialize(picker, owner)) return null;
			try { return (await picker.PickSingleFolderAsync())?.Path; }
			catch (Exception ex) { Debug.WriteLine($"[basemap] folder picker failed: {ex.Message}"); return null; }
		}

		/// <summary>A .pmtiles map file, or null if cancelled.</summary>
		public static async Task<string?> PickMapFileAsync(UIElement owner)
		{
			var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
			picker.FileTypeFilter.Add(".pmtiles");
			if (!Initialize(picker, owner)) return null;
			try { return (await picker.PickSingleFileAsync())?.Path; }
			catch (Exception ex) { Debug.WriteLine($"[basemap] file picker failed: {ex.Message}"); return null; }
		}

		/// <summary>Open Explorer with the file selected.</summary>
		public static void ShowInFolder(string path)
		{
			try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); }
			catch (Exception ex) { Debug.WriteLine($"[basemap] Explorer failed: {ex.Message}"); }
		}

		private static bool Initialize(object picker, UIElement owner)
		{
			if (owner.XamlRoot?.ContentIslandEnvironment is not { } island) return false;
			var hwnd = Microsoft.UI.Win32Interop.GetWindowFromWindowId(island.AppWindowId);
			WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
			return true;
		}
	}
}
