using Anvil.Models;
using Anvil.Services;
using Anvil.ViewModels;
using Xunit;
using static Anvil.Tests.TemporalWindowPersistenceTests;

namespace Anvil.Tests
{
	/// <summary>The Radar memory budget: default from RAM, clamps, the frame cap and the Settings warning tiers.</summary>
	public class RadarMemoryBudgetTests
	{
		[Fact]
		public void Default_is_a_quarter_of_ram_on_the_half_gb_step()
		{
			Assert.Equal(4.0, RadarMemoryBudget.RecommendedGb(16));
			Assert.Equal(8.0, RadarMemoryBudget.RecommendedGb(32));
			Assert.Equal(2.0, RadarMemoryBudget.RecommendedGb(7.8)); // a "8 GB" PC reports a little under
			Assert.Equal(1.0, RadarMemoryBudget.RecommendedGb(2));    // never below the floor
		}

		[Fact]
		public void Saved_value_is_clamped_to_this_pc_and_null_follows_ram()
		{
			Assert.Equal(12.0, RadarMemoryBudget.MaxGb(16));
			Assert.Equal(12.0, RadarMemoryBudget.Effective(30, 16)); // set on a bigger PC
			Assert.Equal(1.0, RadarMemoryBudget.Effective(0.2, 16));
			Assert.Equal(6.5, RadarMemoryBudget.Effective(6.4, 16)); // snapped to the step
			Assert.Equal(4.0, RadarMemoryBudget.Effective(null, 16));
		}

		[Fact]
		public void The_trio_is_always_paid_and_each_dual_pol_pane_adds_one()
		{
			Assert.Equal(3, RadarMemoryBudget.ProductsFor(new[] { "reflectivity" }));
			Assert.Equal(3, RadarMemoryBudget.ProductsFor(new[] { "reflectivity", "velocity", "srv" }));
			Assert.Equal(4, RadarMemoryBudget.ProductsFor(new[] { "reflectivity", "velocity", "srv", "cc" }));
			Assert.Equal(4, RadarMemoryBudget.ProductsFor(new[] { "cc", "cc" }));
			Assert.Equal(7, RadarMemoryBudget.WorstProductsFor(4));
		}

		[Fact]
		public void Frame_cap_matches_the_measured_cost_and_has_a_floor()
		{
			// 4 GB at the measured 4-product layout (Ref/Vel/SRV/CC): 4096 / (17.6 × 4) = 58.
			Assert.Equal(58, RadarMemoryBudget.FrameCap(4, 4));
			// 8 GB holds a whole 2 h SAILS ×3 window (~76 frames) at the same layout.
			Assert.True(RadarMemoryBudget.FrameCap(8, 4) >= 76);
			Assert.Equal(RadarMemoryBudget.MinFrames, RadarMemoryBudget.FrameCap(1, 7));
		}

		[Fact]
		public void Warning_tiers()
		{
			Assert.Equal(RadarMemoryLevel.Fine, RadarMemoryBudget.LevelOf(4, 16));
			Assert.Equal(RadarMemoryLevel.Fine, RadarMemoryBudget.LevelOf(8, 16));   // exactly half is allowed
			Assert.Equal(RadarMemoryLevel.High, RadarMemoryBudget.LevelOf(8.5, 16));
			Assert.Equal(RadarMemoryLevel.Low, RadarMemoryBudget.LevelOf(2, 16));    // 4-pane worst case on the floor
		}

		[Fact]
		public void View_model_persists_and_use_recommended_clears_to_null()
		{
			var settings = new AppSettings();
			var svc = Null<ISettingsService>.Create(new() { ["get_Settings"] = _ => settings });
			var vm = new RadarMemoryViewModel(svc, machineRamGb: 16);

			Assert.Equal(4.0, vm.BudgetGb);
			Assert.True(vm.IsRecommended);
			Assert.Equal("16 GB of RAM", vm.RamText);
			Assert.Equal("4 panes: 33+ frames", vm.FourPaneText);

			vm.BudgetGb = 10;
			Assert.Equal(10.0, settings.RadarMemoryGb);
			Assert.True(vm.IsWarning);

			vm.UseRecommended();
			Assert.Null(settings.RadarMemoryGb);
			Assert.Equal(4.0, vm.BudgetGb);
		}
	}
}
