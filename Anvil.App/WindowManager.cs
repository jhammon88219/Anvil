using System;
using System.Collections.Generic;
using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Anvil.Models;
using static Anvil.NativeWindowInterop;

namespace Anvil
{
	// ============================================================================================
	// APP-WIDE WINDOWS (removable feature — see Windows/ + the manager.Register calls in MainWindow).
	//
	// Every app-wide panel (Timeframe, Settings, Radar Atlas and the Pipeline Console) lives in its OWN
	// top-level OS window, not docked in MainWindow, so a multi-monitor user can park control panels on a
	// second screen. The radar console (per-pane, Row 2) is deliberately NOT one of these; it stays in the
	// main window.
	//
	// ⚠️ THE PANEL COUNT KEEPS FALLING, on purpose. Settings absorbed App Settings + Map Controls + Dev Tools
	// as TABS; Timeframe then absorbed Past Event + Live Radar + SPC Outlooks the same way (so their three
	// mode keys could go back to being plain toggles — see MapViewModel's temporal region). A new GROUP of
	// controls is a tab of an existing panel far more often than it is a new window here.
	//
	// MODEL — a panel IS a window. There is no docked state and no in-window copy: each window carries a
	// single IsOpen bool on the coordinator VM, and this manager watches that state and reconciles IsOpen →
	// a live Window. The flags are INDEPENDENT — no one-at-a-time grouping, so any combination may be open at
	// once. Content is a fresh instance of the section control bound to the shared singleton VM, hosted
	// headerless (the window's own content supplies the title; the native caption supplies the buttons).
	//
	// ⚠️ CHROME POLICY — uniform, owned HERE (in OpenWindow), not per-registration: every panel gets a
	// MINIMIZE + CLOSE caption (maximize off, so Windows greys its box) and a taskbar/Alt-Tab entry. See the
	// comments in OpenWindow for the full reasoning before changing any of it.
	//
	// ⚠️ PIN = "ABOVE ANVIL", NOT TOPMOST. A pinned panel is a Win32 OWNED window of the main window
	// (NativeWindowInterop.SetOwner): it always stacks above Anvil and moves through the z-order WITH it, so
	// another app covers both, and minimizing Anvil takes pinned panels with it. It used to be
	// OverlappedPresenter.IsAlwaysOnTop, which floated panels over every app and the desktop — wrong for a
	// single-monitor user. Unpinned = un-owned, an ordinary window that can fall behind Anvil.
	// IsAlwaysOnTop is never set. (A future multi-monitor mode may want different rules; not built.)
	//
	// ⚠️ PLACEMENT — every panel opens in a FIXED SPOT measured off the MAIN WINDOW'S client area (never the
	// monitor), so the spots travel with the main window to whatever screen it is on:
	//
	//   ┌─ main window ───────────────────────────────────────────────────────────┐
	//   │┌──────┐            ┌────────────────────────────────┐          ┌──────┐│  ← 5 px margins
	//   ││ Left │            │ Center — ≈16:9, sized as if    │          │Right ││
	//   ││ 480  │            │ BOTH edge strips were occupied │          │ 480  ││
	//   ││      │            │ (so it never depends on what   │          │      ││
	//   ││ full │            │  else happens to be open)      │          │ full ││
	//   ││height│            └────────────────────────────────┘          │height││
	//   │└──────┘                                                         └──────┘│  ← 5 px above…
	//   │═══════════════════ map tools tier (or the bar, if hidden) ══════════════│  …the bottom chrome
	//   └──────────────────────────────────────────────────────────────────────────┘
	//
	// ⚠️ MEASURED AT OPEN, and always to the designated spot — a window reopened after being dragged comes back
	// home, and nothing reflows when other panels open or chrome hides. Overlap is allowed.
	// ⚠️ …AND RE-MEASURED when the MAIN WINDOW resizes, maximizes, restores or moves — for LOCKED panels only (an
	// unlocked one is the user's to place). See FOLLOW below (2026-10-07).
	// ⚠️ The centre window has a FLOOR (CenterMinWidth × CenterMinHeight): below it, it stops honouring the
	// strips and overlaps them, still centred. An edge window never shrinks below 480 except to fit the
	// main window itself.
	// ⚠️ Margins are to the VISIBLE frame — Windows 11's invisible resize border is measured and added back
	// (NativeWindowInterop.PlaceVisibleFrame), or every 5 px gap would really be ~12.
	//
	// ⚠️ LOCK — every panel has a title-bar lock (LockToggle) beside its pin, DEFAULT LOCKED. Locked refuses
	// BOTH moving and resizing, enforced in a window subclass at WM_WINDOWPOSCHANGING — the one choke point
	// that also catches Aero snap, Win+Arrow and the Alt+Space Move/Size commands, which a click-blocking
	// overlay would not. The resize-edge hit tests are masked too so a locked frame doesn't offer the arrows.
	//
	// ⚠️ MONITOR MODE — everything above is the SINGLE-monitor design (MapViewModel.MonitorMode; only Single is
	// built). The manager reads the mode at the three points where Multi would differ, each marked
	// "MONITOR MODE (multi: TODO)" and each running Single's rule for Multi today:
	//   1. PIN          (ApplyPinned)          — Single: pinned = owned by the main window.
	//   2. PLACEMENT    (TryGetReferenceArea)  — Single: measured off the main window's client area.
	//   3. CHROME       (OpenWindow)           — Single: minimize + close, taskbar button, never topmost.
	// Building Multi = fill in those three branches (and whatever new ones it finds), then enable the mode.
	// ============================================================================================

