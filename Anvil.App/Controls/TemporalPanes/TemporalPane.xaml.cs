using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using Anvil.Controls.TemporalPanes.DragMoveLayerExpanders;
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
		private void OnBodyLoaded(object sender, RoutedEventArgs e)
		{
			if (!_headerHooked && ActiveHeader is { } header)
			{
				_headerHooked = true;
				header.MinimizeClick += (_, _) => ViewModel?.SetPaneMinimized(Mode, true);
			}
			ApplyMinimized();
			DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => Shown?.Invoke(this, EventArgs.Empty));
		}

		// ===== MINIMIZED (see the XAML header) =====

		/// <summary>The pane is minimized to its key column. Bound from MainWindow (MapViewModel.Is*PaneMinimized).</summary>
		public bool IsMinimized
		{
			get => (bool)GetValue(IsMinimizedProperty);
			set => SetValue(IsMinimizedProperty, value);
		}
		public static readonly DependencyProperty IsMinimizedProperty =
			DependencyProperty.Register(nameof(IsMinimized), typeof(bool), typeof(TemporalPane),
				new PropertyMetadata(false, (d, _) => ((TemporalPane)d).OnMinimizedChanged()));

		/// <summary>A minimized pane's width: a 32 px key + 4 px each side.</summary>
		private const double MinimizedWidth = 40;
		private const double ExpandButtonHeight = 28; // TemporalPaneKeyColumn's › (centred on the header row)
		private const int PlaceAttempts = 5;          // layout passes a starting-minimized body gets to be measured

		private double _fullWidth = double.NaN;       // the declared Width (MainWindow: TemporalPaneWidth), read once
		private bool _headerHooked;
		private IReadOnlyList<TemporalPaneKeyColumnItem> _keys = Array.Empty<TemporalPaneKeyColumnItem>();

		private DragMoveLayerExpanderHost? ActiveHost => Mode switch
		{
			TemporalMode.Past => TemporalPanePastCastContent?.LayerHost,
			TemporalMode.Now => TemporalPaneNowCastContent?.LayerHost,
			_ => TemporalPaneForeCastContent?.LayerHost,
		};

		private TemporalPaneHeader? ActiveHeader => Mode switch
		{
			TemporalMode.Past => TemporalPanePastCastContent?.HeaderRow,
			TemporalMode.Now => TemporalPaneNowCastContent?.HeaderRow,
			_ => TemporalPaneForeCastContent?.HeaderRow,
		};

		// The pane's OWN width is the one thing MainWindow's layout reads (strip ends, rail, headers, map frame).
		private void OnMinimizedChanged()
		{
			if (double.IsNaN(_fullWidth)) { _fullWidth = Width; }
			Width = IsMinimized ? MinimizedWidth : _fullWidth;
			ApplyMinimized();
		}

		private void OnExpandRequested(object? sender, EventArgs e) => ViewModel?.SetPaneMinimized(Mode, false);

		private void ApplyMinimized()
		{
			if (TemporalPaneBody is null)
			{
				// Not built yet: OnBodyLoaded applies it. Until then there is nothing to show either way.
				TemporalPaneKeyColumn.Visibility = Visibility.Collapsed;
				return;
			}

			if (!IsMinimized)
			{
				TemporalPaneBody.Visibility = Visibility.Visible;
				TemporalPaneBody.Opacity = 1;
				TemporalPaneBody.IsHitTestVisible = true;
				TemporalPaneKeyColumn.Visibility = Visibility.Collapsed;
				SetKeys(Array.Empty<TemporalPaneKeyColumnItem>());
				return;
			}

			// Invisible but still LAID OUT (full width) until the keys are placed from it; then out of the way entirely.
			TemporalPaneBody.Opacity = 0;
			TemporalPaneBody.IsHitTestVisible = false;
			TemporalPaneKeyColumn.Visibility = Visibility.Visible;
			PlaceKeysThenCollapse(PlaceAttempts);
		}

		private void PlaceKeysThenCollapse(int attemptsLeft)
		{
			if (!IsMinimized || TemporalPaneBody is null) { return; }
			if (PlaceKeys())
			{
				TemporalPaneBody.Visibility = Visibility.Collapsed;
				return;
			}
			// Not laid out yet (a pane that STARTS minimized): try again after the next layout pass.
			if (attemptsLeft > 0)
			{
				DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => PlaceKeysThenCollapse(attemptsLeft - 1));
			}
		}

		/// <summary>Fill the key column from the layer list and place it on the full pane's rows. False = the body has
		/// no layout to read yet.</summary>
		private bool PlaceKeys()
		{
			if (ActiveHost is not { } host || host.UnscrolledTop(this) is not double keysTop) { return false; }
			TemporalPaneKeyColumn.KeysTop = keysTop;
			if (ActiveHeader is { ActualHeight: > 0 } header)
			{
				var headerTop = header.TransformToVisual(this).TransformPoint(new Point(0, 0)).Y;
				TemporalPaneKeyColumn.ExpandTop = headerTop + (header.ActualHeight - ExpandButtonHeight) / 2;
			}
			SetKeys(host.KeyLayers.Select(layer => new TemporalPaneKeyColumnItem(layer)).ToList());
			return true;
		}

		private void SetKeys(IReadOnlyList<TemporalPaneKeyColumnItem> keys)
		{
			foreach (var old in _keys) { old.Dispose(); }
			_keys = keys;
			TemporalPaneKeyColumn.Items = keys;
		}
	}
}
