// ===== FULL-SCREEN (bolt-on, 2026-11-04 retry) — grep "FULL-SCREEN (bolt-on)" for every hook =====
//
//   windowed (maximized, as at launch)          F11 / View → Full screen
//   ┌ menu bar ──────────────── – □ ✕ ┐   ──►   ▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔   ← the cursor at the top edge
//   ├──┬──────────────────────────────┤         ├──┬──────────────────────────────┤     ~150 ms slides the bar DOWN,
//   │▤ │  map                         │         │▤ │  map        [hint ~3 s]       │     OVER the map; ~600 ms off it
//   └──┴──────────────────────────────┘         └──┴──────────────────────────────┘     (+8 px) slides it away
//                                               panes, rail, selectors run to the screen's top in full screen
//
// Microsoft's documented pattern throughout (and the user's research brief):
// • Enter = AppWindow.SetPresenter(FullScreen); exit = SetPresenter(DEFAULT) — the window's ORIGINAL presenter ("Manage app
//   windows") — then Maximize again if it was (queued — microsoft-ui-xaml #10439). NEVER remembered: always starts windowed.
// • NO non-client region writes during the switch (the first attempt fail-fasted in Microsoft.UI.Windowing.dll when
//   AppWindow.Changed rewrote them mid-switch — docs/bugs.md U11): cleared first, the writers stand down while
//   _isSwitchingPresenter, AfterPresenterSwitch re-writes them on a fresh turn. In full screen: NO caption (no dragging),
//   the WHOLE window passthrough (ApplyFullScreenRegions) — the brief's "don't register drag regions in full screen".
// • AUTO-HIDE (stage 2 — stage 1's probe proved the trigger over map + pane): ONE timer (50 ms, full screen only) reads the
//   REAL cursor (GetCursorPos). A hover strip can't do this: the WebView, the non-client band and the system's top edge all
//   ate its pointer events (the first attempt). The bar stays down while it's in use (IsTopBarBusy) and hides when the
//   app is deactivated. The slide is the bar's implicit TranslationTransition (XAML) on UIElement.Translation; with
//   Windows' "Animation effects" off it is dropped, so the bar just appears / disappears.
// • KEYS: F11 = a root KeyboardAccelerator (placement Hidden — else an "F11" tooltip pill floats over the map). Esc = the
//   root's BUBBLING KeyDown, so a text box / flyout / dialog that handles Esc keeps it; it backs out ONE LEVEL (an open menu
//   closes → focus in the bar goes back to the map and the bar hides → full screen ends). F10 (the Windows menu-bar key) =
//   reveal + focus Atlas; tabbing into the hidden bar reveals it too. A focused MAP swallows keys (WebView2), so map.js
//   forwards F11 / F10 / Escape as "hostKey" (WebMessageRouter.HostKeyPressed → OnFullScreenHostKey).
//
// BACK OUT = delete this file + every block tagged FULL-SCREEN (bolt-on) in MainWindow.xaml(.cs), WebMessageRouter.cs,
// map.js and Primitives/SectionedComboBox.xaml.cs.
using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
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

		private DispatcherQueueTimer? _topBarWatch;
		private Vector3Transition? _topBarTransition; // the XAML-declared slide, kept to restore after "animations off"
		private bool _isTopBarRevealed = true;        // windowed = always in place
		private int _cursorAtTopMs;
		private int _cursorAwayMs;

		private const int TopBarWatchMs = 50;
		private const int TopEdgePx = 2;              // physical px from the screen's top that count as "at the edge"
		private const int RevealAfterMs = 150;
		private const int HideAfterMs = 600;
		private const double HideMarginDip = 8;       // hysteresis below the bar before it counts as "away"
		private const double TopBarShadowClearance = 20; // the bar's cast shadow hangs below its surface

		private void InitFullScreen()
		{
			_router.HostKeyPressed += OnFullScreenHostKey;
			RootGrid.KeyDown += OnRootKeyDownForFullScreen; // bubbling: only keys nothing below handled
			_topBarTransition = TopChrome.TranslationTransition;
			_topBarWatch = DispatcherQueue.CreateTimer();
			_topBarWatch.Interval = TimeSpan.FromMilliseconds(TopBarWatchMs);
			_topBarWatch.IsRepeating = true;
			_topBarWatch.Tick += OnTopBarWatchTick;
			Activated += (_, e) =>
			{
				// Switching to another app (Alt+Tab, a click elsewhere) puts the bar away, like the taskbar.
				if (_isFullScreen && e.WindowActivationState == WindowActivationState.Deactivated)
				{
					HideTopBar();
				}
			};
		}

		// The panes' + selectors' top in full screen: the screen's top (the bar slides OVER them). Read by
		// ApplyPaneLayerMargin / PlacePaneSelectors (tagged hooks).
		private bool IsTopBarOverlaid => _isFullScreen;

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

		// FULL SCREEN's non-client layout: NO caption (a full-screen window isn't dragged), the WHOLE window passthrough — so
		// no default drag band can sit over the top of the panes / map / the revealed bar. Only written when the size changed.
		private void ApplyFullScreenRegions()
		{
			var whole = new[] { new RectInt32(0, 0, AppWindow.Size.Width, AppWindow.Size.Height) };
			var source = InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
			if (source.GetRegionRects(NonClientRegionKind.Caption).Length > 0)
			{
				source.ClearRegionRects(NonClientRegionKind.Caption);
			}
			if (!SameRegions(_notchRegions, whole))
			{
				_notchRegions = whole;
				source.SetRegionRects(NonClientRegionKind.Passthrough, whole);
			}
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
			Canvas.SetZIndex(TopChrome, on ? 10 : 0); // full screen: the revealed bar lies OVER the panes
			if (on)
			{
				ApplyAnimationSetting();
				HideTopBar();
				((Storyboard)RootGrid.Resources["FullScreenHintFlash"]).Begin();
				_cursorAtTopMs = 0;
				_topBarWatch?.Start();
			}
			else
			{
				_topBarWatch?.Stop();
				_isTopBarRevealed = true;
				TopChrome.Translation = Vector3.Zero;
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
			ApplyPaneLayerMargin();
			PlacePaneSelectors();
			UpdateCaptionBand();
			UpdateNotchInputRegions();
		}

		// Windows' "Animation effects" off → no slide: the bar just appears / disappears.
		private void ApplyAnimationSetting() =>
			TopChrome.TranslationTransition = new Windows.UI.ViewManagement.UISettings().AnimationsEnabled ? _topBarTransition : null;

		private void RevealTopBar()
		{
			if (!_isFullScreen || _isTopBarRevealed)
			{
				return;
			}
			_isTopBarRevealed = true;
			_cursorAwayMs = 0;
			TopChrome.Translation = Vector3.Zero;
		}

		private void HideTopBar()
		{
			if (!_isFullScreen)
			{
				return;
			}
			_isTopBarRevealed = false;
			_cursorAtTopMs = 0;
			TopChrome.Translation = new Vector3(0, -(float)(TopChrome.ActualHeight + TopBarShadowClearance), 0);
		}

		// A hidden bar whose height changed stays fully out of sight (tagged hook in OnTopChromeSizeChanged).
		private void KeepHiddenTopBarHidden()
		{
			if (_isFullScreen && !_isTopBarRevealed)
			{
				HideTopBar();
			}
		}

		// ONE timer while full screen: at the edge long enough → reveal; away from the bar long enough (and not in use) → hide.
		private void OnTopBarWatchTick(DispatcherQueueTimer sender, object args)
		{
			if (_isClosed || !_isFullScreen || !GetCursorPos(out var cursor) || Content?.XamlRoot is not { } root)
			{
				return;
			}
			var top = AppWindow.Position.Y; // full screen: the window's top = the screen's top (physical px)
			if (!_isTopBarRevealed)
			{
				var atTop = cursor.Y >= top && cursor.Y - top <= TopEdgePx;
				_cursorAtTopMs = atTop ? _cursorAtTopMs + TopBarWatchMs : 0;
				if (_cursorAtTopMs >= RevealAfterMs)
				{
					RevealTopBar();
				}
				return;
			}
			var yDip = (cursor.Y - top) / root.RasterizationScale;
			var overBar = yDip <= TopChrome.ActualHeight + HideMarginDip;
			if (overBar || IsTopBarBusy())
			{
				_cursorAwayMs = 0;
				return;
			}
			_cursorAwayMs += TopBarWatchMs;
			if (_cursorAwayMs >= HideAfterMs)
			{
				HideTopBar();
			}
		}

		// The bar stays down while it's being USED: a menu or the Isolation list open, the place search focused (typing, its
		// suggestions), or keyboard focus anywhere in it. Pointer focus left on a menu title doesn't count. ⚠️ Explicit checks,
		// not GetOpenPopupsForXamlRoot: these flyouts are WINDOWED (ShouldConstrainToRootBounds=False).
		private bool IsTopBarBusy()
		{
			if (AtlasMenuButton.Flyout?.IsOpen == true || SettingsMenuButton.Flyout?.IsOpen == true
				|| ViewMenuButton.Flyout?.IsOpen == true || TemporalModeMenuButton.Flyout?.IsOpen == true
				|| PaneModeMenuButton.Flyout?.IsOpen == true || IsolationMenuPicker.IsDropDownOpen)
			{
				return true;
			}
			if (Content?.XamlRoot is { } root && FocusManager.GetFocusedElement(root) is DependencyObject focused
				&& IsInsideTopBar(focused))
			{
				return focused is TextBox || focused is Control { FocusState: FocusState.Keyboard };
			}
			return false;
		}

		private bool IsInsideTopBar(DependencyObject element)
		{
			for (var node = element; node is not null; node = VisualTreeHelper.GetParent(node))
			{
				if (ReferenceEquals(node, TopChrome))
				{
					return true;
				}
			}
			return false;
		}

		// Tab landing keyboard focus in the hidden bar brings it down (tagged GotFocus hook on TopChrome).
		private void OnTopChromeGotFocus(object sender, RoutedEventArgs e)
		{
			if (_isFullScreen && !_isTopBarRevealed && e.OriginalSource is Control { FocusState: FocusState.Keyboard })
			{
				RevealTopBar();
			}
		}

		// F10 — the Windows menu-bar key: in full screen, reveal the bar with keyboard focus on Atlas.
		private void RevealTopBarFromKeyboard()
		{
			if (!_isFullScreen)
			{
				return;
			}
			RevealTopBar();
			AtlasMenuButton.Focus(FocusState.Keyboard);
		}

		private void OnViewFullScreenClick(object sender, RoutedEventArgs e) => SetFullScreen(ViewFullScreenItem.IsChecked);

		private void OnFullScreenAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
		{
			SetFullScreen(!_isFullScreen);
			args.Handled = true;
		}

		private void OnRevealTopBarAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
		{
			if (_isFullScreen)
			{
				RevealTopBarFromKeyboard();
				args.Handled = true;
			}
		}

		// Esc backs out ONE LEVEL at a time (the Windows menu-bar convention — Notepad, Explorer):
		//   1. a menu / the Isolation list is open → it closes itself (the flyout handles that Esc; it never reaches here);
		//   2. focus is IN the bar (a menu title after F10 / Tab / a closed menu, or the search box) → focus goes back to the
		//      map and the bar slides away;
		//   3. otherwise → leave full screen.
		// Only keys nothing below handled reach this bubbling handler (a dialog or another control's Esc wins).
		private void OnRootKeyDownForFullScreen(object sender, KeyRoutedEventArgs e)
		{
			if (e.Key != Windows.System.VirtualKey.Escape || !_isFullScreen || e.Handled)
			{
				return;
			}
			e.Handled = true;
			if (Content?.XamlRoot is { } root && FocusManager.GetFocusedElement(root) is DependencyObject focused
				&& IsInsideTopBar(focused))
			{
				MainMapWebView.Focus(FocusState.Programmatic);
				HideTopBar();
				return;
			}
			SetFullScreen(false);
		}

		// F11 / F10 / Escape pressed while the MAP had focus (map.js FULL-SCREEN keys; Escape only if the page didn't use it).
		private void OnFullScreenHostKey(string key)
		{
			switch (key)
			{
				case "F11":
					SetFullScreen(!_isFullScreen);
					break;
				case "F10":
					RevealTopBarFromKeyboard();
					break;
				case "Escape":
					if (_isFullScreen)
					{
						SetFullScreen(false);
					}
					break;
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