	/// <summary>Where a panel window opens relative to the main window. See the PLACEMENT block above.</summary>
	public enum WindowAnchor
	{
		/// <summary>Flush left, 480 wide, full height above the bottom chrome (PastCast, NowCast).</summary>
		Left,
		/// <summary>Flush right, 480 wide, full height above the bottom chrome (ForeCast).</summary>
		Right,
		/// <summary>Centred, ≈16:9, sized between the two edge strips (Settings, Radar Atlas, Pipeline Console).</summary>
		Center,
	}

	/// <summary>
	/// Hosts each app-wide panel in its own <see cref="Window"/>, keeping the window's existence in sync with
	/// that panel's IsOpen VM state. Register a panel with <see cref="Register"/>; the window opens when
	/// IsOpen becomes true and closes when it becomes false (and the OS-caption Close flips IsOpen back off).
	/// </summary>
	public sealed class WindowManager
	{
		// ----- Placement geometry, in LOGICAL px (scaled by the owner's DPI at open) -----
		private const double Margin = 5;
		private const double EdgeWidth = 480;
		private const double CenterMinWidth = 520;
		private const double CenterMinHeight = 400;

		private sealed class Registration
		{
			public required string Id;
			public required Func<bool> IsOpen;                    // feature showing → window should exist
			public required Action Close;                        // set IsOpen=false (the OS-caption Close path)
			public required Func<FrameworkElement> BuildContent; // a fresh section instance bound to the shared VM
			public required string Title;
			public WindowAnchor Anchor;
			public Func<bool> KeepAboveOwner = () => false; // owned by the main window (evaluated live so a pin toggle can flip it)
			public Func<bool> IsLocked = () => false;    // no move/resize (read live by the subclass, per message)
			public bool CustomChrome; // extend content into the title bar so the dark surface replaces the caption
		}

		// One open window's subclass state. The delegate is held HERE so the GC can't collect it while Win32
		// still calls through it; it is removed at WM_NCDESTROY.
		private sealed class FrameLock
		{
			public required Func<bool> IsLocked;
			public bool Placing; // our own placement calls pass through even while locked
			public bool WasIconic; // minimized as of the last WM_WINDOWPOSCHANGED — marks the RESTORE move
			public SubclassProc? Proc;
		}

		private readonly Dictionary<string, Registration> _regs = new();
		private readonly Dictionary<string, Window> _windows = new();
		// Keyed by INSTANCE, not id: a panel closed and reopened quickly can have its new window's lock attached
		// before the old HWND's WM_NCDESTROY runs, and removing by id would drop the new window's delegate.
		private readonly HashSet<FrameLock> _locks = new();
		// Each OPEN panel's HWND + its lock state, by id — what the FOLLOW pass re-places (see FollowOwner).
		private readonly Dictionary<string, (IntPtr Hwnd, FrameLock Lock)> _frames = new();
		private bool _followQueued;
		// Ids we are closing ourselves (the flag went false elsewhere), so the Closed handler doesn't mistake
		// it for the user clicking the OS caption's Close and re-fire the Close action.
		private readonly HashSet<string> _closingProgrammatically = new();

