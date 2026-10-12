using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.Controls.Primitives;
using Anvil.ViewModels;

namespace Anvil.Controls.Windows
{
	/// <summary>
	/// ONE temporal mode's settings panel (see the XAML header), hosted in its own OS window by
	/// <see cref="WindowManager"/>. Registered three times — once per <see cref="TemporalMode"/> — each
	/// driven by that mode's open flag on <see cref="MapViewModel"/> and latched by the settings rail at the
	/// foot of that mode's bar key.
	/// </summary>
	public sealed partial class TemporalWindow : UserControl
	{
		/// <param name="mode">Which mode this window configures: picks the body (x:Load in the XAML) and which
		/// flags the pin and lock drive. Fixed for the window's life — a window is a fresh instance per open.</param>
		/// <param name="viewModel">The coordinator view model.</param>
		public TemporalWindow(TemporalMode mode, MapViewModel viewModel)
		{
			// ⚠️ Both BEFORE InitializeComponent: the bodies' x:Load and ViewModel binds are OneTime.
			Mode = mode;
			ViewModel = viewModel;
			InitializeComponent();

			// The pin and lock show the flags' CURRENT values — these persist across a window being closed and
			// reopened, so a panel unpinned earlier in the session comes back unpinned.
			Pin.IsChecked = ViewModel.IsTemporalWindowOnTop(Mode);
			Lock.IsChecked = ViewModel.IsTemporalWindowLocked(Mode);

			// The pin writes straight back to the mode's on-top flag (and the lock to its locked flag).
			Pin.RegisterPropertyChangedCallback(PinToggle.IsCheckedProperty, (_, _) =>
				ViewModel?.SetTemporalWindowOnTop(Mode, Pin.IsChecked));
			Lock.RegisterPropertyChangedCallback(LockToggle.IsCheckedProperty, (_, _) =>
				ViewModel?.SetTemporalWindowLocked(Mode, Lock.IsChecked));

			// NO FOCUS RING ON OPEN (the user's call, 2026-10-01): a new window hands its first focus to the first
			// tab stop — PastCast's card Atlas button — as KEYBOARD focus, so it opened wearing the focus ring as if
			// selected. The first focus inside a moment of loading is re-given as PROGRAMMATIC focus (same element,
			// no ring); Tab still starts there. Time-boxed so a real first Tab press later keeps its ring.
			Loaded += (_, _) => _openedAt = Environment.TickCount64;
			GotFocus += OnFirstFocus;
		}

		private long _openedAt;

		private void OnFirstFocus(object sender, RoutedEventArgs e)
		{
			if (Environment.TickCount64 - _openedAt > 1000)
			{
				GotFocus -= OnFirstFocus;
				return;
			}
			if (e.OriginalSource is Control { FocusState: FocusState.Keyboard } control)
			{
				GotFocus -= OnFirstFocus;
				control.Focus(FocusState.Programmatic);
			}
		}

		/// <summary>Which mode this window configures. Read-only — see the XAML header's x:Load note.</summary>
		public TemporalMode Mode { get; }

		/// <summary>The coordinator view model, handed to whichever body loads.</summary>
		public MapViewModel ViewModel { get; }

		// The XAML's x:Load switches — exactly one is true.
		public bool IsPast => Mode == TemporalMode.Past;
		public bool IsNow => Mode == TemporalMode.Now;
		public bool IsFore => Mode == TemporalMode.Fore;
	}
}
