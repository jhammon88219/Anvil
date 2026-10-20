using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Anvil.Controls.Basemaps;
using Anvil.ViewModels;

namespace Anvil
{
	/// <summary>
	/// Shows the MAP SETUP window (<see cref="BasemapSetupWindow"/>) in its own OS window: before Anvil at launch
	/// (<c>App.OnLaunched</c>, while <see cref="BasemapSetupViewModel.ShouldShowAtLaunch"/>), or as a Debug preview from
	/// Settings → Dev. Not a <c>WindowManager</c> window — it exists before the main window it would be owned by.
	/// </summary>
	public static class BasemapSetupHost
	{
		// One fixed size (DIPs): the two cards side by side + the footer. Not resizable — nothing in it grows.
		private const int WidthDip = 720;
		private const int HeightDip = 660;

		[DllImport("user32.dll")]
		private static extern uint GetDpiForWindow(IntPtr hWnd);

		/// <summary>
		/// Open the window. <paramref name="onOpenAnvil"/> runs once, when the user asks for Anvil ("Continue without a
		/// map" / Open Anvil) — the window closes first. Closing it with × runs nothing: at launch that quits Anvil, which
		/// is what closing the only window means.
		/// </summary>
		public static Window Show(BasemapSetupViewModel viewModel, ElementTheme theme, Action? onOpenAnvil)
		{
			var content = new BasemapSetupWindow(viewModel) { RequestedTheme = theme };
			// The hosting Window shell is built in code — a WinUI Window can't be declared as a XAML resource and shown;
			// everything IN it is declared in BasemapSetupWindow.xaml.
			var window = new Window { Title = "Anvil setup", Content = content };

			var opened = false;
			content.OpenAnvilRequested += (_, _) =>
			{
				if (opened) return;
				opened = true;
				onOpenAnvil?.Invoke(); // the main window first, so the app never has zero windows
				window.Close();
			};

			if (window.AppWindow is AppWindow appWindow)
			{
				if (appWindow.Presenter is OverlappedPresenter presenter)
				{
					presenter.IsMaximizable = false;
					presenter.IsResizable = false;
				}

				var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
				var scale = GetDpiForWindow(hwnd) / 96.0;
				var w = (int)Math.Round(WidthDip * scale);
				var h = (int)Math.Round(HeightDip * scale);
				var area = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary).WorkArea;
				appWindow.MoveAndResize(new Windows.Graphics.RectInt32(
					area.X + Math.Max(0, (area.Width - w) / 2), area.Y + Math.Max(0, (area.Height - h) / 2), w, h));
			}

			window.Activate();
			return window;
		}
	}
}
