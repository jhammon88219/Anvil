// PIPELINE CONSOLE (dev/diagnostic — safe to remove as a unit).
using System;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.ViewModels;
using Anvil.Layout;

namespace Anvil.Controls.Windows
{
	/// <summary>
	/// The Pipeline Console — a read-only glass cockpit over the Level-2 build pipeline (a mini-scrubber
	/// per product + VWP/storm-motion state). Observe-only: the shared playhead mirrors the main viewer's
	/// frame (<see cref="PipelineConsoleViewModel.CurrentIndex"/>); there is no seek. Bound to the coordinator
	/// <see cref="MapViewModel"/>; visibility driven by <see cref="MapViewModel.IsPipelineConsoleOpen"/>.
	/// </summary>
	public sealed partial class PipelineConsoleWindow : UserControl
	{
		public PipelineConsoleWindow()
		{
			InitializeComponent();
		}

		/// <summary>The coordinator view model; bound from the host.</summary>
		public MapViewModel ViewModel
		{
			get => (MapViewModel)GetValue(ViewModelProperty);
			set => SetValue(ViewModelProperty, value);
		}

		public static readonly DependencyProperty ViewModelProperty =
			DependencyProperty.Register(nameof(ViewModel), typeof(MapViewModel), typeof(PipelineConsoleWindow),
				new PropertyMetadata(null, OnViewModelChanged));

		// Follow the console VM's frame index / count so the shared playhead tracks the main viewer.
		private static void OnViewModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
		{
			var self = (PipelineConsoleWindow)d;
			if (e.OldValue is MapViewModel oldVm)
			{
				oldVm.PipelineConsole.PropertyChanged -= self.OnConsolePropertyChanged;
			}
			if (e.NewValue is MapViewModel newVm)
			{
				newVm.PipelineConsole.PropertyChanged += self.OnConsolePropertyChanged;
			}
			self.UpdatePlayhead();
		}

		private void OnConsolePropertyChanged(object? sender, PropertyChangedEventArgs e)
		{
			if (e.PropertyName is nameof(PipelineConsoleViewModel.CurrentIndex)
				or nameof(PipelineConsoleViewModel.FrameCount))
			{
				UpdatePlayhead();
			}
		}

		private void OnPlayheadLayerSizeChanged(object sender, SizeChangedEventArgs e) => UpdatePlayhead();

		// Positions the shared playhead over the current frame's centre, asking EqualCellsPanel itself where
		// that cell sits so it lands on the midpoint the panel arranged (no drift). ONE playhead spans every
		// product row: each row has its own EqualCellsPanel strip, but all rows share the console's
		// FrameCount across the same scrubber-column width (PlayheadLayer, inset 80px), so one x serves all.
		private void UpdatePlayhead()
		{
			if (ViewModel is null || PlayheadLayer is null || Playhead is null || PlayheadTransform is null) return;
			var count = ViewModel.PipelineConsole.FrameCount;
			var width = PlayheadLayer.ActualWidth;
			if (count <= 0 || width <= 0)
			{
				Playhead.Visibility = Visibility.Collapsed;
				return;
			}
			Playhead.Visibility = Visibility.Visible;
			var idx = Math.Clamp(ViewModel.PipelineConsole.CurrentIndex, 0, count - 1);
			var centre = EqualCellsPanel.CellCenter(width, count, idx);
			PlayheadTransform.X = Math.Clamp(centre - Playhead.Width / 2, 0, width - Playhead.Width);
		}

		// Closes the console (independent app-wide state; nothing else changes).
		private void OnCloseClick(object sender, RoutedEventArgs e)
		{
			if (ViewModel is not null) ViewModel.IsPipelineConsoleOpen = false;
		}

		// x:Bind function: bool → Visibility.
		public Visibility VisibleWhen(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

		// x:Bind function: INVERSE — the empty-state message shows only when NO loop is loaded.
		public Visibility VisibleWhen2(bool hasLoop) => hasLoop ? Visibility.Collapsed : Visibility.Visible;
	}
}
