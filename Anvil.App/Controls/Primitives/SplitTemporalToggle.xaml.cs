using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Anvil.Controls.Primitives
{
	/// <summary>
	/// A bar key that is a temporal MODE toggle with that mode's SETTINGS WINDOW latch across its foot (see
	/// the XAML header for the shape and the history). The upper half toggles the mode — raising
	/// <see cref="ModeClick"/>, since only the view model knows whether the mode will actually take — and the
	/// side car two-ways <see cref="IsPanelOpen"/>. Plain dependency properties, no view model: the host
	/// (<c>TemporalToggles</c>) does the binding.
	/// </summary>
	public sealed partial class SplitTemporalToggle : UserControl
	{
		public SplitTemporalToggle()
		{
			InitializeComponent();

			// ⚠️ NO SIZING HERE, on purpose. Every dimension is declared in XAML — the shared numbers in
			// Controls/Styles.xaml section 2, the mark's width in this control's own Resources. There used to
			// be a whole class computing them (Anvil.App/Layout/BarKeyMetrics); the bar's height stopped
			// moving when the product chips left for the pane notches, so it was measuring a row that never
			// changed. See the ⚠️ history in that section before adding a number back to code.
			//
			// The face, the stroke and the mark's ink are all looked up from the current theme rather than
			// bound, so they have to be re-resolved when the theme flips.
			ActualThemeChanged += (_, _) => ApplyState();
			ApplyState();
		}

		/// <summary>Raised when the MODE half is clicked. The host turns that into a view-model call; this
		/// control never decides whether the mode changed.</summary>
		public event RoutedEventHandler? ModeClick;

		// ===== Content =====

		/// <summary>The mode's Segoe Fluent glyph, shown above the name.</summary>
		public string Glyph
		{
			get => (string)GetValue(GlyphProperty);
			set => SetValue(GlyphProperty, value);
		}

		public static readonly DependencyProperty GlyphProperty =
			DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(SplitTemporalToggle),
				new PropertyMetadata(string.Empty, (d, _) => ((SplitTemporalToggle)d).ApplyContent()));

		/// <summary>The mode's name, under the glyph ("PastCast" / "NowCast" / "ForeCast").</summary>
		public string ModeName
		{
			get => (string)GetValue(ModeNameProperty);
			set => SetValue(ModeNameProperty, value);
		}

		public static readonly DependencyProperty ModeNameProperty =
			DependencyProperty.Register(nameof(ModeName), typeof(string), typeof(SplitTemporalToggle),
				new PropertyMetadata(string.Empty, (d, _) => ((SplitTemporalToggle)d).ApplyContent()));

		/// <summary>Tooltip for the MODE half. The car's is fixed in the markup — it says the same thing on
		/// all three keys, because it does the same thing on all three.</summary>
		public string ModeToolTip
		{
			get => (string)GetValue(ModeToolTipProperty);
			set => SetValue(ModeToolTipProperty, value);
		}

		public static readonly DependencyProperty ModeToolTipProperty =
			DependencyProperty.Register(nameof(ModeToolTip), typeof(string), typeof(SplitTemporalToggle),
				new PropertyMetadata(string.Empty, (d, _) => ((SplitTemporalToggle)d).ApplyContent()));

		// ===== State =====

		/// <summary>Whether this mode is RUNNING. Lights the key, and is the ONLY thing that enables the
		/// side car — see the XAML header on why an unlit car can never hide an open window.</summary>
		public bool IsModeOn
		{
			get => (bool)GetValue(IsModeOnProperty);
			set => SetValue(IsModeOnProperty, value);
		}

		public static readonly DependencyProperty IsModeOnProperty =
			DependencyProperty.Register(nameof(IsModeOn), typeof(bool), typeof(SplitTemporalToggle),
				new PropertyMetadata(false, (d, _) => ((SplitTemporalToggle)d).ApplyState()));

		/// <summary>Whether this mode's settings window is open. Two-way: the car drives it, and the host
		/// binds it to that window's view-model flag.</summary>
		public bool IsPanelOpen
		{
			get => (bool)GetValue(IsPanelOpenProperty);
			set => SetValue(IsPanelOpenProperty, value);
		}

		public static readonly DependencyProperty IsPanelOpenProperty =
			DependencyProperty.Register(nameof(IsPanelOpen), typeof(bool), typeof(SplitTemporalToggle),
				new PropertyMetadata(false, (d, _) => ((SplitTemporalToggle)d).ApplyState()));

		// The mode half raises the click and then re-asserts its own lit state from the view model's answer.
		//
		// ⚠️ The re-assert is load-bearing. A ToggleButton has ALREADY flipped its own IsChecked by the time
		// this runs, but the mode is a PROJECTION of a subsystem that may decline the change — and when it
		// declines, IsModeOn does not change, so no property-changed callback arrives to put the key back.
		// Re-applying unconditionally covers both outcomes. It is the same trap the pane key and TabStrip
		// documented, and the host's OnPropertyChanged re-raise is the other half of it.
		private void OnModeKeyClick(object sender, RoutedEventArgs e)
		{
			ModeClick?.Invoke(this, e);
			ApplyState();
		}

		// The car latches its own window. Writing the same value back is a no-op, so this cannot ping-pong
		// with the ApplyState that pushes IsPanelOpen the other way.
		private void OnCarToggled(object sender, RoutedEventArgs e)
		{
			var open = CarKey.IsChecked == true;
			if (IsPanelOpen != open)
			{
				IsPanelOpen = open;
			}
		}

		private void ApplyContent()
		{
			ModeGlyph.Glyph = Glyph;
			NameText.Text = ModeName;
			ToolTipService.SetToolTip(ModeKey, ModeToolTip);
		}

		// One place that pushes every piece of state onto the key, so no caller has to remember which part of
		// it a flag touches. Cheap, and called from anywhere a flag might have moved.
		private void ApplyState()
		{
			ModeKey.IsChecked = IsModeOn;

			// THE rule: no mode, no window. The car is dead while the key is unlit, which is what makes a
			// dark car mean "there is nothing open here" rather than "you haven't found the button yet".
			CarKey.IsEnabled = IsModeOn;

			if ((CarKey.IsChecked == true) != IsPanelOpen)
			{
				CarKey.IsChecked = IsPanelOpen;
			}

			// The FACE spans both halves, so a lit key reads as one object rather than as a lit top with a
			// dark strip under it. The halves' own fills paint over this on hover and press.
			Face.Background = (IsModeOn ? Brush("OverlayBarSurfaceElevatedBrush") : null) ?? Transparent;

			// The SHELL is the whole key's stroke — accent and 2px when the mode is on, hairline otherwise.
			// It is drawn over both halves and never hit-tested, so thickening it nudges nothing and steals
			// no pointer from the halves.
			Shell.BorderThickness = new Thickness(IsModeOn ? 2 : 1);
			var stroke = Brush(IsModeOn ? "AccentFillColorDefaultBrush" : "ControlStrokeColorDefaultBrush");
			if (stroke is not null)
			{
				Shell.BorderBrush = stroke;
			}

			ApplyMarkInk();
		}

		// The three-dot mark is Shapes, which do not inherit the content Foreground the templates drive, so its
		// ink is set here — and it has THREE values, not two: dimmed while the car is dead, the usual
		// secondary text colour while it is live, and the on-accent colour once the car fills, where the
		// ordinary ink would sit on a saturated ground and disappear.
		//
		// ⚠️ TextOnAccentFillColorPrimaryBrush, not a hardcoded dark: the accent is light in dark theme and
		// dark in light theme, so the ink on top has to flip with it. Looking the brushes up from the app's
		// resources takes a snapshot of the CURRENT theme, which is why the constructor re-runs ApplyState on
		// ActualThemeChanged.
		private void ApplyMarkInk()
		{
			var ink = Brush(
				!IsModeOn ? "TextFillColorDisabledBrush" :
				IsPanelOpen ? "TextOnAccentFillColorPrimaryBrush" :
				"TextFillColorSecondaryBrush");

			if (ink is null)
			{
				return;
			}

			MarkDot1.Fill = ink;
			MarkDot2.Fill = ink;
			MarkDot3.Fill = ink;
		}

		private static Brush? Brush(string key) =>
			Application.Current.Resources.TryGetValue(key, out var value) ? value as Brush : null;

		// The unlit face. A literal rather than a theme key, because "no fill" is not a colour that varies:
		// an unlit key shows the bar's own surface through it in either theme.
		private static readonly SolidColorBrush Transparent = new(Microsoft.UI.Colors.Transparent);
	}
}
