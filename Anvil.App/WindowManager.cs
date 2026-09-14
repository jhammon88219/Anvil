using System;
using System.Collections.Generic;
using System.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using static Anvil.NativeWindowInterop;

namespace Anvil
{
	// ============================================================================================
	// APP-WIDE WINDOWS (removable feature — see Windows/ + the manager.Register calls in MainWindow).
	//
	// Every app-wide panel (Timeframe, Settings, Site Explorer and the Pipeline Console) lives in its OWN
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
	// CLOSE-ONLY caption (no minimize, no maximize) and is hidden from the taskbar + Alt-Tab. A panel's hide
	// is its bar key, which also unlatches the toggle; minimize did the same thing WORSE (window gone, toggle
	// still lit). See the comments in OpenWindow for the full reasoning before changing any of it.
	// Consequence: with no switcher entry, the only route back to a panel buried behind the main window is
	// toggling its bar key off/on.
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
	// ⚠️ MEASURED ONCE, AT OPEN, and always to the designated spot — a window reopened after being dragged
	// comes back home, and nothing reflows when other panels open or chrome hides. Overlap is allowed.
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
	// ============================================================================================

	/// <summary>Where a panel window opens relative to the main window. See the PLACEMENT block above.</summary>
	public enum WindowAnchor
	{
		/// <summary>Flush left, 480 wide, full height above the bottom chrome (PastCast, NowCast).</summary>
		Left,
		/// <summary>Flush right, 480 wide, full height above the bottom chrome (ForeCast).</summary>
		Right,
		/// <summary>Centred, ≈16:9, sized between the two edge strips (Settings, Sites, Pipeline Console).</summary>
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
			public Func<bool> AlwaysOnTop = () => false; // topmost (evaluated live so a pin toggle can flip it)
			public Func<bool> IsLocked = () => false;    // no move/resize (read live by the subclass, per message)
			public bool CustomChrome; // extend content into the title bar so the dark surface replaces the caption
		}

		// One open window's subclass state. The delegate is held HERE so the GC can't collect it while Win32
		// still calls through it; it is removed at WM_NCDESTROY.
		private sealed class FrameLock
		{
			public required Func<bool> IsLocked;
			public bool Placing; // our own placement calls pass through even while locked
			public SubclassProc? Proc;
		}

		private readonly Dictionary<string, Registration> _regs = new();
		private readonly Dictionary<string, Window> _windows = new();
		// Keyed by INSTANCE, not id: a panel closed and reopened quickly can have its new window's lock attached
		// before the old HWND's WM_NCDESTROY runs, and removing by id would drop the new window's delegate.
		private readonly HashSet<FrameLock> _locks = new();
		// Ids we are closing ourselves (the flag went false elsewhere), so the Closed handler doesn't mistake
		// it for the user clicking the OS caption's Close and re-fire the Close action.
		private readonly HashSet<string> _closingProgrammatically = new();

		private Window? _owner;
		private DispatcherQueue? _dispatcher;
		private Func<double> _availableBottom = () => double.PositiveInfinity;