		private readonly Microsoft.Extensions.Logging.ILogger<WindowManager> _logger;

		public WindowManager(Microsoft.Extensions.Logging.ILogger<WindowManager> logger) => _logger = logger;

		/// <summary>A panel window drew its first frame after opening (its id). See OPEN TIMING.</summary>
		public event EventHandler<string>? WindowShown;

		private Window? _owner;
		private bool _ownerClosed; // the app is closing — never hand focus back to the main window then
		private DispatcherQueue? _dispatcher;
		private Func<double> _availableBottom = () => double.PositiveInfinity;
		private Func<MonitorMode> _monitorMode = () => MonitorMode.Single;

		/// <summary>
		/// Wire the manager to the owner window + the coordinator VM whose PropertyChanged drives reconciles.
		/// Call once, on the UI thread, after the owner exists.
		/// </summary>
		/// <param name="availableBottom">The bottom of the space panels may use, in LOGICAL px from the top of
		/// the owner's content — the top edge of whatever bottom chrome is showing. Read at each open.</param>
		/// <param name="monitorMode">The effective monitor mode, read live at each MONITOR MODE decision point.</param>
		public void Initialize(Window owner, INotifyPropertyChanged coordinator, Func<double> availableBottom,
			Func<MonitorMode> monitorMode)
		{
			_owner = owner;
			_dispatcher = owner.DispatcherQueue;
			_availableBottom = availableBottom;
			_monitorMode = monitorMode;
			coordinator.PropertyChanged += (_, _) => RequestReconcile();
			owner.Closed += (_, _) => { _ownerClosed = true; CloseAll(); }; // don't leak panel windows when the app closes
			// The main window resized, maximized, restored or moved → the locked panels follow (see FOLLOW).
			owner.AppWindow.Changed += (_, e) => { if (e.DidSizeChange || e.DidPositionChange) RequestFollow(); };
			// …and again once XAML has laid the new size out: the panels' bottom bound (_availableBottom, the tools
			// tier's top) is a LAYOUT measurement, and Changed can fire before layout catches up.
			if (owner.Content is FrameworkElement root) root.SizeChanged += (_, _) => RequestFollow();
		}

		// ===== FOLLOW — the locked panels go where their anchor says, after the main window changes =====
		// Restoring the main window from maximized (the bar's caption key) left every panel at its MAXIMIZED spot,
		// laid over the smaller main window (app note, 2026-10-06). Now a LOCKED panel is re-placed to its anchor,
		// measured off the main window as it is now. ⚠️ UNLOCKED panels are left alone: unlocking is how you say
		// "I'll place this one" — and panels are locked by default, so the common case follows.
		// ⚠️ Size/position changes of the MAIN WINDOW only: panels still don't reflow when another panel opens or the
		// chrome hides (the PLACEMENT block). A minimized panel, or a minimized main window, is skipped.
		// Coalesced to one pass per dispatcher turn: a live drag-resize fires Changed many times a second.
		private void RequestFollow()
		{
			if (_dispatcher is null || _followQueued || _frames.Count == 0) return;
			_followQueued = true;
			_dispatcher.TryEnqueue(FollowOwner);
		}

		private void FollowOwner()
		{
			_followQueued = false;
			if (_owner is null || _ownerClosed) return;
			if (IsIconic(WinRT.Interop.WindowNative.GetWindowHandle(_owner))) return;
			double scale = (_owner.Content as FrameworkElement)?.XamlRoot?.RasterizationScale ?? 1.0;

			foreach (var (id, frame) in _frames)
			{
				if (!_regs.TryGetValue(id, out var reg) || !reg.IsLocked() || IsIconic(frame.Hwnd)) continue;
				if (ComputePlacement(reg.Anchor, scale) is not { } target) continue;
				frame.Lock.Placing = true; // our own move — the lock lets it through
				PlaceVisibleFrame(frame.Hwnd, target.X, target.Y, target.W, target.H);
				frame.Lock.Placing = false;
			}
		}

