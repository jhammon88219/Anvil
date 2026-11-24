using Anvil.Models;
using Anvil.ViewModels;
using Xunit;

namespace Anvil.Tests
{
	// NATIVE-MAP (bolt-on): the dev map-engine switch's Core half — the engine flag and the camera handed across.
	public class MapEngineTests
	{
		[Fact]
		public void Starts_on_MapLibre()
		{
			var engine = new MapEngineViewModel();
			Assert.Equal(0, engine.EngineIndex);
			Assert.False(engine.IsNativeMaps);
		}

		[Fact]
		public void Picking_NativeMaps_raises_IsNativeMaps()
		{
			var engine = new MapEngineViewModel();
			var raised = false;
			engine.PropertyChanged += (_, e) => raised |= e.PropertyName == nameof(MapEngineViewModel.IsNativeMaps);
			engine.EngineIndex = 1;
			Assert.True(engine.IsNativeMaps);
			Assert.True(raised);
		}

		[Fact]
		public void Every_coverage_row_has_a_name_and_one_state()
		{
			foreach (var layer in new MapEngineViewModel().Layers)
			{
				Assert.False(string.IsNullOrWhiteSpace(layer.Name));
				Assert.NotEqual(layer.IsDrawn, layer.IsNotDrawn);
			}
		}

		[Fact]
		public void Parses_the_pages_camera_array()
		{
			var camera = MapCameraState.Parse("[-97.51,35.47,7.25,12.5,30]");
			Assert.Equal(new MapCameraState(-97.51, 35.47, 7.25, 12.5, 30), camera);
		}

		[Theory]
		[InlineData(null)]
		[InlineData("")]
		[InlineData("null")]
		[InlineData("[1,2,3]")]
		[InlineData("[\"a\",2,3,4,5]")]
		[InlineData("not json")]
		public void Anything_else_is_no_camera(string? json) => Assert.Null(MapCameraState.Parse(json));
	}
}
