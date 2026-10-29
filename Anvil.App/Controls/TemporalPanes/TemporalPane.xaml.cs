using System;
using System.Collections.Generic;
using System.Linq;
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
			Loaded += (_, _) => { ApplyStates(); RealizeIfShown(); };
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
			DependencyProperty.Register(nameof(ViewModel), typeof(MapViewModel), typeof(TemporalPane),
				new PropertyMetadata(null, (d, _) => ((TemporalPane)d).OnViewModelSet()));

		/// <summary>This mode's pane state + sections (MapViewModel.PaneFor(Mode)).</summary>
		public TemporalPaneViewModel? Pane => ViewModel?.PaneFor(Mode);

		// Set ONCE from MainWindow: bind the rail, follow the pane's Full / Rail state, and take this mode's edge.
		private void OnViewModelSet()
		{
			if (Pane is not { } pane) { return; }
			Bindings.Update();
			pane.PropertyChanged += (_, e) =>
			{
				if (e.PropertyName is nameof(TemporalPaneViewModel.IsRail) or nameof(TemporalPaneViewModel.HasDock)) ApplyStates();
			};
			ApplyStates();
		}

		// ── x:Bind helpers for the three Contents (static, called as tp:TemporalPane.X(...)) — ONE copy of the dock rules ──

		/// <summary>The ghost box, the activity expander: FULL PANE ONLY (never in the dock stack).</summary>
		public static Visibility FullOnly(bool isRail) => isRail ? Visibility.Collapsed : Visibility.Visible;

		/// <summary>A section (or the header block, "header") shows iff the pane's ShownSections holds it.</summary>
		public static Visibility SectionVisibility(IReadOnlyCollection<string>? shown, string id) =>
			shown?.Contains(id) == true ? Visibility.Visible : Visibility.Collapsed;

		/// <summary>A ghost row: its box on and its mode running (the VM's Are…GhostsShown), and the FULL pane only.</summary>
		public static Visibility GhostVisibility(bool ghostsShown, bool isRail) =>
			ghostsShown && !isRail ? Visibility.Visible : Visibility.Collapsed;

		/// <summary>Drag-to-reorder is FULL PANE ONLY (DragMoveLayerExpanderHost.IsReorderEnabled).</summary>
		public static bool ReorderEnabled(bool isRail) => !isRail;

		// Not templated, so GoToState lands before Loaded too.
		private void ApplyStates()
		{
			if (Pane is not { } pane) { return; }
			var docked = pane.IsRail && pane.HasDock;
			VisualStateManager.GoToState(this, !pane.IsRail ? "Full" : docked ? "RailDocked" : "Rail", false);
			VisualStateManager.GoToState(this, pane.IsRightEdge ? "RightEdge" : "LeftEdge", false);

			// The OVERFLOW fit runs only while there is a dock stack; leaving it gives every list its full-pane cap back.
			if (docked == _fitting) { return; }
			_fitting = docked;
			if (docked) { LayoutUpdated += OnLayoutWhileDocked; QueueFit(); }
			else { LayoutUpdated -= OnLayoutWhileDocked; ReleaseLists(); }
		}

		// ===== OVERFLOW (the dock stack only): headers never shrink, expanded bodies share what's left, only LISTS give
		// way ("+N more"), and if even the minimums don't fit the stack's own PanelScroller scrolls (gutter always there).
		// The rules are Models/Map/DockStackAllocator (pure, tested); this only MEASURES and APPLIES:
		//   • fixed  = the Content's natural height (laid-out height + its section scroller's overflow) minus every list's
		//              current height — everything that never shrinks;
		//   • lists  = each visible LIST lane (PanelScroller LaneOnLeft): its rows, row height, MaxRows cap, "+N more";
		//   • → each list's DockRows. Re-run after every layout while docked (queued, and a no-op unless a cap changes —
		//     the fixed part doesn't depend on the caps, so it settles in one pass).
		private bool _fitting;
		private bool _fitQueued;
		private readonly List<Primitives.PanelScroller> _dockedLists = new();

		private void OnLayoutWhileDocked(object? sender, object e) => QueueFit();

		private void QueueFit()
		{
			if (_fitQueued) { return; }
			_fitQueued = true;
			DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
			{
				_fitQueued = false;
				if (_fitting) { FitDockStack(); }
			});
		}

		private void FitDockStack()
		{
			if (ActiveContent is not { ActualHeight: > 0 } content) { return; }
			Primitives.PanelScroller? sections = null;
			var lists = new List<Primitives.PanelScroller>();
			FindScrollers(content, ref sections, lists);

			var natural = content.ActualHeight + (sections?.OverflowHeight ?? 0);
			var entries = new List<Models.DockSectionSize> { new(0, false, 0) };
			var fitted = new List<Primitives.PanelScroller>();
			double fixedHeight = natural;
			foreach (var list in lists)
			{
				fixedHeight -= list.ActualHeight;
				if (list.RowHeight <= 0) { fixedHeight += list.ActualHeight; continue; } // not measured yet: fixed for now
				entries.Add(new(0, true, 0, list.ItemCount, list.RowHeight, list.MaxRows, Primitives.PanelScroller.MoreLineHeight));
				fitted.Add(list);
			}
			entries[0] = new(Math.Max(0, fixedHeight), false, 0);

			var available = ActualHeight - content.Margin.Top - content.Margin.Bottom;
			var fit = Models.DockStackAllocator.Fit(entries, available);
			for (int i = 0; i < fitted.Count; i++)
			{
				var list = fitted[i];
				if (!_dockedLists.Contains(list))
				{
					_dockedLists.Add(list);
					list.MoreRequested += OnMoreRequested;
				}
				var rows = fit.Sections[i + 1].VisibleRows;
				if (list.DockRows != rows) { list.DockRows = rows; }
			}
		}

		// The section scroller (the first plain PanelScroller) and every VISIBLE list lane under it; collapsed subtrees
		// (undocked sections, folded lists) are skipped.
		private static void FindScrollers(DependencyObject node, ref Primitives.PanelScroller? sections, List<Primitives.PanelScroller> lists)
		{
			for (int i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(node); i++)
			{
				var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(node, i);
				if (child is UIElement { Visibility: Visibility.Collapsed }) { continue; }
				if (child is Primitives.PanelScroller scroller)
				{
					if (scroller.LaneOnLeft) { lists.Add(scroller); continue; }
					sections ??= scroller;
				}
				FindScrollers(child, ref sections, lists);
			}
		}

		// Leaving the dock stack: every list back to its full-pane behaviour.
		private void ReleaseLists()
		{
			foreach (var list in _dockedLists)
			{
				list.MoreRequested -= OnMoreRequested;
				list.DockRows = -1;
			}
			_dockedLists.Clear();
		}

		// "+N more": the whole pane, where the list scrolls as usual.
		private void OnMoreRequested(object? sender, EventArgs e)
		{
			if (Pane is { IsRail: true } pane) { pane.ToggleState(); }
		}

		private FrameworkElement? ActiveContent => Mode switch
		{
			TemporalMode.Past => TemporalPanePastCastContent,
			TemporalMode.Fore => TemporalPaneForeCastContent,
			_ => TemporalPaneNowCastContent,
		};

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
