using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.Controls.Composites;
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
		public TemporalWindow()
		{
			InitializeComponent();

			// The pin writes straight back to the mode's on-top flag. Registered once, here, rather than in
			// ApplyMode — which runs again whenever either dependency property lands, and would otherwise stack
			// up a callback per assignment.
			Pin.RegisterPropertyChangedCallback(PinToggle.IsCheckedProperty, (_, _) =>
				ViewModel?.SetTemporalWindowOnTop(Mode, Pin.IsChecked));
		}

		/// <summary>Which mode this window configures. Set once at construction by the registration in
		/// MainWindow; it picks the title, the body and which on-top flag the pin drives.</summary>
		public TemporalMode Mode
		{
			get => (TemporalMode)GetValue(ModeProperty);
			set => SetValue(ModeProperty, value);
		}

		public static readonly DependencyProperty ModeProperty =
			DependencyProperty.Register(nameof(Mode), typeof(TemporalMode), typeof(TemporalWindow),
				new PropertyMetadata(TemporalMode.Past, (d, _) => ((TemporalWindow)d).ApplyMode()));

		/// <summary>The coordinator view model; bound from the host.</summary>
		public MapViewModel ViewModel
		{
			get => (MapViewModel)GetValue(ViewModelProperty);
			set => SetValue(ViewModelProperty, value);
		}

		public static readonly DependencyProperty ViewModelProperty =
			DependencyProperty.Register(nameof(ViewModel), typeof(MapViewModel), typeof(TemporalWindow),
				new PropertyMetadata(null, (d, _) => ((TemporalWindow)d).ApplyMode()));

		// Fill in everything that depends on WHICH mode this window is. Runs on either dependency property
		// landing, because the registration sets them in whatever order the object initializer happens to use
		// and both are needed before a body can be built.
		//
		// ⚠️ The body is built ONCE per window instance and never swapped: Mode does not change after the
		// registration sets it, and a window is a fresh instance every time it opens. The guard below is what
		// keeps a second property assignment from throwing the body (and the live widget state inside
		// PastCastTab) away and rebuilding it.
		private void ApplyMode()
		{
			if (ViewModel is null)
			{
				return;
			}

			// The same three names the bar keys carry, so the panel and the key that opened it agree.
			TitleText.Text = Mode switch
			{
				TemporalMode.Past => "PastCast",
				TemporalMode.Now => "NowCast",
				_ => "ForeCast",
			};

			// The pin shows the flag's CURRENT value — these persist across a window being closed and
			// reopened, so a panel unpinned earlier in the session comes back unpinned.
			Pin.IsChecked = ViewModel.IsTemporalWindowOnTop(Mode);

			if (_builtFor == Mode && BodyHost.Content is not null)
			{
				return;
			}

			BodyHost.Content = Mode switch
			{
				TemporalMode.Past => new PastCastTab { ViewModel = ViewModel },
				TemporalMode.Now => new NowCastTab { ViewModel = ViewModel },
				_ => (FrameworkElement)new ForeCastTab { ViewModel = ViewModel },
			};

			_builtFor = Mode;
		}

		// Which mode's body is currently in BodyHost; null until the first build.
		private TemporalMode? _builtFor;
	}
}