		/// <summary>
		/// Register a panel. <paramref name="isOpen"/> reads the panel's VM state, <paramref name="close"/> turns
		/// the feature off (used when the user closes the window via its caption), and <paramref name="buildContent"/>
		/// makes a fresh section instance (bound to the shared VM, rendered headerless). <paramref name="anchor"/>
		/// decides where it opens AND how big — there are no per-window sizes.
		/// </summary>
		public void Register(
			string id,
			Func<bool> isOpen,
			Action close,
			Func<FrameworkElement> buildContent,
			string title,
			WindowAnchor anchor,
			Func<bool>? keepAboveOwner = null,
			Func<bool>? isLocked = null,
			bool customChrome = false)
		{
			_regs[id] = new Registration
			{
				Id = id,
				IsOpen = isOpen,
				Close = close,
				BuildContent = buildContent,
				Title = title,
				Anchor = anchor,
				KeepAboveOwner = keepAboveOwner ?? (() => false),
				IsLocked = isLocked ?? (() => false),
				CustomChrome = customChrome,
			};
		}

		/// <summary>
		/// Re-evaluate every panel's IsOpen against its window. Driven by the coordinator's PropertyChanged
		/// — every window's open state is a bool on it, so there is no other source to reconcile from.
		/// </summary>
		private void RequestReconcile()
		{
			if (_dispatcher is null) return;
			if (_dispatcher.HasThreadAccess) ReconcileAllNow();
			else _dispatcher.TryEnqueue(ReconcileAllNow);
		}

		private void ReconcileAllNow()
		{
			foreach (var reg in _regs.Values)
			{
				bool want = reg.IsOpen();
				bool have = _windows.ContainsKey(reg.Id);
				if (want && !have) OpenWindow(reg);
				else if (!want && have) CloseWindow(reg.Id, programmatic: true);
				else if (want && have) ApplyPinned(reg, _windows[reg.Id]); // keep an open window's pin in sync
			}
		}

		// Own the panel by the main window while pinned, un-own it while not (see PIN in the header). Runs at
		// open and on every reconcile, so a pin toggle — which flips the VM flag — takes effect immediately.
		private void ApplyPinned(Registration reg, Window window)
		{
			if (_owner is null) return;
			bool ownByMain = _monitorMode() switch
			{
				// MONITOR MODE (multi: TODO) — Multi borrows Single's rule until a second screen exists to build it.
				MonitorMode.Multi => reg.KeepAboveOwner(),
				// Single: pinned = owned by the main window (above Anvil only).
				_ => reg.KeepAboveOwner(),
			};
			var owner = ownByMain ? WinRT.Interop.WindowNative.GetWindowHandle(_owner) : IntPtr.Zero;
			SetOwner(WinRT.Interop.WindowNative.GetWindowHandle(window), owner);
		}

		/// <summary>
		/// Re-applies the owner's light/dark palette to every panel window that is currently open. Called
		/// by <c>MainWindow</c> when the app's theme changes.
		/// </summary>
		/// <remarks>
		/// ⚠️ Needed because a panel takes its palette ONCE, in <see cref="OpenWindow"/> — each is its own
		/// top-level Window with its own XAML tree, so it does not inherit the main window's theme and
		/// nothing propagates a later change to it. Without this, panels left open across a theme switch
		/// keep the previous palette until they are closed and reopened.
		/// </remarks>
		public void ApplyOwnerTheme()
		{
			if (_owner?.Content is not FrameworkElement ownerRoot) return;

			foreach (var window in _windows.Values)
			{
				if (window.Content is FrameworkElement content)
				{
					content.RequestedTheme = ownerRoot.ActualTheme;
				}
			}
		}

