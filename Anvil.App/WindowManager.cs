using System;
using System.Collections.Generic;
using System.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

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
	// still lit). Panels stay freely RESIZABLE. See the comments in OpenWindow for the full reasoning before
	// changing any of it. Consequence: with no switcher entry, the only route back to a panel buried behind
	// the main window is toggling its bar key off/on.
	// ============================================================================================

	/// <summary>
	/// Hosts each app-wide panel in its own <see cref="Window"/>, keeping the window's existence in sync with
	/// that panel's IsOpen VM state. Register a panel with <see cref="Register"/>; the window opens when
	/// IsOpen becomes true and closes when it becomes false (and the OS-caption Close flips IsOpen back off).
	/// </summary>
	public sealed class WindowManager
	{
		private sealed class Registration
		{
			public required string Id;
			public required Func<bool> IsOpen;                    // feature showing → window should exist
			public required Action Close;                        // set IsOpen=false (the OS-caption Close path)
			public required Func<FrameworkElement> BuildContent; // a fresh section instance bound to the shared VM
			public required string Title;
			public double Width;
			public double Height;
			public bool SizeToContent; // measure the content for HEIGHT; Height is then only a fallback
			public Func<bool> AlwaysOnTop = () => false; // topmost (evaluated live so a pin toggle can flip it)
			public bool CustomChrome; // extend content into the title bar so the dark surface replaces the caption
		}

		private readonly Dictionary<string, Registration> _regs = new();
		private readonly Dictionary<string, Window> _windows = new();
		// Ids we are closing ourselves (the flag went false elsewhere), so the Closed handler doesn't mistake
		// it for the user clicking the OS caption's Close and re-fire the Close action.
		private readonly HashSet<string> _closingProgrammatically = new();

		private Window? _owner;
		private DispatcherQueue? _dispatcher;

		/// <summary>
		/// Wire the manager to the owner window + the coordinator VM whose PropertyChanged drives reconciles.
		/// Call once, on the UI thread, after the owner exists.
		/// </summary>
		public void Initialize(Window owner, INotifyPropertyChanged coordinator)
		{
			_owner = owner;
			_dispatcher = owner.DispatcherQueue;
			coordinator.PropertyChanged += (_, _) => RequestReconcile();
			owner.Closed += (_, _) => CloseAll(); // don't leak panel windows when the app closes
		}

		/// <summary>
		/// Register a panel. <paramref name="isOpen"/> reads the panel's VM state, <paramref name="close"/> turns
		/// the feature off (used when the user closes the window via its caption), and <paramref name="buildContent"/>
		/// makes a fresh section instance (bound to the shared VM, rendered headerless).
		/// </summary>
		public void Register(
			string id,
			Func<bool> isOpen,
			Action close,
			Func<FrameworkElement> buildContent,
			string title,
			double width,
			double height,
			Func<bool>? alwaysOnTop = null,
			bool customChrome = false,
			bool sizeToContent = false)
		{
			_regs[id] = new Registration
			{
				Id = id,
				IsOpen = isOpen,
				Close = close,
				BuildContent = buildContent,
				Title = title,
				Width = width,
				Height = height,
				AlwaysOnTop = alwaysOnTop ?? (() => false),
				CustomChrome = customChrome,
				SizeToContent = sizeToContent,
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

			// Size + center over the main window BEFORE Activate so it opens already-fitted (no default-size
			// flash). Still freely RESIZABLE (see the caption note below). AppWindow works in physical pixels,
			// so scale the panel's logical footprint by the monitor's DPI scale.
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
					// ⚠️ IsResizable is deliberately LEFT ALONE (defaults true) — drag-resize is how a panel's
					// size gets tuned, and it's what makes losing maximize free. Don't "finish the set" here.
					// Static policy, set once at open — unlike IsAlwaysOnTop these never belong in the reconcile.
					presenter.IsMinimizable = false;
					presenter.IsMaximizable = false;
				}

				int w = (int)Math.Ceiling(reg.Width * scale);
				int h = (int)Math.Ceiling(MeasuredHeight(reg, content, appWindow, scale) * scale);
				appWindow.ResizeClient(new Windows.Graphics.SizeInt32(w, h));

				if (_owner.AppWindow is AppWindow owner)
				{
					var pos = owner.Position;
					var size = owner.Size;
					appWindow.Move(new Windows.Graphics.PointInt32(
						pos.X + (size.Width - w) / 2, pos.Y + (size.Height - h) / 2));
				}
			}

			window.Closed += (_, _) => OnWindowClosed(reg);
			_windows[reg.Id] = window;
			window.Activate();
		}

		// The panel's HEIGHT: measured from its content when the registration asks for it, otherwise the
		// registered value.
		//
		// ⚠️ WIDTH IS NEVER MEASURED. These bodies are vertical stacks that wrap to whatever width they are
		// given, so width is the INPUT to the measure and height is the answer. Measuring both would just
		// return whatever the widest single control happened to want.
		//
		// ⚠️ IT IS A ONE-SHOT, at open. Content that grows later (a card gaining a footer line, a longer
		// status) does not resize the window — the same as the fixed sizes this replaced. The window stays
		// resizable, which is the escape hatch.
		//
		// ⚠️ CLAMPED TO THE MONITOR'S WORK AREA. A panel with no ScrollViewer and a taller-than-screen
		// content would put its bottom rows out of reach for good, so an oversized measure is capped rather
		// than honoured. If a panel ever hits this cap it needs its scroller back, not a bigger clamp.
		//
		// ⚠️ A DEGENERATE MEASURE FALLS BACK to the registered height. Measuring an element that is not yet
		// in a visual tree is not guaranteed to produce anything useful, and a window sized 0 would be a far
		// worse failure than one sized as it always used to be.
		private static double MeasuredHeight(Registration reg, FrameworkElement content, AppWindow appWindow, double scale)
		{
			if (!reg.SizeToContent)
			{
				return reg.Height;
			}

			content.Measure(new Windows.Foundation.Size(reg.Width, double.PositiveInfinity));
			var desired = content.DesiredSize.Height;
			if (double.IsNaN(desired) || desired <= 0)
			{
				return reg.Height;
			}

			var workArea = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
			var maxLogical = (workArea.Height / scale) - WorkAreaMargin;
			return maxLogical > 0 ? Math.Min(desired, maxLogical) : desired;
		}

		// Breathing room left below a content-sized panel so it never runs flush to the taskbar.
		private const double WorkAreaMargin = 48;

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
