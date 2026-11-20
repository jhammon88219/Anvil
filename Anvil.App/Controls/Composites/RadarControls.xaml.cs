using System;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Anvil.Models;
using Anvil.Services;
using Anvil.ViewModels;
// ⚠️ Color is IMPORTED, never written inline as a Windows.UI.-qualified name: the sibling namespace
// Anvil.Controls.Windows exists, so from inside Anvil.Controls.* a leading "Windows." binds to THAT
// rather than to WinRT and fails to resolve. A using directive sits outside the namespace, where
// "Windows" still means the global one.
using Windows.Foundation;
using Windows.UI;

namespace Anvil.Controls.Composites
{
	/// <summary>
	/// The GLOBAL time module — ONE row in the time strip (scrubber on the window's midpoint). Per-pane
	/// product/ramp/tilt are Composites/PaneHeader; the site is the top bar's Radar menu (Composites/RadarMenu). Holds the
	/// clock · age, the scrubber, the transport, the frame counter, check-now and the one-line scan readout, all bound to
	/// one <see cref="RadarViewModel"/> — the state every pane shares (one site, one camera, one time cursor), which is
	/// why none of it multiplies with the pane layout.
	/// </summary>
	public sealed partial class RadarControls : UserControl
	{
		public RadarControls()
		{
			InitializeComponent();
		}

		/// <summary>The radar view model driving these controls; bound from the host.</summary>
		public RadarViewModel ViewModel
		{
			get => (RadarViewModel)GetValue(ViewModelProperty);
			set => SetValue(ViewModelProperty, value);
		}

		public static readonly DependencyProperty ViewModelProperty =
			DependencyProperty.Register(nameof(ViewModel), typeof(RadarViewModel), typeof(RadarControls),
				new PropertyMetadata(null));

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
		// The scrubber is drawn (Primitives/ClassicScrubber or DotMatrixScrubber), not a Slider, so seeking is
		// handled here: press/drag on the strip maps the pointer x to a frame index — EQUAL slots for the classic cells,
		// DotMatrixScrubber.IndexAt for the dot field (its column→frame table, shares ±1 column). Playback pauses on grab so the drag isn't fought by the advancing loop; the drawn
		// scrubber follows CurrentFrameIndex on its own.
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

		private void SeekToPointer(PointerRoutedEventArgs e)
		{
			if (ViewModel is null) return;
			// Count from the RENDERED cells (Segments), so seek + playhead + cells always agree.
			var count = ViewModel.Segments.Count;
			var width = ScrubberHost.ActualWidth;
			if (count <= 0 || width <= 0) return;
			var x = e.GetCurrentPoint(null).Position.X - _dragOriginX; // against the PRESS origin — see _dragOriginX
			// The dot matrix is one centred run (its columns, not equal slots of the strip) — ask it; classic = slots.
			var idx = ViewModel.IsDotMatrixScrubber && DotScrubber.IndexAt(x) is var dot and >= 0
				? dot
				: Math.Clamp((int)Math.Floor(x / (width / count)), 0, count - 1);
			// Can't scrub past the built frontier onto a blank slower-product / undecoded frame (the reachable
			// range grows as the active product builds; reflectivity is the full decoded range).
			idx = Math.Min(idx, ViewModel.MaxReachableFrame);
			if (idx != ViewModel.CurrentFrameIndex) ViewModel.CurrentFrameIndex = idx;
		}

		// Segoe Fluent glyph for the center button: Stop while playing, Play otherwise.
		public string PlayStopGlyph(bool isPlaying) => isPlaying ? "" : "";

		// ===== THE STRIP'S FIXED WIDTHS (see the XAML header) =====
		// ⚠️ MainWindow.LayoutRadarStrip reads LeftNeed / RightNeed to decide whether a side fits beside a temporal pane,
		// so these are CONSTANTS — a width that followed the text would make the panes jump as the clock or VCP changed.
		// ModelConsole uses the SAME numbers (its slots say other things in the same places).
		public const double ClockBlockWidthPx = 220;
		public const double FrameCountWidthPx = 56;
		public const double ScanLineWidthPx = 260;
		public const double ScrubberWidthPx = 420;
		public const double BlockGap = 16;   // scrubber ↔ a side block, and a side block ↔ the strip's end

		public double ClockBlockWidth => ClockBlockWidthPx;
		public double FrameCountWidth => FrameCountWidthPx;
		public double ScanLineWidth => ScanLineWidthPx;

		/// <summary>How far the strip must reach LEFT of the scrubber: gap + clock block + edge gap.</summary>
		public static double LeftNeed => BlockGap + ClockBlockWidthPx + BlockGap;

		/// <summary>How far the strip must reach RIGHT of the scrubber: the transport block (its own leading gap included;
		/// fixed buttons + the reserved count, so its width never changes once laid out) + gap + the reserved scan line +
		/// edge gap. 0 before layout (the host re-lays on this control's first SizeChanged).</summary>
		public double RightNeed =>
			TransportBlock.ActualWidth <= 0 ? 0
				: TransportBlock.ActualWidth + TransportBlock.Margin.Left + BlockGap + ScanLineWidthPx + BlockGap;

