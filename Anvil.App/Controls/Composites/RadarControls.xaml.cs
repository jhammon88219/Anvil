using System;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Anvil.Models;
using Anvil.Services;
using Anvil.ViewModels;
using Anvil.Layout;
// ⚠️ Color is IMPORTED, never written inline as a Windows.UI.-qualified name: the sibling namespace
// Anvil.Controls.Windows exists, so from inside Anvil.Controls.* a leading "Windows." binds to THAT
// rather than to WinRT and fails to resolve. A using directive sits outside the namespace, where
// "Windows" still means the global one.
using Windows.Foundation;
using Windows.UI;

namespace Anvil.Controls.Composites
{
	/// <summary>
	/// The GLOBAL time module of the bottom bar: the clock + age, the segmented scrubber, the frame counter,
	/// the transport (prev · play/stop · next) and the scan readout. All bound to one <see cref="RadarViewModel"/>.
	///
	/// <para>Nothing per-pane lives here any more. The product selector and the tilt combo moved into the
	/// per-pane notch (<c>Composites/PaneNotchContent</c>); site markers, Inspect and the site itself are on
	/// the tools tier (MapControlsStrip). What is left is the state every pane shares — one site, one camera, one
	/// time cursor — which is why none of it multiplies with the pane layout.</para>
	/// </summary>
	public sealed partial class RadarControls : UserControl
	{
		public RadarControls()
		{
			InitializeComponent();
			HookScanTextSizes();
		}

		/// <summary>The radar view model driving these controls; bound from the host.</summary>
		public RadarViewModel ViewModel
		{
			get => (RadarViewModel)GetValue(ViewModelProperty);
			set => SetValue(ViewModelProperty, value);
		}

		public static readonly DependencyProperty ViewModelProperty =
			DependencyProperty.Register(nameof(ViewModel), typeof(RadarViewModel), typeof(RadarControls),
				new PropertyMetadata(null, OnViewModelChanged));

		// Track the VM's frame index / count so the scrubber playhead can follow (there's no thumb control
		// to two-way-bind now — the segmented scrubber is drawn, and the playhead is positioned by code).
		private static void OnViewModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
		{
			var self = (RadarControls)d;
			if (e.OldValue is RadarViewModel oldVm)
			{
				oldVm.PropertyChanged -= self.OnViewModelPropertyChanged;
				oldVm.Segments.CollectionChanged -= self.OnSegmentsChanged;
			}
			if (e.NewValue is RadarViewModel newVm)
			{
				newVm.PropertyChanged += self.OnViewModelPropertyChanged;
				newVm.Segments.CollectionChanged += self.OnSegmentsChanged; // count changes -> reposition playhead
			}
			self.UpdatePlayhead();
		}

		private void OnSegmentsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) =>
			UpdatePlayhead();

		private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
		{
			if (e.PropertyName is nameof(RadarViewModel.CurrentFrameIndex) or nameof(RadarViewModel.MaxFrameIndex))
			{
				UpdatePlayhead();
			}
		}

		// Play/stop button: while playing, Stop (halt + return to newest); otherwise Play/resume. Mirrors
		// the old dial's center button so the single-button "play + stop in one" behavior is unchanged.
		private void OnPlayStopClick(object sender, RoutedEventArgs e)
		{
			if (ViewModel is null)
			{
				return;
			}
			if (ViewModel.IsPlaying)
			{
				ViewModel.StopRadarLoop();
			}
			else
			{
				ViewModel.ToggleRadarPlay();
			}
		}

		private void OnPrevClick(object sender, RoutedEventArgs e) => ViewModel?.StepFrame(-1);

		private void OnNextClick(object sender, RoutedEventArgs e) => ViewModel?.StepFrame(+1);

		// Refresh button beside the age readout: one live poll now. The VM owns the debounce + cooldown.
		private void OnForceLiveCheckClick(object sender, RoutedEventArgs e) => _ = ViewModel?.ForceLiveFrameCheckAsync();