		private void OpenWindow(Registration reg)
		{
			if (_owner is null) return;

			// OPEN TIMING (2026-10-10): building the launch's Now + Fore windows was 804 ms of the map-ready chain, and the
			// UI thread froze 0.9-2.6 s right after. Every open logs its phases, all from the start of this method: build
			// (the section's XAML) · create (Window + chrome + placement + Activate) · loaded / first layout / first
			// rendered frame (the content's own, on later UI ticks) — and whether a debugger is attached, whose XAML
			// tooling hooks every element (compare an F5 launch with a Start-menu one).
			var clock = System.Diagnostics.Stopwatch.StartNew();

			// The section content IS the window content: its dark surface fills the whole window (no panel
			// frame, no backdrop), so growing the window just reveals more of that surface.
			var content = reg.BuildContent();
			var buildMs = clock.ElapsedMilliseconds;

			// Take the DPI scale from the OWNER window (already loaded, so its XamlRoot is available) rather
			// than the new window's — the new window has no XamlRoot until its content loads, which is AFTER
			// Activate. Using it here lets us size + place the window BEFORE showing it, so it appears at the
			// right size/spot instead of flashing at WinUI's default size and then snapping.
			double scale = 1.0;
			if (_owner.Content is FrameworkElement ownerRoot)
			{
				content.RequestedTheme = ownerRoot.ActualTheme; // match the main window's light/dark theme
				scale = ownerRoot.XamlRoot?.RasterizationScale ?? 1.0;
			}

			var window = new Window { Title = reg.Title, Content = content };

			// Extend the dark content into the title-bar area so the native (light) caption bar is replaced by
			// the panel's own dark surface. The default title-bar drag region keeps the window movable — no
			// custom drag element / SetTitleBar needed.
			if (reg.CustomChrome)
			{
				window.ExtendsContentIntoTitleBar = true;
			}

			var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
			var frameLock = AttachFrameLock(reg, hwnd);

			switch (_monitorMode())
			{
				// MONITOR MODE (multi: TODO) — Multi borrows Single's chrome until a second screen exists to build it.
				case MonitorMode.Multi:
				default:
					ApplySingleMonitorChrome(window, hwnd);
					break;
			}

			ApplyPinned(reg, window);

			// Place BEFORE Activate so it opens already-fitted (no default-size flash), then once more after —
			// see PlaceVisibleFrame for why the second call is what makes the 5 px margins exact.
			var target = ComputePlacement(reg.Anchor, scale);
			frameLock.Placing = true;
			if (target is { } before) PlaceVisibleFrame(hwnd, before.X, before.Y, before.W, before.H);

			window.Closed += (_, _) => OnWindowClosed(reg);
			_windows[reg.Id] = window;
			_frames[reg.Id] = (hwnd, frameLock);
			window.Activate();

			if (target is { } after) PlaceVisibleFrame(hwnd, after.X, after.Y, after.W, after.H);
			frameLock.Placing = false;
			TraceOpen(reg.Id, content, clock, buildMs, createMs: clock.ElapsedMilliseconds - buildMs);
		}

		// The rest of OPEN TIMING (see OpenWindow): waits for the content's Loaded, then its first LayoutUpdated, then the
		// next rendered frame, and logs one line. Handlers unhook themselves; nothing here changes the window.
		private void TraceOpen(string id, FrameworkElement content, System.Diagnostics.Stopwatch clock, long buildMs, long createMs)
		{
			long loadedMs = -1, layoutMs = -1;
			void OnLoaded(object s, RoutedEventArgs e)
			{
				content.Loaded -= OnLoaded;
				loadedMs = clock.ElapsedMilliseconds;
				content.LayoutUpdated += OnLayout;
			}
			void OnLayout(object? s, object e)
			{
				content.LayoutUpdated -= OnLayout;
				layoutMs = clock.ElapsedMilliseconds;
				Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnFrame;
			}
			void OnFrame(object? s, object e)
			{
				Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnFrame;
				WindowShown?.Invoke(this, id); // MapViewModel's LAUNCH WINDOWS opens the next restored window on this
				_logger.LogInformation(
					"Window {Id} opened in {Total} ms: build {Build} · create+activate {Create} · loaded at {Loaded} · " +
					"first layout at {Layout} · first frame at {Frame} (debugger {Debugger})",
					id, clock.ElapsedMilliseconds, buildMs, createMs, loadedMs, layoutMs, clock.ElapsedMilliseconds,
					System.Diagnostics.Debugger.IsAttached ? "attached" : "not attached");
			}
			content.Loaded += OnLoaded;
		}

