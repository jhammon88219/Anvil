using System;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.ViewModels;

namespace Anvil.Controls.TemporalPanes
{
	/// <summary>
	/// ONE temporal mode's pane (see the XAML header): an overlay in MainWindow's map band, declared there three times —
	/// once per <see cref="TemporalMode"/> — and shown while that mode's pane flag on <see cref="MapViewModel"/> is set.
	/// Its body is built the first time it shows and kept from then on.
	/// </summary>
	public sealed partial class TemporalPane : UserControl
	{
		public TemporalPane()
		{
			InitializeComponent();
			// The body is built on FIRST SHOW (see the XAML header): Visibility is MainWindow's bind to the pane flag.
			RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => RealizeIfShown());
			Loaded += (_, _) => RealizeIfShown();
		}

		/// <summary>Which mode this pane shows: picks the Content (x:Load in the XAML). Set ONCE in MainWindow.xaml.</summary>
		public TemporalMode Mode
		{
			get => (TemporalMode)GetValue(ModeProperty);
			set => SetValue(ModeProperty, value);
		}

		public static readonly DependencyProperty ModeProperty =
			DependencyProperty.Register(nameof(Mode), typeof(TemporalMode), typeof(TemporalPane), new PropertyMetadata(TemporalMode.Now));

		/// <summary>The coordinator view model, handed to whichever Content loads; bound from the host.</summary>
		public MapViewModel ViewModel
		{
			get => (MapViewModel)GetValue(ViewModelProperty);
			set => SetValue(ViewModelProperty, value);
		}

		public static readonly DependencyProperty ViewModelProperty =
			DependencyProperty.Register(nameof(ViewModel), typeof(MapViewModel), typeof(TemporalPane), new PropertyMetadata(null));

		/// <summary>The body has been built (first show). Never goes back to false.</summary>
		public bool IsRealized { get; private set; }

		/// <summary>The body has loaded after its first show — MapViewModel's LAUNCH WINDOWS shows the next pane on it.</summary>
		public event EventHandler? Shown;

		// The XAML's x:Load switches — exactly one is true.
		public bool IsPast => Mode == TemporalMode.Past;
		public bool IsNow => Mode == TemporalMode.Now;
		public bool IsFore => Mode == TemporalMode.Fore;

		/// <summary>This pane's activity expander (radar + feeds for NowCast; the outlook for Fore; the loads for Past).</summary>
		public TemporalActivityViewModel PaneActivity => ViewModel.ActivityFor(Mode);

		private void RealizeIfShown()
		{
			if (IsRealized || Visibility != Visibility.Visible || !IsLoaded || ViewModel is null) { return; }
			IsRealized = true;
			Bindings.Update();
		}

		// "Shown" once the body's first layout has run: a Low-priority hop lets the frame it is in finish first.
		private void OnBodyLoaded(object sender, RoutedEventArgs e) =>
			DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => Shown?.Invoke(this, EventArgs.Empty));
	}
}
