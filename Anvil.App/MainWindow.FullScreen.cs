// ===== FULL-SCREEN (bolt-on, 2026-11-04 retry) — grep "FULL-SCREEN (bolt-on)" for every hook =====
//
//   windowed (maximized, as at launch)          F11 / View → Full screen
//   ┌ menu bar ──────────────── – □ ✕ ┐   ──►   ┌ menu bar ──────────────── – □ ✕ ┐   the same layout, edge to edge:
//   ├──┬──────────────────────────────┤         ├──┬──────────────────────────────┤   no taskbar, no window frame
//   │▤ │  map                         │         │▤ │  map        [hint ~3 s]       │
//   └──┴──────────────────────────────┘         └──┴──────────────────────────────┘
//
// STAGE 1 of the retry (the user's call: careful, easy to back out of). The menu bar STAYS in full screen; an auto-hide is
// stage 2, only if the PROBE below proves a dependable trigger. Microsoft's documented pattern throughout:
// • Enter = AppWindow.SetPresenter(FullScreen); exit = SetPresenter(DEFAULT), which re-applies the window's ORIGINAL
//   presenter ("Manage app windows"), then Maximize again if it was (queued — microsoft-ui-xaml #10439).
// • NO non-client region writes during the switch (the first attempt fail-fasted in Microsoft.UI.Windowing.dll when
//   AppWindow.Changed rewrote them mid-switch — docs/bugs.md U11): the regions are cleared first, UpdateCaptionBand /
//   UpdateNotchInputRegions stand down while _isSwitchingPresenter, and AfterPresenterSwitch re-writes them on the next
//   dispatcher turn, once layout has caught up.
// • KEYS: F11 = a root KeyboardAccelerator (placement Hidden — else an "F11" tooltip pill floats over the map). Esc = the
//   root's BUBBLING KeyDown, so a text box / flyout / dialog that handles Esc keeps it. A focused MAP swallows both
//   (WebView2), so map.js forwards them as "hostKey" (WebMessageRouter.HostKeyPressed → OnFullScreenHostKey).
// • NEVER remembered: the app always starts windowed.
// • PROBE (stage 1 only, logs, draws nothing): while full screen, every 100 ms read the REAL cursor (GetCursorPos) and log
//   when it reaches / leaves the top 2 px of the window — "Full screen probe: …" in the Serilog log. Reading the cursor
//   sidesteps everything that sank the first hover strip (the WebView, the non-client band, the system's top edge).
//
// BACK OUT = delete this file + every block tagged FULL-SCREEN (bolt-on) in MainWindow.xaml(.cs), WebMessageRouter.cs, map.js.
using System;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Graphics;

namespace Anvil
{
	public sealed partial class MainWindow
	{
		private bool _isFullScreen;
		private bool _isFullScreenSwitchQueued;
		private bool _isSwitchingPresenter; // the region writers stand down while true
		private bool _wasMaximizedBeforeFullScreen;
		private DispatcherQueueTimer? _fullScreenProbe;
		private bool _probeCursorAtTop;
		private const int FullScreenProbeMs = 100;
		private const int FullScreenProbeEdgePx = 2;

		private void InitFullScreen()
		{
			_router.HostKeyPressed += OnFullScreenHostKey;
			RootGrid.KeyDown += OnRootKeyDownForFullScreen; // bubbling: only keys nothing below handled
			_fullScreenProbe = DispatcherQueue.CreateTimer();
			_fullScreenProbe.Interval = TimeSpan.FromMilliseconds(FullScreenProbeMs);
			_fullScreenProbe.IsRepeating = true;
			_fullScreenProbe.Tick += OnFullScreenProbeTick;
		}

		/// <summary>Enter or leave full screen. Queued: it never runs inside key / menu processing or a windowing callback.</summary>
		private void SetFullScreen(bool on)
		{
			ViewFullScreenItem.IsChecked = _isFullScreen; // the item flipped itself; the window's state answers
			if (_isClosed || _isFullScreenSwitchQueued || on == _isFullScreen)
			{
				return;
			}
			_isFullScreenSwitchQueued = true;
			DispatcherQueue.TryEnqueue(() =>
			{
				_isFullScreenSwitchQueued = false;
				if (_isClosed)
				{
					return;
				}
				try
				{
					_isSwitchingPresenter = true;
					ClearNonClientRegions();
					if (on)
					{
						_wasMaximizedBeforeFullScreen =
							(AppWindow.Presenter as OverlappedPresenter)?.State == OverlappedPresenterState.Maximized;
						_logger.LogInformation("Full screen: entering (was maximized {Max})", _wasMaximizedBeforeFullScreen);
						AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
					}
					else
					{
						_logger.LogInformation("Full screen: leaving");
						AppWindow.SetPresenter(AppWindowPresenterKind.Default);
					}
					_logger.LogInformation("Full screen: presenter is {Kind}", AppWindow.Presenter.Kind);
				}
				catch (Exception ex)
				{
					_logger.LogError(ex, "Full screen: presenter switch failed");
				}
				finally
				{
					_isSwitchingPresenter = false;
				}
				DispatcherQueue.TryEnqueue(AfterPresenterSwitch);
			});
		}