		/// <summary>Where the strip's ends are, measured from the window's edges (MainWindow sets it): the side blocks stay
		/// inside them while the scrubber stays on the window's midpoint.</summary>
		public Thickness SideInsets
		{
			get => (Thickness)GetValue(SideInsetsProperty);
			set => SetValue(SideInsetsProperty, value);
		}

		public static readonly DependencyProperty SideInsetsProperty =
			DependencyProperty.Register(nameof(SideInsets), typeof(Thickness), typeof(RadarControls), new PropertyMetadata(default(Thickness)));

		// The scan line closes the strip on the right: its right end is the strip's, less the edge gap.
		public Thickness RightBlockMargin(Thickness insets) => new(BlockGap, 0, insets.Right + BlockGap, 0);

		// "N / M" frame counter (1-based) beside the transport. Empty until there's a real multi-frame
		// loop (max 0 = 0 or 1 frames, where the scrubber is disabled anyway) so it isn't a misleading "1 / 1".
		public string FrameCountText(double current, int max) =>
			max <= 0 ? string.Empty : $"{(int)Math.Round(current) + 1} / {max + 1}";

		// The Scan readout: the VOLUME's scan strategy — "VCP 215 · precip · MESO-SAILS ×2".
		//
		// RadarViewModel.RadarModeText is one formatted string, "VCP 215 · precip · MESO-SAILS ×2 ·
		// 0.5°×3" (or "VCP ? · 0.5°×3" when the VCP couldn't be read, or "VCP 212 · precip" with no
		// sweep segment at all on the archive path). Everything before the "0.5°" sweep token describes
		// the volume, so that's this row; the token itself is dropped, because "0.5°×3" and
		// "MESO-SAILS ×2" state the same fact twice (3 sweeps = 1 + 2 extra) and the Tilt row now shows
		// the rendered elevation instead.
		//
		// Rescans belong HERE, not on the Tilt row: a property of the volume that holds whichever tilt is
		// on screen. On the Tilt row SAILS could only ever be true for 0.5° and disappeared as soon as a
		// higher tilt was selected.
		//
		// The cut itself is RadarViewModel.ScanStrategyText — shared with the Radar Atlas's Scan mode line.
		public string RadarVcpText(string mode) => RadarViewModel.ScanStrategyText(mode);

		// The Scan block's three VALUES (the labels are XAML), cut from RadarVcpText at its " · " separators.
		// ⚠️ ALWAYS a value — a missing fact is a placeholder, so all three lines show no matter what:
		//   0 VCP:        "212" (from "VCP 212"; "?" when unparsed); before a frame, the state itself ("—", "loading…")
		//   1 Mode:       the regime word, else "—"
		//   2 Rescans:    the scheme part WHOLE — "SAILS ×1", "MESO-SAILS ×3", "MRLE ×4" (the ROC's names; ×N counts
		//                 extra sweeps for SAILS, TILTS for MRLE); "off" / "pre-SAILS" from the "rescans off" /
		//                 "pre-SAILS" part (no rescans, and the radar's build says why — Level2Format.DescribeMode);
		//                 else "n/a" for TDWR (no such scheme), "none" when the sweep count WAS read but the reason
		//                 wasn't, "—" when the count wasn't read either (the archive path reads the VCP only).
		// ⚠️ These words mirror Level2Format's SailsName / MesoSailsName / MrleName / RescansOffLabel / PreSailsLabel.
		private static readonly string[] SchemePrefixes = { "SAILS ×", "MESO-SAILS ×", "MRLE ×" };
		private const string RescansOffPart = "rescans off";
		private const string PreSailsPart = "pre-SAILS";

		// ===== THE SCAN LINE's tail: " · precip · MRLE ×4" (the VCP is its own Run) =====
		// Words the user can read (2026-11-20): no rescans of any kind — "rescans off", "pre-SAILS", or counted and zero —
		// says "no rescans"; a part with nothing to say (unknown "—", TDWR's "n/a") is LEFT OUT, separator and all.
		public string ScanTail(string mode)
		{
			var tail = string.Empty;
			var regime = ScanValue(mode, 1);
			if (regime != "—") { tail += " · " + regime; }
			var rescans = ScanValue(mode, 2) switch
			{
				"off" or "none" or PreSailsPart => "no rescans",
				"—" or "n/a" => string.Empty,
				var scheme => scheme,
			};
			if (rescans.Length > 0) { tail += " · " + rescans; }
			return tail;
		}

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
						if (Array.Exists(SchemePrefixes, p => part.StartsWith(p, StringComparison.Ordinal)))
						{
							return part;
						}
						if (part == RescansOffPart)
						{
							return "off";
						}
						if (part == PreSailsPart)
						{
							return PreSailsPart;
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
