using Microsoft.UI.Windowing;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;
using Anvil.Models;
using Anvil.Services;
using Anvil.Controls.Composites;
using Anvil.Controls.Primitives;   // BarEdgeStyle, for the bar's runtime edge swap
using Anvil.ViewModels;
using Anvil.Dialogs;
using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Storage.Pickers;   // the basemap folder picker (Map settings tab)
using Windows.Graphics;
using Windows.UI.ViewManagement;

namespace Anvil
{
	public sealed partial class MainWindow : Window, IMapView
	{
		public MapViewModel ViewModel { get; }

		/// <summary>DEV-ONLY site-sweep engine. Non-null only in Debug builds (see the ctor). Handed to the
		/// Settings window, whose Dev tab exists only in Debug.</summary>
		public SiteSweepViewModel? SweepVm { get; }

		/// <summary>DEV-ONLY velocity-dealias validation engine (fixed-corpus regression scorer). Non-null
		/// only in Debug builds. Handed to the Settings window's Debug-only Dev tab.</summary>
		public RadarValidationViewModel? ValidationVm { get; }

		/// <summary>DEV-ONLY load-time seeding run. Non-null only in Debug builds; handed to the Dev tab.</summary>
		public LoadTimeSeedingViewModel? SeedVm { get; }

		// NOTE: the bottom chrome is GONE (2026-11-04, the user's call): its map tools are the vertical MAP-TOOLS RAIL on the
		// left (MainWindow.xaml, in the TEMPORAL PANES layer), Pane Mode + Isolation are top-menu-bar items, and the floating
		// console sits SelectorGap above the window bottom (a XAML margin now — there is nothing to measure).

		// ===== THE MAP-TOOLS RAIL =====
		// On the window's left edge, or DOCKED to the left temporal pane's right edge while Now's or Past's pane shows.
		public Thickness MapToolsRailMargin(bool pastShown, bool nowShown) =>
			new(pastShown || nowShown ? TemporalPaneLayerLeftPaneWidth : 0, 0, 0, 0);

		// The left panes' declared width (Styles.xaml TemporalPaneWidth — read off the declared pane, like the old
		// AlignSideSections did, so the two can't disagree).
		private double TemporalPaneLayerLeftPaneWidth => TemporalPaneNowCast?.Width ?? 480;

		// EDGE-HATCH (bolt-on): the rail's hatch hangs OFF its map-side edge, like a pane's.
		public Thickness MapToolsRailHatchMargin(double width) => new(0, 0, -width, 0);

		public Visibility HiddenWhen(bool on) => on ? Visibility.Collapsed : Visibility.Visible;

		// ===== FULL SCREEN (the user's call, 2026-11-04) =====
		//
		//   windowed (maximized, as at launch)          F11 / View → Full screen
		//   ┌ menu bar ──────────────── – □ ✕ ┐   ──►   ▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁   ← the REVEAL STRIP (3 px)
		//   ├──┬──────────────────────────────┤         ├──┬──────────────────────────────┤   panes, rail, selectors run to
		//   │▤ │  map                         │         │▤ │  map           [hint ~3 s]    │   the screen's top; the bar
		//   └──┴──────────────────────────────┘         └──┴──────────────────────────────┘   slides up out of sight
		//
		// • The FullScreen presenter: no taskbar, no frame. NEVER REMEMBERED — the app always starts windowed (the ctor's
		//   Maximize); leaving restores maximized if it was. Exits: F11, Esc, View → Full screen, the caption key.
		// • AUTO-HIDE like the Windows taskbar: the pointer resting on the top-edge strip ~150 ms (RevealDelayMs) slides the
		//   bar DOWN, OVER the map (nothing reflows; it is z-raised over the panes while full screen). It slides away once
		//   the pointer has been off it ~500 ms — but never while one of its menus / the Isolation list is open, the place
		//   search has focus, or KEYBOARD focus is in it (IsTopBarBusy).
		// • KEYS: F11 toggles; Esc leaves (not while typing; with keyboard focus in the bar it first hands focus back to the
		//   map); a lone Alt press reveals the bar and focuses Atlas. A focused MAP swallows keys, so map.js forwards
		//   F11 / Esc / lone-Alt as "hostKey" (WebMessageRouter.HostKeyPressed → OnHostKey).
		// • While full screen there is no caption band (nothing to drag) — UpdateCaptionBand.
		private bool _isFullScreen;
		private bool _isTopBarRevealed = true; // windowed = always in place
		private bool _wasMaximizedBeforeFullScreen;
		private bool _pointerOverTopBar;
		private int _topBarAwayTicks;
		private bool _altAlone;
		private Microsoft.UI.Dispatching.DispatcherQueueTimer? _revealTimer;
		private Microsoft.UI.Dispatching.DispatcherQueueTimer? _hideTimer;
		private const int RevealDelayMs = 150;
		private const int HideCheckMs = 250;
		private const int HideAfterTicks = 2; // away ~500 ms
		private const double TopBarShadowClearance = 20; // the cast shadow hangs below the surface