		/// <summary>
		/// Wire the manager to the owner window + the coordinator VM whose PropertyChanged drives reconciles.
		/// Call once, on the UI thread, after the owner exists.
		/// </summary>
		/// <param name="availableBottom">The bottom of the space panels may use, in LOGICAL px from the top of
		/// the owner's content — the top edge of whatever bottom chrome is showing. Read at each open.</param>
		public void Initialize(Window owner, INotifyPropertyChanged coordinator, Func<double> availableBottom)
		{
			_owner = owner;
			_dispatcher = owner.DispatcherQueue;
			_availableBottom = availableBottom;
			coordinator.PropertyChanged += (_, _) => RequestReconcile();
			owner.Closed += (_, _) => CloseAll(); // don't leak panel windows when the app closes
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
			Func<bool>? alwaysOnTop = null,
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
				AlwaysOnTop = alwaysOnTop ?? (() => false),
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
				else if (want && have) ApplyAlwaysOnTop(reg); // keep an open window's topmost state in sync
			}
		}

		// Push the panel's current always-on-top state onto its open window's presenter (so a pin toggle,
		// which flips the VM flag, takes effect on the next reconcile).
		private void ApplyAlwaysOnTop(Registration reg)
		{
			if (_windows.TryGetValue(reg.Id, out var window)
				&& window.AppWindow?.Presenter is OverlappedPresenter presenter)
			{
				presenter.IsAlwaysOnTop = reg.AlwaysOnTop();
			}
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

			// The section content IS the window content: its dark surface fills the whole window (no panel
			// frame, no backdrop), so growing the window just reveals more of that surface.
			var content = reg.BuildContent();

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

			if (window.AppWindow is AppWindow appWindow)
			{
				// A panel is app chrome, not an app: keep it out of the taskbar + Alt-Tab so it can't read as a
				// second Anvil. With minimize gone (below) a panel can't vanish, so the switcher entry only ever
				// offered a second way to raise one — at the cost of eight bogus taskbar buttons.
				appWindow.IsShownInSwitchers = false;

				// Topmost windows (e.g. the Pipeline Console) float above the main window even when the map has
				// focus — so a single-monitor user can watch them while interacting with the map. It doesn't
				// take focus, so the map underneath stays clickable/draggable.
				if (appWindow.Presenter is OverlappedPresenter presenter)
				{
					presenter.IsAlwaysOnTop = reg.AlwaysOnTop();

					// CLOSE-ONLY CAPTION — the panel's bar key IS its hide, so the caption keeps only ✕.
					// ⚠️ MINIMIZE is the one that had to go: it hides the window while the bar key stays LIT, so
					// the app claims a panel is open with nothing on screen. The bar key does the same job AND
					// unlatches itself. MAXIMIZE travels with it, not on its own merits — drop only the minimize
					// box and the button still draws, greyed; drop both and the caption collapses to one button.
					// ⚠️ IsResizable is deliberately LEFT ALONE (defaults true). The LOCK is what stops resizing,
					// in the subclass — flipping IsResizable would restyle the frame (and change the invisible
					// border the placement just measured) every time the lock toggled.
					// Static policy, set once at open — unlike IsAlwaysOnTop these never belong in the reconcile.
					presenter.IsMinimizable = false;
					presenter.IsMaximizable = false;
				}
			}

			// Place BEFORE Activate so it opens already-fitted (no default-size flash), then once more after —
			// see PlaceVisibleFrame for why the second call is what makes the 5 px margins exact.
			var target = ComputePlacement(reg.Anchor, scale);
			frameLock.Placing = true;
			if (target is { } before) PlaceVisibleFrame(hwnd, before.X, before.Y, before.W, before.H);

			window.Closed += (_, _) => OnWindowClosed(reg);
			_windows[reg.Id] = window;
			window.Activate();

			if (target is { } after) PlaceVisibleFrame(hwnd, after.X, after.Y, after.W, after.H);
			frameLock.Placing = false;
		}

		// The panel's designated rect in SCREEN physical px, from the owner's client area and the anchor
		// rules in the PLACEMENT block. Null if the owner can't be measured (the window then opens wherever
		// WinUI puts it, which beats a zero-size window).
		private (int X, int Y, int W, int H)? ComputePlacement(WindowAnchor anchor, double scale)
		{
			if (_owner is null || scale <= 0) return null;

			var ownerHwnd = WinRT.Interop.WindowNative.GetWindowHandle(_owner);
			if (!GetClientRect(ownerHwnd, out var client)) return null;
			var origin = new POINT();
			if (!ClientToScreen(ownerHwnd, ref origin)) return null;

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
						pos.Flags |= SWP_NOMOVE | SWP_NOSIZE;
						System.Runtime.InteropServices.Marshal.StructureToPtr(pos, lParam, false);
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