		// ---- Segmented scrubber interaction ----
		// The scrubber is drawn (cells + playhead), not a Slider, so seeking is handled here: press/drag on
		// the strip maps the pointer x to a frame index. Playback pauses on grab so the drag isn't fought by
		// the advancing loop, and the playhead follows via UpdatePlayhead (VM PropertyChanged / SizeChanged).
		private bool _scrubbing;

		// ⚠️ Where the scrubber's left edge stood at PRESS, in window coordinates. A drag maps the pointer against
		// THIS, not the scrubber's live position: crossing 9:59 → 10:00 widens the clock and slides the scrubber
		// right mid-drag, and a live mapping would then read the same pointer as an earlier frame — back to 9:59,
		// the clock shrinks, the scrubber slides back, and the frame flickers while the hand holds still.
		private double _dragOriginX;

		private void OnScrubberPointerPressed(object sender, PointerRoutedEventArgs e)
		{
			if (ViewModel is null || !ViewModel.IsTransportEnabled || ViewModel.Segments.Count == 0) return;
			_scrubbing = true;
			_dragOriginX = ScrubberHost.TransformToVisual(null).TransformPoint(default).X;
			ScrubberHost.CapturePointer(e.Pointer);
			if (ViewModel.IsPlaying) ViewModel.ToggleRadarPlay(); // pause in place; stays engaged so Stop remains available
			SeekToPointer(e);
		}

		private void OnScrubberPointerMoved(object sender, PointerRoutedEventArgs e)
		{
			if (_scrubbing) SeekToPointer(e);
		}

		private void OnScrubberPointerReleased(object sender, PointerRoutedEventArgs e)
		{
			if (!_scrubbing) return;
			_scrubbing = false;
			ScrubberHost.ReleasePointerCapture(e.Pointer);
		}

		private void OnScrubberSizeChanged(object sender, SizeChangedEventArgs e) => UpdatePlayhead();

		private void SeekToPointer(PointerRoutedEventArgs e)
		{
			if (ViewModel is null) return;
			// Count from the RENDERED cells (Segments), so seek + playhead + cells always agree.
			var count = ViewModel.Segments.Count;
			var width = ScrubberHost.ActualWidth;
			if (count <= 0 || width <= 0) return;
			var x = e.GetCurrentPoint(null).Position.X - _dragOriginX; // against the PRESS origin — see _dragOriginX
			var idx = Math.Clamp((int)Math.Floor(x / (width / count)), 0, count - 1);
			// Can't scrub past the built frontier onto a blank slower-product / undecoded frame (the reachable
			// range grows as the active product builds; reflectivity is the full decoded range).
			idx = Math.Min(idx, ViewModel.MaxReachableFrame);
			if (idx != ViewModel.CurrentFrameIndex) ViewModel.CurrentFrameIndex = idx;
		}

		// Positions the playhead over the current segment's centre. Counts from Segments.Count and asks
		// EqualCellsPanel itself where that cell sits, so the playhead lands exactly on the midpoint the
		// panel arranged (no cumulative drift, and no second copy of its rounding rule to drift from).
		// Called when the frame index / count changes (VM) or the strip resizes.
		private void UpdatePlayhead()
		{
			if (ViewModel is null || ScrubberHost is null || Playhead is null || PlayheadTransform is null) return;
			var count = ViewModel.Segments.Count;
			var width = ScrubberHost.ActualWidth;
			if (count <= 0 || width <= 0)
			{
				Playhead.Visibility = Visibility.Collapsed;
				return;
			}
			Playhead.Visibility = Visibility.Visible;
			var idx = Math.Clamp(ViewModel.CurrentFrameIndex, 0, count - 1);
			var centre = EqualCellsPanel.CellCenter(width, count, idx);
			PlayheadTransform.X = Math.Clamp(centre - Playhead.Width / 2, 0, width - Playhead.Width);
		}

		// Segoe Fluent glyph for the center button: Stop while playing, Play otherwise.
		public string PlayStopGlyph(bool isPlaying) => isPlaying ? "" : "";