		private void InitFullScreen()
		{
			_revealTimer = DispatcherQueue.CreateTimer();
			_revealTimer.Interval = TimeSpan.FromMilliseconds(RevealDelayMs);
			_revealTimer.IsRepeating = false;
			_revealTimer.Tick += (_, _) => { _pointerOverTopBar = true; RevealTopBar(); };
			_hideTimer = DispatcherQueue.CreateTimer();
			_hideTimer.Interval = TimeSpan.FromMilliseconds(HideCheckMs);
			_hideTimer.IsRepeating = true;
			_hideTimer.Tick += OnTopBarHideTick;
			_router.HostKeyPressed += OnHostKey;
			// handledEventsToo: a control that handles Alt must not hide it from the lone-Alt rule.
			RootGrid.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnRootKeyDown), true);
			RootGrid.AddHandler(UIElement.KeyUpEvent, new KeyEventHandler(OnRootKeyUp), true);
			((Storyboard)RootGrid.Resources["TopChromeSlideIn"]).Completed += (_, _) => SettleSlide((Storyboard)RootGrid.Resources["TopChromeSlideIn"], 0);
			((Storyboard)RootGrid.Resources["TopChromeSlideOut"]).Completed += (_, _) => SettleSlide((Storyboard)RootGrid.Resources["TopChromeSlideOut"], -TopBarHiddenOffset);
		}

		private double TopBarHiddenOffset => TopChrome.ActualHeight + TopBarShadowClearance;

		/// <summary>Enter or leave full screen (the View item, F11, Esc, the caption key). Never persisted.</summary>
		/// <remarks>
		/// ⚠️ CRASH FIX (2026-11-04, docs/bugs.md): switching the presenter straight from the F11 handler, with the caption /
		/// passthrough regions still set and AppWindow.Changed re-writing them mid-switch, FAIL-FASTED in
		/// Microsoft.UI.Windowing.dll (0xe0464645). Now: the switch runs QUEUED on the dispatcher (out of key / menu
		/// processing), the non-client regions are CLEARED before entering, and the chrome follow-up (ApplyFullScreenState)
		/// is queued too — never re-entered from inside AppWindow.Changed. Each step logs, so a repeat shows where it died.
		/// </remarks>
		private void SetFullScreen(bool on)
		{
			if (_isClosed || _isFullScreenSwitchQueued)
			{
				return;
			}
			ViewFullScreenItem.IsChecked = _isFullScreen; // the item flipped itself; the window's state answers
			if (on == _isFullScreen)
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
					if (on)
					{
						_wasMaximizedBeforeFullScreen =
							(AppWindow.Presenter as OverlappedPresenter)?.State == OverlappedPresenterState.Maximized;
						_logger.LogInformation("Full screen: entering (was maximized {Max})", _wasMaximizedBeforeFullScreen);
						_isEnteringFullScreen = true; // the region writers stand down from here on
						ClearNonClientRegions();
						AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
						_logger.LogInformation("Full screen: presenter set");
					}
					else
					{
						_logger.LogInformation("Full screen: leaving");
						AppWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
						if (_wasMaximizedBeforeFullScreen)
						{
							(AppWindow.Presenter as OverlappedPresenter)?.Maximize();
						}
						_logger.LogInformation("Full screen: presenter restored");
					}
				}
				catch (Exception ex)
				{
					_logger.LogError(ex, "Full screen: presenter switch failed");
				}
				finally
				{
					_isEnteringFullScreen = false;
				}
				QueueApplyFullScreenState();
			});
		}

		private bool _isFullScreenSwitchQueued;
		private bool _isEnteringFullScreen;

		// The chrome follows the presenter, but never from INSIDE a windowing callback (AppWindow.Changed or the switch
		// itself) — always on a fresh dispatcher turn.
		private void QueueApplyFullScreenState() => DispatcherQueue.TryEnqueue(ApplyFullScreenState);

		private void ClearNonClientRegions()
		{
			var source = InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
			source.ClearRegionRects(NonClientRegionKind.Caption);
			source.ClearRegionRects(NonClientRegionKind.Passthrough);
			_notchRegions = Array.Empty<RectInt32>();
		}

		// Follows the PRESENTER, so the chrome can never disagree with the window.
		private void ApplyFullScreenState()
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
			_logger.LogInformation("Full screen: chrome → {State}", on ? "full screen" : "windowed");
			_isFullScreen = on;
			_revealTimer?.Stop();
			Canvas.SetZIndex(TopChrome, on ? 10 : 0); // full screen: the revealed bar lies OVER the panes
			if (on)
			{
				HideTopBar(animate: false);
				((Storyboard)RootGrid.Resources["FullScreenHintFlash"]).Begin();
			}
			else
			{
				_hideTimer?.Stop();
				StopSlides();
				TopChromeSlide.Y = 0;
				_isTopBarRevealed = true;
				FullScreenRevealStrip.Visibility = Visibility.Collapsed;
			}
			ApplyPaneLayerMargin();
			PlacePaneSelectors();
			UpdateCaptionBand();
			UpdateNotchInputRegions();
		}

		private void RevealTopBar()
		{
			if (!_isFullScreen || _isTopBarRevealed)
			{
				return;
			}
			_isTopBarRevealed = true;
			_topBarAwayTicks = 0;
			FullScreenRevealStrip.Visibility = Visibility.Collapsed;
			BeginSlide((Storyboard)RootGrid.Resources["TopChromeSlideIn"], (Storyboard)RootGrid.Resources["TopChromeSlideOut"], 0);
			_hideTimer?.Start();
		}

		private void HideTopBar(bool animate)
		{
			_isTopBarRevealed = false;
			_hideTimer?.Stop();
			FullScreenRevealStrip.Visibility = Visibility.Visible;
			var slideIn = (Storyboard)RootGrid.Resources["TopChromeSlideIn"];
			var slideOut = (Storyboard)RootGrid.Resources["TopChromeSlideOut"];
			if (animate)
			{
				BeginSlide(slideOut, slideIn, -TopBarHiddenOffset);
			}
			else
			{
				StopSlides();
				TopChromeSlide.Y = -TopBarHiddenOffset;
			}
		}

		// Start one slide from wherever the bar is now (a reversal mid-slide carries on from the current spot).
		private void BeginSlide(Storyboard slide, Storyboard other, double to)
		{
			var y = TopChromeSlide.Y;
			other.Stop();
			slide.Stop();
			TopChromeSlide.Y = y;
			((DoubleAnimation)slide.Children[0]).To = to;
			slide.Begin();
		}

		// A finished slide hands its end value to the transform itself and lets go (a held animation would fight the next).
		private void SettleSlide(Storyboard slide, double y)
		{
			slide.Stop();
			TopChromeSlide.Y = y;
		}

		private void StopSlides()
		{
			((Storyboard)RootGrid.Resources["TopChromeSlideIn"]).Stop();
			((Storyboard)RootGrid.Resources["TopChromeSlideOut"]).Stop();
		}

		private void OnTopBarHideTick(object? sender, object e)
		{
			if (!_isFullScreen || !_isTopBarRevealed)
			{
				_hideTimer?.Stop();
				return;
			}
			if (_pointerOverTopBar || IsTopBarBusy())
			{
				_topBarAwayTicks = 0;
				return;
			}
			if (++_topBarAwayTicks >= HideAfterTicks)
			{
				HideTopBar(animate: true);
			}
		}

		// The bar stays down while it's being USED: a menu or the Isolation list open, the place search focused (typing,
		// its suggestions), or keyboard focus anywhere in it. Pointer focus left on a menu title doesn't count.
		private bool IsTopBarBusy()
		{
			if (ViewMenuButton.Flyout?.IsOpen == true || TemporalModeMenuButton.Flyout?.IsOpen == true
				|| PaneModeMenuButton.Flyout?.IsOpen == true || IsolationMenuPicker.IsDropDownOpen)
			{
				return true;
			}
			if (Content?.XamlRoot is { } root && FocusManager.GetFocusedElement(root) is DependencyObject focused
				&& IsInsideTopBar(focused))
			{
				return focused is TextBox || (focused is Control { FocusState: FocusState.Keyboard });
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

		private bool IsTextInputFocused() =>
			Content?.XamlRoot is { } root && FocusManager.GetFocusedElement(root) is TextBox;

		private void OnRevealStripPointerEntered(object sender, PointerRoutedEventArgs e) => _revealTimer?.Start();

		private void OnRevealStripPointerExited(object sender, PointerRoutedEventArgs e) => _revealTimer?.Stop();

		private void OnTopChromePointerEntered(object sender, PointerRoutedEventArgs e) => _pointerOverTopBar = true;

		private void OnTopChromePointerExited(object sender, PointerRoutedEventArgs e) => _pointerOverTopBar = false;

		// Tab / Alt landing keyboard focus in the hidden bar brings it down.
		private void OnTopChromeGotFocus(object sender, RoutedEventArgs e)
		{
			if (_isFullScreen && !_isTopBarRevealed && e.OriginalSource is Control { FocusState: FocusState.Keyboard })
			{
				RevealTopBar();
			}
		}

		private void OnViewFullScreenClick(object sender, RoutedEventArgs e) => SetFullScreen(ViewFullScreenItem.IsChecked);

		private void OnFullScreenAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
		{
			SetFullScreen(!_isFullScreen);
			args.Handled = true;
		}

		private void OnEscapeAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
		{
			if (!_isFullScreen || IsTextInputFocused())
			{
				return; // not ours: windowed, or Esc belongs to the text box
			}
			args.Handled = true;
			if (Content?.XamlRoot is { } root && FocusManager.GetFocusedElement(root) is DependencyObject focused
				&& IsInsideTopBar(focused))
			{
				MainMapWebView.Focus(FocusState.Programmatic); // first Esc: out of the bar (it then slides away)
				return;
			}
			SetFullScreen(false);
		}

		// The lone-Alt rule (Windows menus): Alt down then up with no other key between.
		private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
		{
			_altAlone = IsAltKey(e.Key) && !e.KeyStatus.WasKeyDown;
		}

		private void OnRootKeyUp(object sender, KeyRoutedEventArgs e)
		{
			if (IsAltKey(e.Key) && _altAlone)
			{
				OnLoneAlt();
			}
			_altAlone = false;
		}

		private static bool IsAltKey(Windows.System.VirtualKey key) =>
			key is Windows.System.VirtualKey.Menu or Windows.System.VirtualKey.LeftMenu or Windows.System.VirtualKey.RightMenu;

		// Full screen only: a lone Alt brings the bar down with keyboard focus on Atlas (keyboard focus keeps it down);
		// pressed again from inside the bar it hands focus back to the map, and the bar slides away.
		private void OnLoneAlt()
		{
			if (!_isFullScreen)
			{
				return;
			}
			if (Content?.XamlRoot is { } root && FocusManager.GetFocusedElement(root) is DependencyObject focused
				&& IsInsideTopBar(focused))
			{
				MainMapWebView.Focus(FocusState.Programmatic);
				return;
			}
			_pointerOverTopBar = false;
			RevealTopBar();
			AtlasMenuButton.Focus(FocusState.Keyboard);
		}

		// F11 / Escape / lone Alt pressed while the MAP had focus (map.js HOST KEYS).
		private void OnHostKey(string key)
		{
			switch (key)
			{
				case "F11":
					SetFullScreen(!_isFullScreen);
					break;
				case "Escape":
					if (_isFullScreen)
					{
						SetFullScreen(false);
					}
					break;
				case "Alt":
					OnLoneAlt();
					break;
			}
		}

		// ===== PANE MODE (top menu bar) =====
		// Greyed while the PastCast loading screen is up (Radar.AreControlsLocked) — Isolation too.
		public bool Free(bool locked) => !locked;

		// x:Bind can't compare against an enum literal, so the XAML passes the member's ordinal.
		public bool IsPaneLayout(PaneLayout layout, int ordinal) => (int)layout == ordinal;

		// ⚠️ Re-assert all three items after the VM answers: a same or refused pick changes no property, so the one-way
		// IsChecked would leave the clicked item flipped.
		private void OnPaneModeClick(object sender, RoutedEventArgs e)
		{
			if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse<PaneLayout>(tag, out var layout))
			{
				ViewModel.Radar.PaneLayout = layout;
			}
			var now = ViewModel.Radar.PaneLayout;
			PaneModeSingleItem.IsChecked = IsPaneLayout(now, 0);
			PaneModeDoubleItem.IsChecked = IsPaneLayout(now, 1);
			PaneModeQuadItem.IsChecked = IsPaneLayout(now, 2);
		}

		// ── SITE MENU ── the menu a map site key's chevron opens. Its items are DECLARED in MainWindow.xaml
		// (SiteMenu); RadarSiteMenu describes one open (labels, icons, enabled) and does what the items do.
		private readonly RadarSiteMenu _siteMenu;
		private RadarSiteMenuState? _siteMenuState; // the site the open menu is about

		// The page's client px are the WebView's DIPs (default zoom), so the point needs no conversion.
		private void OnRadarSiteMenuRequested(string id, double x, double y)
		{
			var state = _siteMenu.Describe(id);
			if (state is null)
			{
				return; // an unknown id does nothing
			}

			_siteMenuState = state;
			SiteMenuHeader.Text = state.Title;
			SiteMenuHeader.KeyboardAcceleratorTextOverride = state.Status;
			SiteMenuLoad.Text = state.LoadText;
			SiteMenuLoadIcon.Symbol = state.LoadSymbol;
			SiteMenuLoad.IsEnabled = state.CanLoad;
			SiteMenuLoad.KeyboardAcceleratorTextOverride = state.LoadReason;
			SiteMenuHome.Text = state.HomeText;
			SiteMenuFavorite.Text = state.FavoriteText;
			SiteMenuFavoriteIcon.Symbol = state.FavoriteSymbol;
			SiteMenu.ShowAt(MainMapWebView, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions
			{
				Position = new Windows.Foundation.Point(x, y),
				Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedLeft,
			});
		}

		private void OnSiteMenuLoad(object sender, RoutedEventArgs e)
		{
			if (_siteMenuState is { } s) _siteMenu.Load(s.Row);
		}

		private void OnSiteMenuAtlas(object sender, RoutedEventArgs e)
		{
			if (_siteMenuState is { } s) _siteMenu.OpenInAtlas(s.Row);
		}

		private void OnSiteMenuHome(object sender, RoutedEventArgs e)
		{
			if (_siteMenuState is { } s) _siteMenu.ToggleHome(s.Row);
		}

		private void OnSiteMenuFavorite(object sender, RoutedEventArgs e)
		{
			if (_siteMenuState is { } s) _siteMenu.ToggleFavorite(s.Row);
		}

		// The temporal panes (and the map-tools rail in their layer) run from the TOP BAR's foot to the window bottom.
		// FULL SCREEN: from the screen's top (the bar is hidden, and slides down OVER them when revealed).
		private void ApplyPaneLayerMargin() =>
			TemporalPaneLayer.Margin = new Thickness(0, _isFullScreen ? 0 : TopChrome.SurfaceHeight, 0, 0);

		// ===== TOP MENU BAR (2026-11-01) =====
		// Its height moves the temporal panes' top, the floating selectors (single / two across sit under its tab) and
		// the caption band (the bar IS the caption now).
		private void OnTopChromeSizeChanged(object sender, SizeChangedEventArgs e)
		{
			// FULL SCREEN: a hidden bar that grew (the search box's width, a menu title) stays fully out of sight.
			if (_isFullScreen && !_isTopBarRevealed)
			{
				HideTopBar(animate: false);
			}
			ApplyPaneLayerMargin();
			PlacePaneSelectors();
			UpdateCaptionBand();
			UpdateNotchInputRegions();
		}

		// View menu → the pane items (ToggleMenuFlyoutItem IsChecked is ONE-WAY to the VM's "open" tick; the item has
		// already flipped itself by Click, so it is re-asserted from the VM's answer — the bar keys' rule).
		private void OnViewPastPaneClick(object sender, RoutedEventArgs e) => TogglePaneFromMenu(TemporalMode.Past, ViewPastPaneItem);
		private void OnViewNowPaneClick(object sender, RoutedEventArgs e) => TogglePaneFromMenu(TemporalMode.Now, ViewNowPaneItem);
		private void OnViewForePaneClick(object sender, RoutedEventArgs e) => TogglePaneFromMenu(TemporalMode.Fore, ViewForePaneItem);

		private void TogglePaneFromMenu(TemporalMode mode, ToggleMenuFlyoutItem item)
		{
			ViewModel.TogglePane(mode);
			item.IsChecked = mode switch
			{
				TemporalMode.Past => ViewModel.IsPastPaneOpen,
				TemporalMode.Now => ViewModel.IsNowPaneOpen,
				_ => ViewModel.IsForePaneOpen,
			};
		}

		// Temporal Mode menu → toggle THAT mode only (the old bar keys' rule): IsChecked is ONE-WAY, so the item is
		// re-asserted from the VM's answer after it flipped itself.
		private void OnTemporalNowCastClick(object sender, RoutedEventArgs e) => ToggleModeFromMenu(TemporalMode.Now, TemporalNowCastItem);
		private void OnTemporalForeCastClick(object sender, RoutedEventArgs e) => ToggleModeFromMenu(TemporalMode.Fore, TemporalForeCastItem);
		private void OnTemporalPastCastClick(object sender, RoutedEventArgs e) => ToggleModeFromMenu(TemporalMode.Past, TemporalPastCastItem);

		private void ToggleModeFromMenu(TemporalMode mode, ToggleMenuFlyoutItem item)
		{
			ViewModel.ToggleTemporalMode(mode);
			item.IsChecked = mode switch
			{
				TemporalMode.Past => ViewModel.IsPastCast,
				TemporalMode.Now => ViewModel.IsNowCast,
				_ => ViewModel.IsForeCast,
			};
		}

		// x:Bind functions for the Temporal Mode menu (mock-up C).
		// The title names what runs: "Temporal Mode · Now + Fore", or just "Temporal Mode".
		public string TemporalModeTitle(bool now, bool fore, bool past)
		{
			var on = new List<string>(2);
			if (now) { on.Add("Now"); }
			if (fore) { on.Add("Fore"); }
			if (past) { on.Add("Past"); }
			return on.Count == 0 ? "Temporal Mode" : "Temporal Mode · " + string.Join(" + ", on);
		}

		// The side that is OFF dims while the other side runs (Past excludes Now + Fore).
		public double DimWhen(bool other) => other ? 0.45 : 1.0;
		public double DimWhenEither(bool a, bool b) => DimWhen(a || b);

		// A dimmed mode item reads as DISABLED at rest, so it lifts to full strength under the pointer or keyboard focus
		// and settles back to the rule's dim after. (Opacity is x:Bind OneWay; this local value lasts until the next
		// mode change re-binds it, which yields the same rule anyway.)
		private void OnModeItemLift(object sender, RoutedEventArgs e) => ((UIElement)sender).Opacity = 1.0;

		private void OnModeItemSettle(object sender, RoutedEventArgs e) =>
			((UIElement)sender).Opacity = sender == TemporalPastCastItem
				? DimWhenEither(ViewModel.IsNowCast, ViewModel.IsForeCast)
				: DimWhen(ViewModel.IsPastCast);

		// View's pane items: the shortcut while enabled; while disabled, what to do first (a disabled item shows no
		// tooltip in WinUI, so the hint sits in the shortcut column where it is always visible).
		public string PastPaneAccel(bool canPick) => canPick ? "Ctrl+1" : "Turn on PastCast first";
		public string NowPaneAccel(bool canPick) => canPick ? "Ctrl+2" : "Turn on NowCast first";
		public string ForePaneAccel(bool canPick) => canPick ? "Ctrl+3" : "Turn on ForeCast first";

		// The mode runs but View hid its pane — shown in the item's right column.
		public string PaneHiddenTag(bool hidden) => hidden ? "pane hidden" : "";

		// The greyed line under PastCast: what a click on the dimmed side turns off.
		public string TemporalModeHint(bool now, bool fore, bool past) =>
			past ? "NowCast or ForeCast turns PastCast off"
			: now || fore ? "Turns NowCast and ForeCast off"
			: "";

		public Visibility TemporalModeHintVisibility(bool now, bool fore, bool past) =>
			now || fore || past ? Visibility.Visible : Visibility.Collapsed;

		// ===== FLOATING PANE SELECTORS — where each sits in its pane's cell (the user's call, 2026-11-01) =====
		// SINGLE: centred, just under the top bar's tab. TWO ACROSS: against the divider on each side, same height.
		// QUAD: all four at the CROSSING, each in its pane's inner corner — the top row hangs its shadow UP (Edge Bottom).
		// In CODE because the top offset is the top bar's MEASURED height (no tab since 2026-11-04);
		// the elements are declared in XAML (Notch0…3), this only sets their alignment, margin and shadow edge.
		private const double SelectorGap = 8;

		// The top bar's PLACE SEARCH is as wide as the pane selector (the user's call, 2026-11-03) — and KEEPS that width
		// whether or not the selector shows (2026-11-04): a hidden selector is invisible but still laid out (IsNotchShown),
		// so it is measured from launch. Pane 0's selector in its FULL radar form: Single or two across (only Quad is
		// compact); while a ForeCast model draws it wears the model form, so the box keeps its last radar-form width.
		private void OnPaneSelectorSizeChanged(object sender, SizeChangedEventArgs e)
		{
			if (ViewModel.Radar.PaneLayout != PaneLayout.Quad && !ViewModel.ShortRange.IsDrawing && e.NewSize.Width > 0)
			{
				PlaceSearchBox.Width = e.NewSize.Width;
			}
		}

		private void PlacePaneSelectors()
		{
			var layout = ViewModel.Radar.PaneLayout;
			var top = (_isFullScreen ? 0 : TopChrome.ActualHeight) + SelectorGap; // full screen: the bar is hidden
			var notches = new[] { Notch0, Notch1, Notch2, Notch3 };
			for (var i = 0; i < notches.Length; i++)
			{
				var notch = notches[i];
				var cell = layout.CellOf(i);
				var leftColumn = cell.Column == 0;
				switch (layout)
				{
					case PaneLayout.Single:
						notch.HorizontalAlignment = HorizontalAlignment.Center;
						notch.VerticalAlignment = VerticalAlignment.Top;
						notch.Edge = BarEdge.Top;
						notch.Margin = new Thickness(0, top, 0, 0);
						break;
					case PaneLayout.TwoAcross:
						notch.HorizontalAlignment = leftColumn ? HorizontalAlignment.Right : HorizontalAlignment.Left;
						notch.VerticalAlignment = VerticalAlignment.Top;
						notch.Edge = BarEdge.Top;
						notch.Margin = leftColumn ? new Thickness(0, top, SelectorGap, 0) : new Thickness(SelectorGap, top, 0, 0);
						break;
					default: // Quad
						var topRow = cell.Row == 0;
						notch.HorizontalAlignment = leftColumn ? HorizontalAlignment.Right : HorizontalAlignment.Left;
						notch.VerticalAlignment = topRow ? VerticalAlignment.Bottom : VerticalAlignment.Top;
						notch.Edge = topRow ? BarEdge.Bottom : BarEdge.Top;
						notch.Margin = new Thickness(
							leftColumn ? 0 : SelectorGap, topRow ? 0 : SelectorGap,
							leftColumn ? SelectorGap : 0, topRow ? SelectorGap : 0);
						break;
				}
			}
		}

		// (AlignCentreSection is GONE, 2026-11-04: the console's top row it edge-aligned went when Inspect + rings/ruler moved
		// to the map-tools rail; the site picker is the console's TAB again, centred by layout alone. The tools strip's
		// AlignSideSections went with the strip.)

		// NOTE: DevVisibility is gone. It existed to collapse the dev bar key in Release; there is no dev key
		// any more — the dev tools are a tab of the Settings window, and SettingsWindow omits that tab from
		// its strip (and never constructs its body) in Release.

		// ===== Pane notches =====
		// The overlay grid in MainWindow.xaml has to line its cells up with the page's pane rects, so both
		// sides take the groove width from the same constant and the placement from PaneLayoutInfo.CellOf.
		// x:Bind functions rather than VM properties: this is view geometry, so it stays in the view and
		// Anvil.Core keeps no notion of grid cells.
		//
		// These four placement helpers are UNCHANGED from when this grid held the pane watermarks - the
		// notch sits in exactly the same cell the watermark did, just aligned to its top centre instead of
		// its top left.
		public GridLength PaneGutter => new(PaneLayoutInfo.GutterPx);

		public int PaneRow(PaneLayout layout, int index) => layout.CellOf(index).Row;
		public int PaneColumn(PaneLayout layout, int index) => layout.CellOf(index).Column;
		public int PaneRowSpan(PaneLayout layout, int index) => layout.CellOf(index).RowSpan;
		public int PaneColumnSpan(PaneLayout layout, int index) => layout.CellOf(index).ColumnSpan;

		/// <summary>
		/// A pane's selector shows while View → Pane selectors is on, its pane is up, AND a radar site is selected
		/// (Now and Past alike) — or a ForeCast model draws (the selector's read-only model form needs no site).
		///
		/// <para>⚠️ NO SITE, NO SELECTOR (the user's call, 2026-11-04, alongside the console's same rule). It used to
		/// show from launch, DORMANT (dimmed, controls off — PaneNotchContent.IsDormant) as "yours once you pick a
		/// site"; now the map is bare until a site is picked, and the selector arrives with its loop.</para>
		///
		/// <para>⚠️ HIDDEN = INVISIBLE, NOT COLLAPSED (2026-11-04): a hidden selector stays LAID OUT (Opacity 0, not
		/// hit-testable), so its width is always measured and the top bar's place search keeps it whether or not the selector
		/// shows (OnPaneSelectorSizeChanged). Only a pane that isn't in the layout collapses its selector (Visibility).</para>
		/// </summary>
		public bool IsNotchShown(bool selectorsVisible, bool hasSite, bool modelDrawing) =>
			selectorsVisible && (hasSite || modelDrawing);

		public double NotchOpacity(bool selectorsVisible, bool hasSite, bool modelDrawing) =>
			IsNotchShown(selectorsVisible, hasSite, modelDrawing) ? 1 : 0;

		/// <summary>A temporal pane shows while its pane flag is set (MapViewModel; never past its mode).</summary>
		public Visibility PaneVisibility(bool shown) => shown ? Visibility.Visible : Visibility.Collapsed;

		/// <summary>The floating console: View → Radar console on, AND a radar site selected (Now and Past alike, the
		/// user's call 2026-11-04) — or a ForeCast model playing in it, which needs no site.</summary>
		public Visibility ConsoleVisibility(bool consoleOn, bool hasSite, bool modelConsole) =>
			consoleOn && (hasSite || modelConsole) ? Visibility.Visible : Visibility.Collapsed;

		/// <summary>MODEL MODE (the bar's console cell): RadarControls goes INVISIBLE, never Collapsed, while ModelConsole
		/// plays over it — the tools tier's alignment keeps measuring it (see MainWindow.xaml at TimeModule). The argument is
		/// ShortRange.IsModelConsoleShown (drawing and NOT comparing with radar).</summary>
		public double RadarConsoleOpacity(bool modelConsole) => modelConsole ? 0 : 1;

		public bool IsRadarConsoleLive(bool modelConsole) => !modelConsole;

		// ⚠️ Visibility, not x:Load: x:Load in a Window's XAML generates a FindName call a Window doesn't have (CS1061).
		public Visibility ModelConsoleVisibility(bool modelConsole) => modelConsole ? Visibility.Visible : Visibility.Collapsed;

		/// <summary>COMPARE WITH RADAR: the two panes' corner labels show only while comparing.</summary>
		public Visibility CompareLabelVisibility(bool comparing) => comparing ? Visibility.Visible : Visibility.Collapsed;

		/// <summary>The radar pane's corner label: "KTLX · live radar" ("Live radar" before a site is picked).</summary>
		public string RadarPaneLabel(RadarOption? site) =>
			site?.Site is { } s ? $"{s.Id} · live radar" : "Live radar · pick a site";

		/// <summary>
		/// Whether the notches take their tighter form (no scale numbers under the ramp, shorter ramp).
		/// Quad only: a quad pane is half the window wide, and the full notch would take a real bite out of
		/// it. Two-across panes are still wide enough for the numbers.
		/// </summary>
		public bool NotchCompact(PaneLayout layout) => layout == PaneLayout.Quad;

		// ===== Pane notches vs the TITLE BAR =====
		// ⚠️ THE TOP-ROW NOTCHES SIT INSIDE THE WINDOW'S CAPTION. This window sets
		// ExtendsContentIntoTitleBar = true (so the map runs edge to edge) and never calls SetTitleBar, so
		// WinUI applies a DEFAULT drag region across the whole top of the window. That band is NON-CLIENT:
		// the system takes its input for dragging and double-click-to-maximize, and XAML controls inside it
		// are only partly reachable. It is why a notch in the top band behaved as if its dropdown were
		// dead - the flyout opened (IsOpen true) but the clicks that should have driven it were being
		// swallowed by the caption - while the BOTTOM-row notches in a quad, which sit below the band,
		// worked perfectly. That asymmetry is what identified it.
		//
		// The fix is to punch interactive holes in the caption: every visible notch is registered as a
		// PASSTHROUGH non-client region, which hands its rect back to XAML. Everything around the notches
		// stays draggable, so the window still behaves like a window.
		//
		// ⚠️ Rects are PHYSICAL pixels relative to the window, so they are scaled by RasterizationScale -
		// pass logical units and the holes land in the wrong place on any display that is not 100%.
		private RectInt32[] _notchRegions = Array.Empty<RectInt32>();

		// ⚠️ SET ON Closed, AND EVERY NOTCH-REGION PATH MUST CHECK IT. A closing window still runs one or
		// more layout passes as its content tears down, so LayoutUpdated fires AFTER the native window is
		// gone - and every member this method touches (Content, AppWindow) is a projection over that dead
		// object, so touching one throws COMException "The WinUI Desktop Window object has already been
		// closed". Unsubscribing in Closed is not enough on its own: a pass already queued still lands.
		// (It never reproduced under VS Stop, which kills the process instead of closing the window.)
		private bool _isClosed;

		// LayoutUpdated rather than SizeChanged: a notch also MOVES without resizing (the window resizes,
		// the pane layout changes, a notch is hidden). It fires often, so the work is four transforms and an
		// equality check, and SetRegionRects is only called when the rects actually change.
		private void OnPaneNotchLayerLayoutUpdated(object? sender, object e)
		{
			UpdateCaptionBand();
			UpdateNotchInputRegions();
		}

		// ===== CAPTION BAND = THE TOP MENU BAR (2026-11-01) =====
		// The system minimize/maximize/close stay HIDDEN (TitleBarHeightOption.Collapsed, set in the ctor): the top
		// bar draws its own (Primitives/WindowCaptionKey, at its right end), and OnCaption* below drive the presenter.
		//
		// The window's caption — drag, double-click to maximize/restore, Alt+Space, snap — is declared EXPLICITLY
		// here as a Caption region: the top bar's whole surface (its View button and caption key are Passthrough
		// holes, UpdateNotchInputRegions). Collapsing the title bar is not trusted to leave a default in place, so
		// the band is re-asserted on every layout pass and size change (only written when the system's copy differs).
		// ⚠️ TOP BAR HIDDEN = NO CAPTION BAND (accepted for now, the user's call): only the tab remains.
		private void UpdateCaptionBand()
		{
			// FULL SCREEN: no caption at all — a full-screen window can't be dragged; the regions were cleared on the way in
			// (ClearNonClientRegions) and nothing writes them until the window is back (crash fix, see SetFullScreen).
			if (_isClosed || _isFullScreen || _isEnteringFullScreen || Content?.XamlRoot is not { } root)
			{
				return;
			}

			var height = (int)Math.Ceiling(TopChrome.SurfaceHeight * root.RasterizationScale);
			var bands = height > 0
				? new[] { new RectInt32(0, 0, AppWindow.Size.Width, height) }
				: Array.Empty<RectInt32>();

			var source = InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
			if (SameRegions(source.GetRegionRects(NonClientRegionKind.Caption), bands))
			{
				return;
			}

			if (bands.Length == 0)
			{
				source.ClearRegionRects(NonClientRegionKind.Caption);
			}
			else
			{
				source.SetRegionRects(NonClientRegionKind.Caption, bands);
			}
		}

		// FULL SCREEN: minimize leaves full screen first (the FullScreen presenter can't minimize); □ just leaves it.
		private void OnCaptionMinimize(object? sender, EventArgs e)
		{
			if (_isFullScreen)
			{
				SetFullScreen(false); // queued — the minimize queues BEHIND it, onto the restored Overlapped presenter
				DispatcherQueue.TryEnqueue(() => (AppWindow.Presenter as OverlappedPresenter)?.Minimize());
				return;
			}
			(AppWindow.Presenter as OverlappedPresenter)?.Minimize();
		}

		private void OnCaptionMaximizeRestore(object? sender, EventArgs e)
		{
			if (_isFullScreen)
			{
				SetFullScreen(false);
				return;
			}
			if (AppWindow.Presenter is not OverlappedPresenter presenter)
			{
				return;
			}

			if (presenter.State == OverlappedPresenterState.Maximized)
			{
				presenter.Restore();
			}
			else
			{
				presenter.Maximize();
			}
		}

		private void OnCaptionClose(object? sender, EventArgs e) => Close();

		// The menu bar's Atlas and Settings items: each closes its window if open, else opens it (Atlas on Radar sites).
		private void OnAtlasMenuClick(object sender, RoutedEventArgs e) => ViewModel.ToggleAtlasFromBar();

		private void OnSettingsMenuClick(object sender, RoutedEventArgs e) =>
			ViewModel.IsSettingsWindowOpen = !ViewModel.IsSettingsWindowOpen;

		private void UpdateNotchInputRegions()
		{
			// FULL SCREEN: no caption, so no holes are needed — and none are written mid-switch (crash fix, SetFullScreen).
			if (_isClosed || _isFullScreen || _isEnteringFullScreen)
			{
				return;
			}

			if (Content?.XamlRoot is not { } root)
			{
				return;
			}

			var scale = root.RasterizationScale;

			// Keep clear of the caption buttons: a passthrough rect over them would break close/maximize.
			// The insets are already physical pixels.
			var captionLeft = AppWindow.TitleBar.LeftInset;
			var captionRight = AppWindow.Size.Width - AppWindow.TitleBar.RightInset;

			// The TOP BAR's controls sit IN the caption band (it is the bar), so they are the holes that matter now; the
			// tab, selectors and panes are listed too so nothing interactive can ever end up under a caption rect.
			var rects = new List<RectInt32>(PaneLayoutInfo.MaxPanes + 8);
			foreach (var notch in new FrameworkElement[]
				{ MenuBarMenus, PlaceSearchBox, CaptionKey, ConsoleIslandHost,
				  Notch0, Notch1, Notch2, Notch3, TemporalPanePastCast, TemporalPaneNowCast, TemporalPaneForeCast })
			{
				// A hidden pane selector is invisible but laid out (IsNotchShown) — not hit-testable, so it is no hole either.
				if (notch.Visibility != Visibility.Visible || !notch.IsHitTestVisible || notch.ActualWidth <= 0 || notch.ActualHeight <= 0)
				{
					continue;
				}

				var origin = notch.TransformToVisual(null).TransformPoint(new Point(0, 0));
				if (origin.Y + notch.ActualHeight <= 0)
				{
					continue; // FULL SCREEN: the hidden top bar's controls are slid above the window — no hole there
				}
				var left = (int)Math.Floor(origin.X * scale);
				var right = (int)Math.Ceiling((origin.X + notch.ActualWidth) * scale);
				left = Math.Max(left, (int)captionLeft);
				right = Math.Min(right, (int)captionRight);
				if (right <= left)
				{
					continue;
				}

				rects.Add(new RectInt32(
					left,
					(int)Math.Floor(origin.Y * scale),
					right - left,
					(int)Math.Ceiling(notch.ActualHeight * scale)));
			}

			var next = rects.ToArray();
			if (SameRegions(_notchRegions, next))
			{
				return;
			}

			_notchRegions = next;
			var source = InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
			if (next.Length == 0)
			{
				source.ClearRegionRects(NonClientRegionKind.Passthrough);
			}
			else
			{
				source.SetRegionRects(NonClientRegionKind.Passthrough, next);
			}
		}

		private static bool SameRegions(RectInt32[] a, RectInt32[] b)
		{
			if (a.Length != b.Length)
			{
				return false;
			}

			for (var i = 0; i < a.Length; i++)
			{
				if (a[i].X != b[i].X || a[i].Y != b[i].Y || a[i].Width != b[i].Width || a[i].Height != b[i].Height)
				{
					return false;
				}
			}

			return true;
		}

		// (The pane-layout key moved to MapControlsStrip as three 1/2/4 toggles.)

		// (The marker key's glyph + tooltip moved with it to MapControlsStrip.)
		// (The Location key's tooltip + click handler moved with the key to MapControlsStrip.)

		// (The Home key and the Atlas key's home/favorites flyout are gone: both became the tools tier's site
		// picker — MapControlsStrip. The Atlas key is a plain TwoWay latch now, with no handler.)

		// A temporal pane's body drew for the first time: the LAUNCH WINDOWS show the next restored pane on it.
		private void OnTemporalPaneShown(object? sender, EventArgs e)
		{
			ViewModel.OnTemporalPaneShown();
			if (sender is Controls.TemporalPanes.TemporalPane pane)
			{
				LaunchTiming.LogMoment(_logger, $"pane temporal.{pane.Mode} drawn");
			}
		}

		// Imports a .dow.json mobile-radar frame into the DOW library, for the Atlas's DOW events tab.
		// Here for the same reason as the folder picker below: the picker needs a window HWND, and the body
		// raising the request is a UserControl inside a UserControl.
		// ⚠️ The frame is COPIED into %LocalAppData%\Anvil\DowEvents rather than referenced where it sits —
		// the WebView fetches it, so it has to be same-origin under the mapped dowevents host.
		private async void OnImportDowEventRequested(object? sender, EventArgs e)
		{
			var picker = new FileOpenPicker
			{
				SuggestedStartLocation = PickerLocationId.ComputerFolder,
			};
			// .dow.json is a DOUBLE extension and the picker only matches the last one, so this filters to
			// .json and the import validates from there.
			picker.FileTypeFilter.Add(".json");
			WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));

			try
			{
				var file = await picker.PickSingleFileAsync();
				await ViewModel.Radar.Dow.ImportAsync(file?.Path); // null (cancelled) is ignored
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "DOW frame import failed");
			}
		}

		// LIVE MAP FILE SWITCH: the map in use (ISettingsService.MapDataFolder + MapDataFileName) changed — re-point the
		// mapdata host at its folder, then have the page swap the archive behind the styles' name (map.js setMapFile).
		// The Settings preview follows through WebViewEnvironment.MapDataChanged. A folder that doesn't exist is left
		// unmapped (the map draws blank, exactly as a missing file at launch would).
		private void OnMapFileSettingChanged(object? sender, PropertyChangedEventArgs e)
		{
			if (e.PropertyName is not (nameof(AppSettings.BasemapFilePath) or nameof(AppSettings.MapDataFolder))) return;
			// INLINE when already on the UI thread: Settings → Basemap's Delete lets go of the map in use and THEN deletes it.
			if (DispatcherQueue.HasThreadAccess) SwitchMapFile();
			else DispatcherQueue.TryEnqueue(SwitchMapFile);
		}

		private void SwitchMapFile()
		{
			var folder = _settingsService.MapDataFolder;
			var file = _settingsService.MapDataFileName;
			if (string.Equals(folder, WebViewEnvironment.LaunchMapDataFolder, StringComparison.OrdinalIgnoreCase)
				&& string.Equals(file, WebViewEnvironment.LaunchMapDataFile, StringComparison.OrdinalIgnoreCase)) return;
			try
			{
				var core = MainMapWebView.CoreWebView2;
				core.ClearVirtualHostNameToFolderMapping("mapdata");
				if (Directory.Exists(folder)) core.SetVirtualHostNameToFolderMapping("mapdata", folder, CoreWebView2HostResourceAccessKind.Allow);
				WebViewEnvironment.LaunchMapDataFolder = folder;
				WebViewEnvironment.LaunchMapDataFile = file;
				_ = _mapService.SetMapFileAsync(file);
				WebViewEnvironment.RaiseMapDataChanged();
				_logger.LogInformation("Map file switched live to {File} in {Folder}", file, folder);
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Live map file switch to {File} failed", file);
			}
		}

		// Picks the folder holding the offline basemap archive, for the Settings window's Map tab. Lives
		// here because a WinRT FolderPicker must be initialized with a window HWND, and the settings panel is
		// a UserControl hosted in a window rather than a window itself — the same reason the report dialogs
		// below are raised up to here. The new folder is drawn at once (OnMapFileSettingChanged).
		private async void OnBrowseMapDataFolderRequested(object? sender, EventArgs e)
		{
			var picker = new FolderPicker
			{
				SuggestedStartLocation = PickerLocationId.ComputerFolder,
			};
			picker.FileTypeFilter.Add("*"); // required: the picker throws on show with an empty filter list
			WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));

			try
			{
				var folder = await picker.PickSingleFolderAsync();
				ViewModel.SetMapDataFolder(folder?.Path); // null (cancelled) is ignored by the setter
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Basemap folder picker failed");
			}
		}

		// Opens the site-sweep results pop-up (Save / Close). Raised by the dev window on run completion
		// or its Report button.
		private async void OnSweepReportRequested(object? sender, SweepReport report)
		{
			var dialog = new SweepReportDialog(report, WinRT.Interop.WindowNative.GetWindowHandle(this))
			{
				XamlRoot = Content.XamlRoot,
			};
			await dialog.ShowAsync();
		}

		// Opens the dealias-validation results pop-up (Save / Close). Raised by the dev window on
		// run completion or its Report button.
		private async void OnValidationReportRequested(object? sender, RadarValidationReport report)
		{
			var dialog = new ValidationReportDialog(report, WinRT.Interop.WindowNative.GetWindowHandle(this))
			{
				XamlRoot = Content.XamlRoot,
			};
			await dialog.ShowAsync();
		}

		// Routes JS→C# WebView2 messages to the view models + diagnostics (owns the map-ready latch).
		private readonly WebMessageRouter _router;

		// Drives the map (JS command strings via IMapView). Kept so the host can push chrome/theme
		// concerns to the page (e.g. the OS accent for the radar-site halo) outside the view models.
		private readonly IMapService _mapService;

		// Watches for OS accent-color / light-dark changes so the radar-site status halo re-tints live,
		// the same way the OverlayBar's accent drop-shadow does. Field so it isn't garbage-collected.
		private readonly UISettings _uiSettings = new();

		// True once the page has posted map-ready; gates the accent push (the shim + page must exist).
		private bool _webReady;

		// SPC outlook data layer (fetch + cache of severe/fire-weather GeoJSON). Kept
		// here because MainWindow owns the WebView2 host mapping for its cache folder.
		private readonly ISpcOutlookService _spcOutlookService;

		// SPC watch-box data layer (fetch + cache of active watch GeoJSON). Same reason —
		// MainWindow owns the WebView2 host mapping for its cache folder.
		private readonly ISpcWatchService _spcWatchService;

		// Storm-based warning data layer (fetch + cache of active Tornado / Severe Thunderstorm warning
		// polygons). Same reason — MainWindow owns the WebView2 host mapping for its cache folder.
		private readonly IWarningService _warningService;

		// SPC storm-report data layer (fetch + cache of Tornado / Wind / Hail verification points). Same
		// reason — MainWindow owns the WebView2 host mapping for its cache folder.
		private readonly IStormReportService _stormReportService;

		// NWS damage surveys (DAT tornado polygons / tracks / points) — same reason, its cache folder.
		private readonly IDamageSurveyService _damageSurveyService;
		private readonly IStormCellService _stormCellService;
		private readonly IPastAlertService _pastAlertService;
		private readonly IMesoDiscussionService _discussionService;
		private readonly ITropicalService _tropicalService;
		private readonly IHrrrService _hrrrService;

		// Level II radar data layer (fetch + cache of .V06 volumes). Kept here because
		// MainWindow owns the WebView2 host mapping for its cache folder.
		private readonly ILevel2RadarService _radarService;

		// App settings (offline basemap folder, …). Read when mapping the "mapdata" WebView host.
		private readonly ISettingsService _settingsService;

		// Hosts each app-wide panel in its own OS window (multi-monitor). MainWindow registers each window
		// with it after construction (below).
		private readonly WindowManager _windows;

		/// <summary>Serilog-backed log for this window. Its one job today is the WebView2 death report —
		/// see <see cref="OnWebViewProcessFailed"/>.</summary>
		private readonly ILogger<MainWindow> _logger;

		public MainWindow(
			MapViewModel viewModel,
			MapService mapService,
			WebMessageRouter router,
			ISpcOutlookService spcOutlookService,
			ISpcWatchService spcWatchService,
			IWarningService warningService,
			IStormReportService stormReportService,
			IDamageSurveyService damageSurveyService,
			IStormCellService stormCellService,
			IPastAlertService pastAlertService,
			IMesoDiscussionService discussionService,
			ITropicalService tropicalService,
			IHrrrService hrrrService,
			ILevel2RadarService radarService,
			ISettingsService settingsService,
			WindowManager windows,
			ILogger<MainWindow> logger,
			ISavedEventLibrary savedEventLibrary,
			RadarSiteMenu siteMenu)
		{
			// The DI container (App.ConfigureServices) built the whole graph and injected it here; this
			// window is just the composition ROOT that wires the WebView-coupled bits the container can't.
			// ⚠️ ViewModel must be assigned BEFORE InitializeComponent — Maximize() below can make x:Bind
			// evaluate synchronously, and a null ViewModel then silently breaks every binding.
			ViewModel = viewModel;
			_mapService = mapService;
			_router = router;
			_spcOutlookService = spcOutlookService;
			_spcWatchService = spcWatchService;
			_warningService = warningService;
			_stormReportService = stormReportService;
			_damageSurveyService = damageSurveyService;
			_stormCellService = stormCellService;
			_pastAlertService = pastAlertService;
			_discussionService = discussionService;
			_tropicalService = tropicalService;
			_hrrrService = hrrrService;
			_radarService = radarService;
			_settingsService = settingsService;
			_windows = windows;
			_logger = logger;

			// MapService needs THIS window as its IMapView (the seam that runs JS). The container couldn't
			// pass it via ctor without a MainWindow↔MapService cycle, so attach now that both exist — well
			// before InitializeComponent / mapReady, so no map command can run against a null view.
			mapService.Attach(this);

			// Start the dedicated radar diagnostics for this run: a per-launch JSONL event stream +
			// a derived markdown report under a package-local Diagnostics/ folder (kept ~6 months under a
			// size cap; see RadarDiagnostics). This is the primary tool for chasing intermittent radar issues.
			Services.RadarDiagnostics.Init(
				System.IO.Path.Combine(_radarService.CacheDirectory, "Diagnostics"));

#if DEBUG
			// DEV-ONLY automated site sweep. Constructed only in Debug; the Settings window's Dev tab, which is
			// the only thing that reaches it, is omitted from the tab strip in Release. (The engine TYPE lives
			// in Anvil.Core and ships with it, but is never constructed here in Release.)
			SweepVm = new SiteSweepViewModel(ViewModel.Radar);

			// DEV-ONLY velocity-dealias regression harness (fixed-corpus scorer). Same Debug-only lifetime
			// as the sweep: driven through the map service (window.radarValidate) against the bundled corpus.
			ValidationVm = new RadarValidationViewModel(_mapService, new RadarCorpusProvider());

			// DEV-ONLY load-time seeding run: every built-in event × window × cold/warm into the (Debug-only) load-time log.
			SeedVm = new LoadTimeSeedingViewModel(ViewModel.Radar, savedEventLibrary, _radarService, _mapService, ViewModel.LoadTimes);
#endif

			// Push the OS theme accent to the radar-site status halo once the page is ready, and re-push
			// whenever the OS accent/theme changes — mirrors the OverlayBar's live-tinted accent shadow.
			_router.MapReady += OnMapReadyAsync;
			_uiSettings.ColorValuesChanged += OnColorValuesChanged;

			// A map site key's chevron → its WinUI menu (SiteMenu, declared in MainWindow.xaml), under the key.
			_siteMenu = siteMenu;
			_router.RadarSiteMenuRequested += OnRadarSiteMenuRequested;

			ExtendsContentIntoTitleBar = true;
			InitializeComponent();

			// The theme's BASE palette, applied to the whole XAML tree. This one line is what makes WinUI's
			// several hundred system brushes (text ranks, card strokes, focus rects, every stock control)
			// resolve to the same light/dark ground the theme's own surfaces in Controls/Styles.xaml are
			// authored against. Set straight after InitializeComponent, before the first render, so nothing
			// paints on the other palette first. A panel window that is ALREADY open needs the change pushed
			// to it (ApplyAppTheme); one opened later copies this window's ActualTheme itself.
			// ⚠️ This PINS the app to the chosen identity instead of following the OS. Following the OS is a
			// different feature — a "System" theme that resolves to the light or dark identity — and it is
			// not built; the two shipped themes are picked explicitly.
			ApplyAppTheme();

			// The identity can change at runtime, and the WinUI half of it lives out here: Core raises the
			// property, the view turns it into a palette. (The map half is pushed by the view model itself,
			// as one ApplyThemeAsync command.)
			ViewModel.PropertyChanged += (_, e) =>
			{
				if (e.PropertyName == nameof(MapViewModel.SelectedTheme)) ApplyAppTheme();
			};

			// FLOATING PANE SELECTORS: re-placed whenever the pane layout changes (and when the top bar does — its SizeChanged).
			ViewModel.Radar.PropertyChanged += (_, e) =>
			{
				if (e.PropertyName == nameof(RadarViewModel.PaneLayout)) PlacePaneSelectors();
			};
			PlacePaneSelectors();


			// Hand the caption band back to XAML wherever a pane notch sits in it (see the PANE NOTCHES vs
			// THE TITLE BAR block above). Hooked straight after InitializeComponent so the very first layout
			// pass registers the holes - otherwise the notch is dead until something else forces a relayout.
			PaneNotchLayer.LayoutUpdated += OnPaneNotchLayerLayoutUpdated;

			// FULL SCREEN: the reveal / hide timers, the keys (see the FULL SCREEN block). The app always STARTS windowed.
			InitFullScreen();

			// Hide the system caption buttons — they moved to the bar (see the CAPTION BAND block above).
			AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Collapsed;

			// App-wide windows: every panel that isn't the radar console or a temporal pane lives in its own native OS
			// window (multi-monitor). The manager watches the coordinator VM's open flags and opens/closes a window
			// to match; content is a fresh section instance bound to this same VM, rendered headerless (the
			// window caption is the chrome). Each flag is INDEPENDENT — any combination may be open at once.
			// The radar console (the floating ConsoleIsland) is deliberately NOT here.
			// ⚠️ The panels' usable band ends at the WINDOW BOTTOM: the bottom chrome is gone (2026-11-04) — the map tools
			// are the left rail — so nothing opaque sits across the window's foot any more.
			_windows.Initialize(this, ViewModel,
				availableBottom: () => ((FrameworkElement)Content).ActualHeight,
				monitorMode: () => ViewModel.MonitorMode);
			_windows.WindowShown += (_, id) => LaunchTiming.LogMoment(_logger, $"window {id} drawn");
			// LAUNCH TIMING: the home radar's first frame, once (later frames are ordinary use).
			EventHandler? onFirstFrame = null;
			onFirstFrame = (_, _) =>
			{
				ViewModel.Radar.FirstFrameShown -= onFirstFrame;
				LaunchTiming.LogMoment(_logger, "home radar first frame");
			};
			ViewModel.Radar.FirstFrameShown += onFirstFrame;
			// ONE settings window with a tab strip — it absorbed the former App Settings, Map Controls and Dev
			// Tools windows, which is why the bar's right cluster is down to Panes / Atlas / Settings.
			// ⚠️ Every panel's size AND spot come from its anchor (WindowManager's PLACEMENT block) — there
			// are no per-window sizes, and nothing sizes to its content any more.
			// The dev VMs are handed over unconditionally; they are null in Release, where SettingsWindow
			// omits the dev tab from its strip and never constructs its body.
			_windows.Register(
				id: "settings",
				isOpen: () => ViewModel.IsSettingsWindowOpen,
				close: () => ViewModel.IsSettingsWindowOpen = false,
				buildContent: () =>
				{
					var settings = new Controls.Windows.SettingsWindow
					{
						ViewModel = ViewModel,
						SweepVm = SweepVm,
						ValidationVm = ValidationVm,
						SeedVm = SeedVm,
					};
					// Wired per-instance (the window's content is rebuilt each time it opens) so a finished
					// dev run still pops its results dialog.
					settings.SweepReportRequested += OnSweepReportRequested;
					settings.ValidationReportRequested += OnValidationReportRequested;
					settings.BrowseMapDataFolderRequested += OnBrowseMapDataFolderRequested;
					return settings;
				},
				title: "Settings", anchor: WindowAnchor.Center,
				keepAboveOwner: () => ViewModel.IsSettingsWindowOnTop,
				isLocked: () => ViewModel.IsSettingsWindowLocked,
				customChrome: true);
			_windows.Register(
				id: "sites",
				isOpen: () => ViewModel.IsAtlasOpen,
				close: () => ViewModel.IsAtlasOpen = false,
				buildContent: () =>
				{
					var atlas = new Controls.Windows.AtlasWindow { ViewModel = ViewModel };
					atlas.ImportDowEventRequested += OnImportDowEventRequested; // the DOW events tab
					return atlas;
				},
				title: "Anvil Atlas", anchor: WindowAnchor.Center,
				keepAboveOwner: () => ViewModel.IsAtlasOnTop,
				isLocked: () => ViewModel.IsAtlasLocked,
				customChrome: true);
			// (The three temporal panes are NOT windows any more: they are overlays declared in MainWindow.xaml's
			// TEMPORAL PANES block, shown by their pane flags. Only their first-draw signal is wired here.)
			TemporalPanePastCast.Shown += OnTemporalPaneShown;
			TemporalPaneNowCast.Shown += OnTemporalPaneShown;
			TemporalPaneForeCast.Shown += OnTemporalPaneShown;
			// SPC's forecast discussion, opened from either outlook section's "Discussion." button (one window:
			// Past excludes Fore, so it reads whichever outlook mode runs). Center, like Settings and the Atlas.
			_windows.Register(
				id: "outlookDiscussion",
				isOpen: () => ViewModel.IsOutlookDiscussionOpen,
				close: () => ViewModel.IsOutlookDiscussionOpen = false,
				buildContent: () => new Controls.Windows.OutlookDiscussionWindow { ViewModel = ViewModel },
				title: "Outlook Discussion", anchor: WindowAnchor.Center,
				keepAboveOwner: () => ViewModel.IsOutlookDiscussionOnTop,
				isLocked: () => ViewModel.IsOutlookDiscussionLocked,
				customChrome: true);
			// SPC MDs + WPC MPDs to read, opened from the Now/Past "Mesoscale discussions" section or a click on a
			// discussion's area on the map (Discussions.ReaderRequested). Center, like the Outlook Discussion.
			_windows.Register(
				id: "mesoDiscussion",
				isOpen: () => ViewModel.IsMesoDiscussionOpen,
				close: () => ViewModel.IsMesoDiscussionOpen = false,
				buildContent: () => new Controls.Windows.MesoDiscussionWindow { ViewModel = ViewModel },
				title: "Mesoscale Discussions", anchor: WindowAnchor.Center,
				keepAboveOwner: () => ViewModel.IsMesoDiscussionOnTop,
				isLocked: () => ViewModel.IsMesoDiscussionLocked,
				customChrome: true);
			_windows.Register(
				id: "pipeline",
				isOpen: () => ViewModel.IsPipelineConsoleOpen,
				close: () => ViewModel.IsPipelineConsoleOpen = false,
				buildContent: () => new Controls.Windows.PipelineConsoleWindow { ViewModel = ViewModel },
				title: "Pipeline Console", anchor: WindowAnchor.Center,
				keepAboveOwner: () => ViewModel.IsPipelineConsoleOnTop, // user-toggled via the pin in the console
				isLocked: () => ViewModel.IsPipelineConsoleLocked,
				customChrome: true); // extend content into the title bar so the dark surface replaces the caption
			// (The dev tools no longer register a window of their own — they are the Settings window's
			// Debug-only Dev tab, registered above with everything else.)

			// Start maximized.
			(AppWindow.Presenter as OverlappedPresenter)?.Maximize();

			// The Atlas's site-hours clock PAUSES while the main window is minimized. Read the presenter state on
			// EVERY change rather than trusting one flag — SetMinimized is idempotent, so the extra calls are free.
			// The same read drives the bar's caption key (Maximize ⇄ Restore) and re-asserts the caption band.
			AppWindow.Changed += (_, args) =>
			{
				if (_isClosed) return;
				// FULL SCREEN follows the presenter however it changed — QUEUED, never re-entered from this callback (crash fix).
				if (args.DidPresenterChange) QueueApplyFullScreenState();
				var state = (AppWindow.Presenter as OverlappedPresenter)?.State;
				ViewModel.SiteUsage.SetMinimized(state == OverlappedPresenterState.Minimized);
				CaptionKey.IsMaximized = state == OverlappedPresenterState.Maximized;
				UpdateCaptionBand();
			};
			CaptionKey.IsMaximized =
				(AppWindow.Presenter as OverlappedPresenter)?.State == OverlappedPresenterState.Maximized;

			_ = InitializeMapAsync();

			// Start the SPC outlook + watch background refresh loops (each owned by its own subsystem VM):
			// the app stays usable offline from the existing cache while fresh data downloads, then
			// keeps refreshing on a timer so a long-running session doesn't sit on stale data.
			// The live-update source FIRST, so the feeds' first cycles can already find it connected (Settings → Live Data).
			ViewModel.LiveFeed.Start();
			ViewModel.Outlook.StartBackgroundRefresh();
			ViewModel.Watches.StartBackgroundRefresh();
			ViewModel.Warnings.StartBackgroundRefresh();
			ViewModel.StormReports.StartBackgroundRefresh();
			ViewModel.StormCells.StartBackgroundRefresh();
			ViewModel.Discussions.StartBackgroundRefresh();
			ViewModel.Tropical.StartBackgroundRefresh();
			ViewModel.ShortRange.StartBackgroundRefresh();

			// Write a final flush + report on close so the run's last events aren't lost between
			// the ~2 s background flushes. Also latch _isClosed and drop the layout hook FIRST - the
			// teardown still runs layout passes, and the notch-region work touches the native window
			// (see _isClosed).
			Closed += (_, _) =>
			{
				_isClosed = true;
				PaneNotchLayer.LayoutUpdated -= OnPaneNotchLayerLayoutUpdated;

				// Stop every app-lifetime background loop BEFORE the flush. They resume on this window's
				// DispatcherQueue and end in property notifications / map pushes, so one still ticking after
				// this point is notifying a XAML tree that is being destroyed. See MapViewModel.Shutdown.
				ViewModel.Shutdown();

				Services.RadarDiagnostics.FlushAll();
			};

			LaunchTiming.Mark("main window");
		}

		private async Task InitializeMapAsync()
		{
			// The page loads the currently-selected basemap directly (no flash to the
			// default, then re-style). The view model's default is Data Viz Black.
			// ⚠️ The full URL, not a file name: an imported style is on a different virtual host, and the
			// model is the one place that knows which (see MapStyle.Url).
			var styleUrl = ViewModel.SelectedStyle?.Url ?? "https://mapassets/style.json";
			var main = ViewModel.MainRegion;
			await InitializeWebViewAsync(MainMapWebView, BuildMapUrl(main, main?.Zoom ?? 4, styleUrl, ViewModel.StateIso.IsConusIsolated,
				ViewModel.IsOnlineTilesActive, ViewModel.OnlineTilesUrl, ViewModel.SelectedTheme.Id, _settingsService.MapDataFileName));
		}

		// Builds the page URL for the map: framed at the given center/zoom, on the given
		// basemap. The page posts 'mapReady' once its MapLibre map's 'load' fires.
		// `conus` = the launch CONUS-mask default, so the page can apply the mask on first load (before it
		// reveals) instead of waiting for the host round-trip — otherwise the world basemap flashes first.
		// `onlineTiles`/`tilesUrl` = the basemap tile source, passed the same way and for the same reason as
		// `conus`: the page builds its FIRST map on the right source instead of loading the offline basemap
		// and then re-styling onto the online one (a visible reload of every tile at launch).
		// The WinUI half of a theme change: the palette every system brush resolves against, pushed to this
		// window's tree, to any panel window already open, and to the WebView's pre-paint ground.
		// ⚠️ THE PAGE'S OWN CHROME IS NOT HERE. That travels as one IMapService.ApplyThemeAsync command from
		// the view model, because the page needs the palette set before it re-adds its layers. Splitting the
		// push across two owners is deliberate — each writes only what it can address.
		private void ApplyAppTheme()
		{
			var theme = ViewModel.SelectedTheme;

			if (Content is FrameworkElement themeRoot)
			{
				themeRoot.RequestedTheme = theme.Base == ThemeBase.Dark ? ElementTheme.Dark : ElementTheme.Light;
			}

			// ⚠️ A panel window is its own top-level XAML tree and takes its palette once, when it opens —
			// nothing propagates a later change, so open ones have to be told.
			_windows.ApplyOwnerTheme();

			// Only ever seen in the gap before the page paints, and on a dead renderer — but on a dead
			// renderer it is the whole window, so it should at least be the right color.
			if (MainMapWebView?.CoreWebView2 is not null)
			{
				MainMapWebView.DefaultBackgroundColor = ParseGroundColor(theme.GroundColor);
			}
		}

		// "#RRGGBB" (the form the map theme sheets use, so the two halves of the ground color read alike) → a Color.
		// ⚠️ Falls back to BLACK rather than throwing: this runs on the WebView bootstrap, and a malformed
		// value should cost a slightly-wrong flash frame, never the map.
		private static Windows.UI.Color ParseGroundColor(string hex)
		{
			if (!string.IsNullOrWhiteSpace(hex) && hex.TrimStart('#').Length == 6
				&& int.TryParse(hex.TrimStart('#'), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
			{
				return Microsoft.UI.ColorHelper.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
			}

			return Microsoft.UI.Colors.Black;
		}

		// `themeId` = the chrome palette, passed for exactly the same reason as `conus` and the tile source:
		// --anvil-ground IS the page's background, so a post-ready push would flash the other theme's ground
		// before the map paints.
		// `mapFile` = the offline map file in the mapdata folder (map setup's in-use file, or usa_full.pmtiles). Every bundled
		// style names usa_full.pmtiles; the page serves this file under that name (map.js, "mapfile").
		private static string BuildMapUrl(MapRegion? region, double zoom, string styleUrl, bool conus,
			bool onlineTiles, string tilesUrl, string themeId, string mapFile)
		{
			var lng = region?.Longitude ?? -95.5;
			var lat = region?.Latitude ?? 37.0;
			return "https://mapassets/map.html" +
				"?interactive=true" +
				$"&style={Uri.EscapeDataString(styleUrl)}" +
				$"&lng={lng.ToString(CultureInfo.InvariantCulture)}" +
				$"&lat={lat.ToString(CultureInfo.InvariantCulture)}" +
				$"&zoom={zoom.ToString(CultureInfo.InvariantCulture)}" +
				$"&conus={(conus ? "true" : "false")}" +
				$"&tiles={(onlineTiles ? "online" : "offline")}" +
				$"&tilesUrl={Uri.EscapeDataString(tilesUrl ?? "")}" +
				$"&theme={Uri.EscapeDataString(themeId ?? "")}" +
				$"&mapfile={Uri.EscapeDataString(mapFile ?? "")}"
#if DEBUG
				// DEV-ONLY: arms the pan frame-time probe (Assets/Map/js/perf-probe.js). map.js imports
				// that module only when this param is present, so a Release page never fetches it and
				// pays nothing. It is a URL param for the same reason `theme` is — the page has to know
				// before it builds its first map, and JS has no #if DEBUG of its own.
				+ "&perf=1"
#endif
				;
		}

		private async Task InitializeWebViewAsync(WebView2 webView, string url)
		{
			// The theme's ground, so there's no flash before the page (and MapLibre) paint.
			// ⚠️ This is ALSO what a DEAD renderer paints — see OnWebViewProcessFailed.
			// ⚠️ The page paints the same color from --anvil-ground in styles/themes/. C# can't read the page's
			// CSS, so the value is written in both places and AppTheme.GroundColor is the C# half.
			webView.DefaultBackgroundColor = ParseGroundColor(ViewModel.SelectedTheme.GroundColor);

			// ⚠️ The SHARED environment (forced sRGB, see WebViewEnvironment) — the Settings preview runs in it too,
			// and a second WebView2 with different options fails to create.
			await webView.EnsureCoreWebView2Async(await WebViewEnvironment.GetAsync());
			LaunchTiming.Mark("browser");

			// Drop a cached copy of the page's files from an earlier build before anything loads (see the remarks).
			await WebViewEnvironment.EnsureFreshMapAssetsAsync(webView.CoreWebView2);

			// The WebView2 death report. Subscribed FIRST, before any host mapping or navigation, so a
			// failure during startup is caught too.
			webView.CoreWebView2.ProcessFailed += OnWebViewProcessFailed;

			// The DOW library is a per-user folder under %LocalAppData% that the app IMPORTS into, so it does
			// not exist on a fresh install - create it before the host mapping below points at it.
			// (It used to live inside the read-only package, which is exactly why importing was impossible.)
			try { Directory.CreateDirectory(DowEventProvider.EventsDirectory); } catch { /* mapping just resolves to nothing */ }

			// Map each virtual host → local folder so the page can fetch everything offline, same-origin:
			//   mapassets  → bundled MapLibre style/glyphs/sprites/libraries
			//   mapdata    → the user-configured (external, ~29 GB) basemap PMTiles folder
			//   spcoutlooks/spcwatches/warnings/stormreports/damagesurveys/stormcells/pastalerts/discussions/radarlevel2 → the services' on-disk caches
			//   dowevents  → the user's imported DOW (mobile-radar) frame library (%LocalAppData%)
			// Services own their cache folders; MainWindow owns the WebView2 mappings.
			var hostFolders = new (string Host, string Folder)[]
			{
				("mapassets", Path.Combine(AppContext.BaseDirectory, "Assets", "Map")),
				("mapdata", _settingsService.MapDataFolder),
				(SpcOutlookService.CacheHostName, _spcOutlookService.CacheDirectory),
				(SpcWatchService.CacheHostName, _spcWatchService.CacheDirectory),
				(WarningService.CacheHostName, _warningService.CacheDirectory),
				(StormReportService.CacheHostName, _stormReportService.CacheDirectory),
				(DamageSurveyService.CacheHostName, _damageSurveyService.CacheDirectory),
				(StormCellService.CacheHostName, _stormCellService.CacheDirectory),
				(PastAlertService.CacheHostName, _pastAlertService.CacheDirectory),
				(MesoDiscussionService.CacheHostName, _discussionService.CacheDirectory),
				(TropicalService.CacheHostName, _tropicalService.CacheDirectory),
				(HrrrService.CacheHostName, _hrrrService.CacheDirectory),
				(Level2RadarService.CacheHostName, _radarService.CacheDirectory),
				(DowEventProvider.HostName, DowEventProvider.EventsDirectory),
			};
			WebViewEnvironment.LaunchMapDataFolder = _settingsService.MapDataFolder; // the preview maps the same one
			WebViewEnvironment.LaunchMapDataFile = _settingsService.MapDataFileName; // …and reads the same file
			foreach (var (host, folder) in hostFolders)
			{
				webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
					host, folder, CoreWebView2HostResourceAccessKind.Allow);
			}
			// From here a change of the map IN USE (Use, a finished download, a delete, the Map tab's folder) is drawn live.
			_settingsService.Settings.PropertyChanged += OnMapFileSettingChanged;

#if DEBUG
			// DEV-ONLY: serve the fixed velocity-dealias corpus (Assets/RadarCorpus, Debug-only Content) so
			// window.radarValidate can fetch each volume same-origin. Not mapped in Release (the folder
			// isn't bundled there and the harness is unreachable).
			webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
				Services.RadarCorpusProvider.CorpusHostName, Services.RadarCorpusProvider.CorpusDirectory,
				CoreWebView2HostResourceAccessKind.Allow);
#endif

			webView.CoreWebView2.WebMessageReceived += _router.OnWebMessageReceived;
#if DEBUG
			// DEV-ONLY UI-thread heartbeat (perf.ui) — is input to the map delayed on THIS thread? Grep perf.ui.
			UiThreadProbe.Start(webView.CoreWebView2);
#endif
			webView.Source = new Uri(url);
			LaunchTiming.Mark("navigate");
		}

		/// <summary>
		/// The WebView2 process died. PURE INSTRUMENTATION — it reports and does not recover.
		/// </summary>
		/// <remarks>
		/// ⚠️ THIS IS THE "ENTIRE MAP WENT BLACK" EVENT, and until it existed the failure was invisible.
		/// The renderer dying leaves the app itself perfectly healthy — C# keeps running, the bar and the pane
		/// notches keep drawing, the refresh loops keep logging — while the WebView paints its
		/// <c>DefaultBackgroundColor</c>, which is deliberately BLACK (the no-white-flash choice above). So the
		/// symptom is a black rectangle where the map was, with working WinUI chrome around it, and nothing
		/// anywhere said why.
		/// <para>Observed 2026-09-03 on a 26-frame PastCast replay: the radar JSONL's JS event stream stopped
		/// dead mid-<c>fullPrefetch</c> while Serilog kept writing for another ~10 s. Diagnosing that took
		/// cross-referencing two logs by timestamp; this handler makes it one line. Suspected cause is renderer
		/// memory (a fully-built 26-frame legacy-volume loop retains ~2.1 GB of gate geometry — see
		/// <c>docs/app-notes.md</c>), which <see cref="CoreWebView2ProcessFailedKind"/> +
		/// <c>ExitCode</c> will confirm or refute the next time it happens.</para>
		/// <para>⚠️ Logged at CRITICAL: the app is alive but its entire map surface is gone, which is as bad as
		/// a crash from where the user sits. It ALSO writes into the radar diagnostics stream, because that is
		/// the timeline the JS events stop in — the death marker belongs in the same file as the last frame
		/// event, not only in the Serilog file.</para>
		/// <para>⚠️ It does NOT reload the WebView. <c>ProcessFailed</c> IS the hook a recovery would hang off,
		/// but recovering means re-running the whole bootstrap (host mappings, navigation, then every overlay
		/// re-added through <c>reAddAll</c>, panes and radar loop included) and deciding what happens to the
		/// loop that was on screen. That is a feature, not instrumentation; keep them separate so this handler
		/// can never be the thing that breaks.</para>
		/// </remarks>
		private void OnWebViewProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
		{
			// Everything here is best-effort and defensive: we are already in a failure path, and a throw from
			// a diagnostic handler would replace a legible report with an unhandled exception.
			var kind = "unknown";
			var reason = "unknown";
			var exitCode = -1;
			var process = string.Empty;
			var frames = 0;
			try { kind = e.ProcessFailedKind.ToString(); } catch { /* best effort */ }
			try { reason = e.Reason.ToString(); } catch { /* not populated for every kind */ }
			try { exitCode = e.ExitCode; } catch { /* not populated for every kind */ }
			try { process = e.ProcessDescription ?? string.Empty; } catch { /* not populated for every kind */ }
			try { frames = e.FrameInfosForFailedProcess?.Count ?? 0; } catch { /* frame-only kinds */ }

			_logger.LogCritical(
				"WebView2 process failed: kind={Kind} reason={Reason} exitCode={ExitCode} process={Process} frames={Frames}. " +
				"The map surface is now blank (DefaultBackgroundColor) while the app keeps running; it will NOT self-recover.",
				kind, reason, exitCode, process, frames);

			// Same event, into the radar stream — the JS events simply STOP at the moment of death, so this is
			// the line that explains the end of that timeline.
			Services.RadarDiagnostics.Log("app", "webview.processfailed",
				("kind", kind), ("reason", reason), ("exitCode", exitCode),
				("process", process), ("frames", frames));
			Services.RadarDiagnostics.FlushAll(); // the renderer is gone; don't wait on the ~2 s flush timer
		}

		/// <summary>
		/// IMapView seam: the ONLY place that touches WebView2 / ExecuteScriptAsync.
		/// Valid only once the map's CoreWebView2 has initialized, and only until the window closes.
		/// </summary>
		/// <remarks>
		/// ⚠️ The _isClosed check is the SECOND half of the shutdown story (MapViewModel.Shutdown is the
		/// first): cancelling a loop stops the NEXT cycle, but a cycle already in flight still runs to its
		/// end, and its end is usually a map push landing here. CoreWebView2 is NOT null at that point — it
		/// is non-null and dead — so the guard above cannot catch it. Same reasoning as the notch-region
		/// latch: after Closed, every WebView2 member is a projection over a torn-down object.
		/// </remarks>
		public async Task<string> RunScriptAsync(string javaScript)
		{
			if (_isClosed || MainMapWebView.CoreWebView2 is null)
			{
				return string.Empty;
			}

			return await MainMapWebView.CoreWebView2.ExecuteScriptAsync(javaScript);
		}

		// Map-ready: the page + its window.setRadarSiteAccent shim now exist, so push the accent.
		private async Task OnMapReadyAsync()
		{
			_webReady = true;
			await PushRadarSiteAccentAsync();
		}

		// OS accent/theme changed (fires on a background thread) — re-push on the UI thread so the
		// radar-site halo tracks the system accent live, like the OverlayBar's accent drop-shadow.
		private void OnColorValuesChanged(UISettings sender, object args) =>
			DispatcherQueue?.TryEnqueue(() => { if (_webReady) _ = PushRadarSiteAccentAsync(); });

		// Pushes the current theme-aware accent to the page as the "available" site-marker halo color.
		private Task PushRadarSiteAccentAsync()
		{
			var (border, glow) = RadarSiteAccentCss();
			return _mapService.SetRadarSiteAccentAsync(border, glow);
		}

		// The theme-aware accent read LIVE from UISettings (matching OverlayBar.AccentShadowColor): the
		// lightened accent variant on dark, the darkened one on light, so the ring reads on either
		// backdrop. Returns a CSS hex for the ring border + a soft rgba for its glow.
		private (string border, string glow) RadarSiteAccentCss()
		{
			var theme = (Content as FrameworkElement)?.ActualTheme ?? ElementTheme.Dark;
			var type = theme == ElementTheme.Light ? UIColorType.AccentDark1 : UIColorType.AccentLight2;
			var c = _uiSettings.GetColorValue(type);
			var border = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
			var glow = string.Format(CultureInfo.InvariantCulture, "rgba({0},{1},{2},0.55)", c.R, c.G, c.B);
			return (border, glow);
		}
	}
}
