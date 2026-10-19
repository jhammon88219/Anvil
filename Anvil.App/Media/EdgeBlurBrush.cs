// EDGE-BLUR (trial, 2026-10-19) — a bolt-on: this file, the Win2D package line in Anvil.App.csproj, the EdgeBlur*
// resources in Styles.xaml and the EDGE-BLUR block in TemporalWindowActivityExpander.xaml. Grep EDGE-BLUR to excise.
//
// EdgeBlurBrush — a BRUSH, declared in XAML as an element's Background, that blurs whatever is drawn BEHIND that
// element and fades the blur out in one direction, so content eases into a surface's edge instead of meeting a line.
//
//     Fade="Up"       ░░░▒▒▒▓▓▓  ← strongest at the BOTTOM (the surface edge), gone at the top
//     Fade="Left"     strongest at the RIGHT edge, gone at the left   (the strip left of a tab)
//     Fade="Right"    strongest at the LEFT edge, gone at the right   (the strip right of a tab)
//     Fade="UpLeft"   a quarter disc strongest at the BOTTOM-RIGHT corner (above-left of a tab)
//     Fade="UpRight"  a quarter disc strongest at the BOTTOM-LEFT corner  (above-right of a tab)
//
// ⚠️ Why code: XAML has no blur and no opacity mask. The composition chain (backdrop → Gaussian blur → gradient
// mask) can only be built here; the ELEMENTS it paints are declared in XAML, per the WinUI rule.
// ⚠️ The mask is ALPHA only (white with rising alpha), never a theme colour — a blur has no colour of its own.
using System;
using System.Numerics;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Anvil.Media
{
	public enum EdgeBlurFade { Up, Left, Right, UpLeft, UpRight }

	public sealed partial class EdgeBlurBrush : XamlCompositionBrushBase
	{
		public static readonly DependencyProperty BlurAmountProperty =
			DependencyProperty.Register(nameof(BlurAmount), typeof(double), typeof(EdgeBlurBrush),
				new PropertyMetadata(5.0, OnLookChanged));

		public static readonly DependencyProperty FadeProperty =
			DependencyProperty.Register(nameof(Fade), typeof(EdgeBlurFade), typeof(EdgeBlurBrush),
				new PropertyMetadata(EdgeBlurFade.Up, OnLookChanged));

		/// <summary>The blur's standard deviation in DIPs at the strong end.</summary>
		public double BlurAmount
		{
			get => (double)GetValue(BlurAmountProperty);
			set => SetValue(BlurAmountProperty, value);
		}

		/// <summary>Which way the blur fades OUT (see the sketch at the top of the file).</summary>
		public EdgeBlurFade Fade
		{
			get => (EdgeBlurFade)GetValue(FadeProperty);
			set => SetValue(FadeProperty, value);
		}

		private CompositionBackdropBrush? _backdrop;
		private CompositionEffectBrush? _blurred;
		private CompositionGradientBrush? _mask;

		protected override void OnConnected() => Build();

		protected override void OnDisconnected() => Teardown();

		private static void OnLookChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
		{
			var brush = (EdgeBlurBrush)d;
			if (brush.CompositionBrush is null) return;   // not connected yet — OnConnected builds with the new value
			brush.Teardown();
			brush.Build();
		}

		private void Build()
		{
			if (CompositionBrush is not null) return;
			var compositor = CompositionTarget.GetCompositorForCurrentThread();

			// HARD border mode clamps the edge pixels instead of blending in transparent black, so the strip's own
			// edges don't darken.
			var blur = new GaussianBlurEffect
			{
				Name = "Blur",
				BlurAmount = (float)BlurAmount,
				BorderMode = EffectBorderMode.Hard,
				Optimization = EffectOptimization.Balanced,
				Source = new CompositionEffectSourceParameter("Backdrop"),
			};
			_backdrop = compositor.CreateBackdropBrush();
			_blurred = compositor.CreateEffectFactory(blur).CreateBrush();
			_blurred.SetSourceParameter("Backdrop", _backdrop);

			_mask = MaskFor(compositor, Fade);
			var masked = compositor.CreateMaskBrush();
			masked.Source = _blurred;
			masked.Mask = _mask;
			CompositionBrush = masked;
		}

		// Three stops, like the ShadowFade* gradients: a two-stop ramp reads as a band with an edge on it.
		private static CompositionGradientBrush MaskFor(Compositor compositor, EdgeBlurFade fade)
		{
			CompositionGradientBrush mask;
			switch (fade)
			{
				case EdgeBlurFade.UpLeft:
				case EdgeBlurFade.UpRight:
					var radial = compositor.CreateRadialGradientBrush();
					radial.EllipseCenter = fade == EdgeBlurFade.UpLeft ? new Vector2(1, 1) : new Vector2(0, 1);
					radial.EllipseRadius = new Vector2(1, 1);
					mask = radial;
					AddStops(compositor, mask, strongAtZero: true);
					return mask;
				default:
					var linear = compositor.CreateLinearGradientBrush();
					(linear.StartPoint, linear.EndPoint) = fade switch
					{
						EdgeBlurFade.Left => (new Vector2(0, 0), new Vector2(1, 0)),
						EdgeBlurFade.Right => (new Vector2(1, 0), new Vector2(0, 0)),
						_ => (new Vector2(0, 0), new Vector2(0, 1)),
					};
					mask = linear;
					AddStops(compositor, mask, strongAtZero: false);
					return mask;
			}
		}

		private static void AddStops(Compositor compositor, CompositionGradientBrush mask, bool strongAtZero)
		{
			(float offset, byte alpha)[] ramp = { (0f, 0), (0.55f, 0x70), (1f, 0xFF) };
			foreach (var (offset, alpha) in ramp)
				mask.ColorStops.Add(compositor.CreateColorGradientStop(
					strongAtZero ? 1f - offset : offset, Color.FromArgb(alpha, 0xFF, 0xFF, 0xFF)));
		}

		private void Teardown()
		{
			CompositionBrush?.Dispose();
			CompositionBrush = null;
			_mask?.Dispose(); _mask = null;
			_blurred?.Dispose(); _blurred = null;
			_backdrop?.Dispose(); _backdrop = null;
		}
	}
}
