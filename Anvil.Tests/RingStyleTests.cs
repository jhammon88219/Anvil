using System.Text.Json;
using Anvil.Models;
using Anvil.Services;
using Anvil.ViewModels;
using Xunit;

namespace Anvil.Tests
{
	/// <summary>
	/// The range rings' LOOK settings. Every value here ends up in a MapLibre paint property, where a bad one
	/// throws mid-render, so the clamps matter — and the records must survive the settings file's JSON round trip,
	/// or a restart would silently drop every ring style back to default.
	/// </summary>
	public class RingStyleTests
	{
		[Fact]
		public void RingStyle_Clamps_AndUnknownLineIsSolid()
		{
			var s = new RingStyle(500, 99, "zigzag").Normalized();
			Assert.Equal(RingStyle.MaxOpacityPct, s.OpacityPct);
			Assert.Equal(RingStyle.MaxWidth, s.Width);
			Assert.Equal(RingLines.Solid, s.Line);

			var low = new RingStyle(-3, double.NaN, RingLines.Dotted).Normalized();
			Assert.Equal(RingStyle.MinOpacityPct, low.OpacityPct);
			Assert.Equal(1, low.Width);
			Assert.Equal(RingLines.Dotted, low.Line);
		}

		[Fact]
		public void LabelStyle_Clamps()
		{
			var s = new RingLabelStyle(0, 100, -1).Normalized();
			Assert.Equal(RingLabelStyle.MinOpacityPct, s.OpacityPct);
			Assert.Equal(RingLabelStyle.MaxSize, s.Size);
			Assert.Equal(RingLabelStyle.MinHalo, s.Halo);
		}

		[Theory]
		[InlineData(0, 0)]
		[InlineData(359.4, 359)]
		[InlineData(359.6, 0)]
		[InlineData(-90, 270)]
		[InlineData(725, 5)]
		[InlineData(double.NaN, 0)]
		public void Bearing_Normalizes(double input, double expected) => Assert.Equal(expected, RingLabelBearing.Normalize(input));

		[Fact]
		public void RingLines_IndexRoundTrips()
		{
			for (var i = 0; i < RingLines.All.Count; i++)
			{
				Assert.Equal(i, RingLines.IndexOf(RingLines.FromIndex(i)));
			}
			Assert.Equal(RingLines.Labels.Count, RingLines.All.Count);
		}

		[Fact]
		public void AppSettings_RingLook_SurvivesJsonRoundTrip()
		{
			var s = new AppSettings
			{
				OutlineRingStyle = new RingStyle(80, 2.5, RingLines.DashDot),
				VelocityRingColor = "#ff4fd8",
				DistanceLabelStyle = new RingLabelStyle(70, 14, 2),
				DistanceLabelBearing = 135,
				ShowDistanceLabelHandle = false,
			};
			var back = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(s))!;
			Assert.Equal(s.OutlineRingStyle, back.OutlineRingStyle);
			Assert.Equal("#FF4FD8", back.VelocityRingColor);
			Assert.Equal(s.DistanceLabelStyle, back.DistanceLabelStyle);
			Assert.Equal(135, back.DistanceLabelBearing);
			Assert.False(back.ShowDistanceLabelHandle);
			Assert.Equal(RingStyle.DistanceDefault, back.DistanceRingStyle); // untouched → default
		}

		[Fact]
		public void AppSettings_RejectsBadColourAndNullStyle()
		{
			var s = new AppSettings { DistanceRingColor = "red'); alert(1", DistanceRingStyle = null! };
			Assert.Equal(ScopeColors.ThemeDefault, s.DistanceRingColor);
			Assert.Equal(RingStyle.DistanceDefault, s.DistanceRingStyle);
		}

		[Fact]
		public void StyleJson_CarriesEveryPart_OpacityAsFraction()
		{
			var s = new AppSettings { VelocityRingStyle = new RingStyle(40, 2, RingLines.Solid), DistanceLabelBearing = 90 };
			using var doc = JsonDocument.Parse(RangeRingsViewModel.BuildStyleJson(s));
			var root = doc.RootElement;
			Assert.Equal(0.4, root.GetProperty("vel").GetProperty("op").GetDouble(), 6);
			Assert.Equal("solid", root.GetProperty("vel").GetProperty("line").GetString());
			Assert.Equal(JsonValueKind.Null, root.GetProperty("refl").GetProperty("color").ValueKind); // outline = CSS var
			Assert.Equal(90, root.GetProperty("bearing").GetDouble());
			Assert.True(root.GetProperty("handle").GetBoolean());
			Assert.Equal(10, root.GetProperty("label").GetProperty("size").GetDouble());
			Assert.DoesNotContain("'", RangeRingsViewModel.BuildStyleJson(s)); // it is single-quoted into a script
		}
	}
}