		// The clock splits CurrentFrameTimeText ("h:mm tt") at its last space: large digits, small AM/PM.
		// A culture with an empty designator has no space, so it is all digits and no suffix.
		public string ClockDigits(string time)
		{
			var cut = time.LastIndexOf(' ');
			return cut < 0 ? time : time[..cut];
		}

		public string ClockSuffix(string time)
		{
			var cut = time.LastIndexOf(' ');
			return cut < 0 ? string.Empty : time[(cut + 1)..];
		}

		// "N / M" frame counter (1-based) shown under the scrubber. Empty until there's a real multi-frame
		// loop (max 0 = 0 or 1 frames, where the scrubber is disabled anyway) so it isn't a misleading "1 / 1".
		public string FrameCountText(double current, int max) =>
			max <= 0 ? string.Empty : $"{(int)Math.Round(current) + 1} / {max + 1}";

		// The Scan readout: the VOLUME's scan strategy — "VCP 215 · precip · SAILS/MRLE ×2".
		//
		// RadarViewModel.RadarModeText is one formatted string, "VCP 215 · precip · SAILS/MRLE ×2 ·
		// 0.5°×3" (or "VCP ? · 0.5°×3" when the VCP couldn't be read, or "VCP 212 · precip" with no
		// sweep segment at all on the archive path). Everything before the "0.5°" sweep token describes
		// the volume, so that's this row; the token itself is dropped, because "0.5°×3" and
		// "SAILS/MRLE ×2" state the same fact twice (3 sweeps = 1 + 2 extra) and the Tilt row now shows
		// the rendered elevation instead.
		//
		// SAILS belongs HERE, not on the Tilt row: it counts re-scans of the BASE tilt, a property of
		// the volume that holds whichever tilt is on screen. On the Tilt row it could only ever be true
		// for 0.5° and disappeared as soon as a higher tilt was selected.
		//
		// The cut itself is RadarViewModel.ScanStrategyText — shared with the Radar Atlas's Scan mode line.
		public string RadarVcpText(string mode) => RadarViewModel.ScanStrategyText(mode);

		// The Scan block's three VALUES (the labels are XAML), cut from RadarVcpText at its " · " separators.
		// ⚠️ ALWAYS a value — a missing fact is a placeholder, so all three lines show no matter what:
		//   0 VCP:        "212" (from "VCP 212"; "?" when unparsed); before a frame, the state itself ("—", "loading…")
		//   1 Mode:       the regime word, else "—"
		//   2 SAILS/MRLE: "×1" from the "SAILS/MRLE ×1" part; else "n/a" for TDWR (no such scheme), "none" when the
		//                 sweep count WAS read (the full mode keeps a "0.5°×N" token) and there are no extra cuts,
		//                 "—" when it wasn't (the archive path reads the VCP only).
		private const string SailsPrefix = "SAILS/MRLE ";

		// The values that mean "no fact here" — drawn DIMMED by the XAML's stand-in TextBlock.
		private static readonly string[] StandIns = { "—", "?", "none", "n/a", "loading…" };

		private static bool IsStandIn(string value) => Array.IndexOf(StandIns, value) >= 0;

		// ScanValue split for the two TextBlocks sharing each cell: exactly one of these is non-empty.
		public string ScanFact(string mode, int line)
		{
			var value = ScanValue(mode, line);
			return IsStandIn(value) ? string.Empty : value;
		}

		public string ScanStandIn(string mode, int line)
		{
			var value = ScanValue(mode, line);
			return IsStandIn(value) ? value : string.Empty;
		}

		// ===== Clock column sizing =====
		// Cap the age line to the TIME's width, so only the time sizes the clock column (a long replay age trims).
		private void OnClockTimeSizeChanged(object sender, SizeChangedEventArgs e) => AgeText.MaxWidth = e.NewSize.Width;