		private void ClearNonClientRegions()
		{
			var source = InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
			source.ClearRegionRects(NonClientRegionKind.Caption);
			source.ClearRegionRects(NonClientRegionKind.Passthrough);
			_notchRegions = Array.Empty<RectInt32>();
		}

		// The chrome follows the PRESENTER (from our switch, or AppWindow.Changed if anything else changes it) on a fresh
		// dispatcher turn. Idempotent: a second call for the same state only re-syncs the menu tick.
		private void AfterPresenterSwitch()
		{
			if (_isClosed)
			{
				return;
			}
			var on = AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen;
			ViewFullScreenItem.IsChecked = on;
			if (on == _isFullScreen)
			{
				return;
			}
			_isFullScreen = on;
			_logger.LogInformation("Full screen: chrome → {State}", on ? "full screen" : "windowed");
			if (on)
			{
				((Storyboard)RootGrid.Resources["FullScreenHintFlash"]).Begin();
				_probeCursorAtTop = false;
				_fullScreenProbe?.Start();
			}
			else
			{
				_fullScreenProbe?.Stop();
				// Back to the custom title bar exactly as the ctor set it (Microsoft's title-bar sample re-asserts this on the
				// way back from full screen). Both are idempotent.
				ExtendsContentIntoTitleBar = true;
				AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Collapsed;
				if (_wasMaximizedBeforeFullScreen)
				{
					DispatcherQueue.TryEnqueue(() => (AppWindow.Presenter as OverlappedPresenter)?.Maximize());
				}
			}
			CaptionKey.IsMaximized = (AppWindow.Presenter as OverlappedPresenter)?.State == OverlappedPresenterState.Maximized;
			UpdateCaptionBand();
			UpdateNotchInputRegions();
		}

		private void OnViewFullScreenClick(object sender, RoutedEventArgs e) => SetFullScreen(ViewFullScreenItem.IsChecked);

		private void OnFullScreenAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
		{
			SetFullScreen(!_isFullScreen);
			args.Handled = true;
		}

		// Esc leaves full screen only when nothing below wanted it (a text box, a flyout, a dialog handles it first).
		private void OnRootKeyDownForFullScreen(object sender, KeyRoutedEventArgs e)
		{
			if (e.Key == Windows.System.VirtualKey.Escape && _isFullScreen && !e.Handled)
			{
				SetFullScreen(false);
				e.Handled = true;
			}
		}

		// F11 / Escape pressed while the MAP had focus (map.js FULL-SCREEN keys; Escape only if the page didn't use it).
		private void OnFullScreenHostKey(string key)
		{
			if (key == "F11")
			{
				SetFullScreen(!_isFullScreen);
			}
			else if (key == "Escape" && _isFullScreen)
			{
				SetFullScreen(false);
			}
		}

		// The caption key's – and □ in full screen: □ leaves full screen; – leaves, then minimizes (FullScreenPresenter has no
		// Minimize — WindowsAppSDK #6355). Both queue behind the switch.
		private bool LeaveFullScreenForCaption(bool thenMinimize)
		{
			if (!_isFullScreen)
			{
				return false;
			}
			SetFullScreen(false);
			if (thenMinimize)
			{
				DispatcherQueue.TryEnqueue(() => DispatcherQueue.TryEnqueue(() =>
					(AppWindow.Presenter as OverlappedPresenter)?.Minimize()));
			}
			return true;
		}

		// ===== PROBE (stage 1): does the cursor reach the top edge, wherever it is (map, pane, rail)? =====
		private void OnFullScreenProbeTick(DispatcherQueueTimer sender, object args)
		{
			if (_isClosed || !_isFullScreen || !GetCursorPos(out var cursor))
			{
				return;
			}
			var top = AppWindow.Position.Y; // full screen: the window's top = the screen's top (physical px)
			var atTop = cursor.Y - top <= FullScreenProbeEdgePx && cursor.Y >= top;
			if (atTop != _probeCursorAtTop)
			{
				_probeCursorAtTop = atTop;
				_logger.LogInformation("Full screen probe: cursor {Where} the top edge at ({X},{Y}), window top {Top}",
					atTop ? "REACHED" : "left", cursor.X, cursor.Y, top);
			}
		}

		[StructLayout(LayoutKind.Sequential)]
		private struct CursorPoint
		{
			public int X;
			public int Y;
		}

		[DllImport("user32.dll", SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		private static extern bool GetCursorPos(out CursorPoint point);
	}
}