		// The single-monitor CHROME POLICY (see the header): taskbar button, minimize + close, never topmost.
		private static void ApplySingleMonitorChrome(Window window, IntPtr hwnd)
		{
			if (window.AppWindow is AppWindow appWindow)
			{
				// ⚠️ A TASKBAR + ALT-TAB ENTRY, and it is what makes minimize (below) safe: a minimized panel is
				// found again from its taskbar button. HISTORY: panels were hidden from the switchers while they
				// were close-only; minimize came back (user's call) to recover a panel lost behind other windows.
				// ⚠️⚠️ IsShownInSwitchers ALONE IS NOT ENOUGH for a PINNED panel: an owned window never gets a
				// taskbar button, and minimizing a window with no button collapses it into a small title bar
				// parked at the bottom-left of the screen (over the radar controls) instead of going to the
				// taskbar. WS_EX_APPWINDOW forces the button; it has to land before the first show (Activate).
				appWindow.IsShownInSwitchers = true;
				ForceTaskbarButton(hwnd);

				if (appWindow.Presenter is OverlappedPresenter presenter)
				{
					// Never topmost — "pinned" is ownership, applied below (see PIN in the header).
					presenter.IsAlwaysOnTop = false;

					// MINIMIZE + CLOSE CAPTION. ⚠️ Accepted trade: a minimized panel leaves its bar key LIT with
					// nothing on screen — the taskbar button is the way back. MAXIMIZE stays off; Windows still
					// draws its box, greyed, so the caption is THREE buttons wide (PinToggle's inset tracks that).
					// ⚠️ IsResizable is deliberately LEFT ALONE (defaults true). The LOCK is what stops resizing,
					// in the subclass — flipping IsResizable would restyle the frame (and change the invisible
					// border the placement just measured) every time the lock toggled.
					// Static policy, set once at open — unlike the pin these never belong in the reconcile.
					presenter.IsMinimizable = true;
					presenter.IsMaximizable = false;
				}
			}
		}

		// The panel's designated rect in SCREEN physical px, from the owner's client area and the anchor
		// rules in the PLACEMENT block. Null if the owner can't be measured (the window then opens wherever
		// WinUI puts it, which beats a zero-size window).
		private (int X, int Y, int W, int H)? ComputePlacement(WindowAnchor anchor, double scale)
		{
			if (_owner is null || scale <= 0) return null;

			if (!TryGetReferenceArea(out var origin, out var client)) return null;

			double ownerWidth = (client.Right - client.Left) / scale;
			double ownerHeight = (client.Bottom - client.Top) / scale;
			if (ownerWidth <= 2 * Margin || ownerHeight <= 2 * Margin) return null;

			// The usable band: the owner's top down to the top of the bottom chrome.
			double bottom = Math.Clamp(_availableBottom(), 2 * Margin + 1, ownerHeight);
			double bandHeight = bottom - 2 * Margin;

			double x, y, w, h;
			switch (anchor)
			{
				case WindowAnchor.Left:
				case WindowAnchor.Right:
					w = Math.Min(EdgeWidth, ownerWidth - 2 * Margin);
					h = bandHeight;
					x = anchor == WindowAnchor.Left ? Margin : ownerWidth - Margin - w;
					y = Margin;
					break;

				default:
					// ⚠️ Sized as if BOTH strips were occupied, whether or not they are — see PLACEMENT.
					double reserved = 2 * (Margin + EdgeWidth + Margin);
					w = Math.Min(Math.Max(ownerWidth - reserved, CenterMinWidth), ownerWidth - 2 * Margin);
					h = Math.Min(Math.Max(w * 9 / 16, CenterMinHeight), bandHeight);
					x = (ownerWidth - w) / 2;
					y = Margin + (bandHeight - h) / 2;
					break;
			}

			return (
				origin.X + (int)Math.Round(x * scale),
				origin.Y + (int)Math.Round(y * scale),
				(int)Math.Round(w * scale),
				(int)Math.Round(h * scale));
		}

		// The area panel placement is measured off: its screen-space origin + its size (physical px).
		private bool TryGetReferenceArea(out POINT origin, out RECT client)
		{
			origin = default;
			client = default;
			if (_owner is null) return false;

			switch (_monitorMode())
			{
				// MONITOR MODE (multi: TODO) — Multi will likely measure off a chosen monitor's work area rather
				// than the main window; until it is built it borrows Single's rule.
				case MonitorMode.Multi:
				default:
					// Single: the main window's client area, so the spots travel with it.
					var ownerHwnd = WinRT.Interop.WindowNative.GetWindowHandle(_owner);
					if (!GetClientRect(ownerHwnd, out client)) return false;
					return ClientToScreen(ownerHwnd, ref origin);
			}
		}