		public string ScanValue(string mode, int line)
		{
			var parts = RadarVcpText(mode).Split(" · ");
			switch (line)
			{
				case 0:
					return parts[0].StartsWith("VCP ", StringComparison.Ordinal) ? parts[0][4..]
						: parts[0].Length > 0 ? parts[0] : "—";
				case 1:
					return parts.Length > 1 ? parts[1] : "—";
				default:
					foreach (var part in parts)
					{
						if (part.StartsWith(SailsPrefix, StringComparison.Ordinal))
						{
							return part[SailsPrefix.Length..];
						}
					}
					if (parts.Length > 1 && parts[1].StartsWith("TDWR", StringComparison.Ordinal))
					{
						return "n/a";
					}
					if (parts[0] == "VCP ?")
					{
						return "—"; // an unread VCP says nothing trustworthy about its cuts
					}
					return mode.Contains("0.5°×", StringComparison.Ordinal) ? "none" : "—";
			}
		}

		/// <summary>Raised when any scan line's text changes width — the site picker on the tools tier tracks the
		/// end of the longest one (MainWindow.AlignSitePicker).</summary>
		public event EventHandler? ScanTextEdgeChanged;

		// Every TextBlock in the scan block (labels + fact/stand-in values) reports its size changes; labels never
		// change, so in practice only a value's new text fires it.
		private void HookScanTextSizes()
		{
			foreach (var child in ScanBlock.Children)
			{
				if (child is FrameworkElement text)
				{
					text.SizeChanged += (_, _) => ScanTextEdgeChanged?.Invoke(this, EventArgs.Empty);
				}
			}
		}

		/// <summary>The right end of the LONGEST scan line's text, in <paramref name="root"/>'s coordinates — where the
		/// tools tier's site picker puts its right edge. The values are LEFT-aligned (ScanValueStyle), so a value's
		/// ActualWidth is its text, not its column; empty ones measure 0 and never win. NaN before layout.</summary>
		public double ScanTextRightEdge(UIElement root)
		{
			double edge = double.NaN;
			foreach (var child in ScanBlock.Children)
			{
				if (child is FrameworkElement text && text.ActualWidth > 0)
				{
					var right = text.TransformToVisual(root).TransformPoint(new Point(text.ActualWidth, 0)).X;
					edge = double.IsNaN(edge) ? right : Math.Max(edge, right);
				}
			}
			return edge;
		}

		// Its tooltip explains THIS site's pattern — words from RadarGlossary (VcpCatalog), never XAML.
		public string ScanTooltip(string mode) => RadarGlossary.ScanPatternTooltip(mode);

		// Dim the scrubber while the transport isn't enabled yet (Grid has no IsEnabled; interaction is
		// blocked via IsHitTestVisible + the pointer-handler guard, this is the visual cue).
		public double ScrubberOpacity(bool enabled) => enabled ? 1.0 : 0.4;

		// Foreground for the staleness readout, ramped continuously by the newest frame's age in minutes:
		// green while fresh → amber at ~12 min (the Live→Recent boundary) → red at ~30 min (Recent→Stale),
		// clamped red beyond. The two knees match RadarStatus's freshness thresholds so the color and the
		// status dot agree. Null age (no frame yet, "—") reads as a muted gray.
		public Brush AgeBrush(double? minutes)
		{
			if (minutes is not double m)
			{
				return new SolidColorBrush(Color.FromArgb(255, 0x8A, 0x8A, 0x8A));
			}

			var fresh = Color.FromArgb(255, 0x3F, 0xB9, 0x50); // green
			var mid = Color.FromArgb(255, 0xE3, 0xB3, 0x41);   // amber
			var stale = Color.FromArgb(255, 0xF8, 0x51, 0x49); // red

			Color c = m <= 12 ? Lerp(fresh, mid, m / 12.0)
				: m <= 30 ? Lerp(mid, stale, (m - 12) / 18.0)
				: stale;
			return new SolidColorBrush(c);
		}

		private static Color Lerp(Color a, Color b, double t)
		{
			t = Math.Clamp(t, 0, 1);
			return Color.FromArgb(255,
				(byte)(a.R + (b.R - a.R) * t),
				(byte)(a.G + (b.G - a.G) * t),
				(byte)(a.B + (b.B - a.B) * t));
		}
	}
}