		// Subclass the panel's HWND so a LOCKED window refuses to move or resize. Installed before placement
		// (with Placing set) so our own SetWindowPos calls pass; the lock flag is read live on every message,
		// so the title-bar toggle needs no push from the reconcile.
		private FrameLock AttachFrameLock(Registration reg, IntPtr hwnd)
		{
			var state = new FrameLock { IsLocked = reg.IsLocked };
			state.Proc = (h, msg, wParam, lParam, id, refData) =>
			{
				bool locked = !state.Placing && state.IsLocked();
				switch (msg)
				{
					case WM_WINDOWPOSCHANGING when locked:
						// ⚠️ The choke point: drags, snap, Win+Arrow and Alt+Space Move/Size all arrive as a
						// SetWindowPos. Strip the move + size from it; z-order and show/hide still pass.
						var pos = System.Runtime.InteropServices.Marshal.PtrToStructure<WINDOWPOS>(lParam);
						// ⚠️⚠️ MINIMIZE AND RESTORE MUST PASS. Both are moves too (to the -32000 parking spot and
						// back). Blocking minimize left the window flagged minimized but still on screen, so
						// WinUI stopped drawing it and an empty frame sat there; blocking restore would strand it
						// off-screen. Minimizing = iconic now or heading to the parking spot; restoring = was
						// iconic at the last CHANGED.
						bool minimizeOrRestore = IsIconic(h) || state.WasIconic
							|| pos.X <= MinimizedParkingCoordinate || pos.Y <= MinimizedParkingCoordinate;
						if (!minimizeOrRestore)
						{
							pos.Flags |= SWP_NOMOVE | SWP_NOSIZE;
							System.Runtime.InteropServices.Marshal.StructureToPtr(pos, lParam, false);
						}
						break;

					case WM_WINDOWPOSCHANGED:
						state.WasIconic = IsIconic(h);
						break;

					case WM_NCHITTEST when locked:
						// Don't offer resize arrows on a frame that won't resize.
						var hit = DefSubclassProc(h, msg, wParam, lParam).ToInt32();
						return hit is >= HTLEFT and <= HTBOTTOMRIGHT ? new IntPtr(HTBORDER) : new IntPtr(hit);

					case WM_SYSCOMMAND when locked:
						int command = wParam.ToInt32() & 0xFFF0;
						if (command is SC_MOVE or SC_SIZE) return IntPtr.Zero;
						break;

					case WM_NCDESTROY:
						RemoveWindowSubclass(h, state.Proc!, id);
						_locks.Remove(state);
						break;
				}
				return DefSubclassProc(h, msg, wParam, lParam);
			};

			SetWindowSubclass(hwnd, state.Proc, UIntPtr.Zero, UIntPtr.Zero);
			_locks.Add(state);
			return state;
		}

		private void OnWindowClosed(Registration reg)
		{
			_windows.Remove(reg.Id);
			_frames.Remove(reg.Id);
			if (_closingProgrammatically.Remove(reg.Id))
			{
				// We closed it (the flag was turned off elsewhere) — the VM is already correct.
				return;
			}
			// The user clicked the window's caption Close: turn the feature off so its top-bar toggle unlatches.
			reg.Close();
		}

		private void CloseWindow(string id, bool programmatic)
		{
			if (!_windows.TryGetValue(id, out var window)) return;
			if (programmatic) _closingProgrammatically.Add(id);
			// ⚠️ Closing the FOREGROUND panel hands activation to whatever Windows picks next — which can be ANOTHER
			// APP (the loading screen closing the PastCast window you just pressed Load in sent Anvil behind Visual
			// Studio, 2026-10-04). Give the focus back to the main window first, so it stays in front.
			if (_owner is not null && !_ownerClosed
				&& NativeWindowInterop.GetForegroundWindow() == WinRT.Interop.WindowNative.GetWindowHandle(window))
			{
				_owner.Activate();
			}
			window.Close(); // fires Closed → OnWindowClosed does the cleanup
		}

		private void CloseAll()
		{
			foreach (var id in new List<string>(_windows.Keys))
			{
				CloseWindow(id, programmatic: true);
			}
		}
	}
}
