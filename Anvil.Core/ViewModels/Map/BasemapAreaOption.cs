using System;
using Anvil.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Anvil.ViewModels
{
	/// <summary>
	/// One row of the offline map's Area checklist: the box, the place name and how many NEXRAD radars stand in it
	/// (counted from Anvil's own site list when the row is built, so it's always true — information only, it never
	/// shapes the download).
	/// </summary>
	public sealed class BasemapAreaOption : ObservableObject
	{
		private readonly Action _changed;

		public BasemapAreaOption(BasemapArea area, int radarCount, bool isChecked, Action changed)
		{
			Area = area;
			_isChecked = isChecked;
			_changed = changed;
			RadarCountText = RadarWords(area, radarCount);
		}

		public BasemapArea Area { get; }
		public string Name => Area.Name;

		/// <summary>"155 radars" · "1 radar" · "Radars coming soon" (an area Anvil has no radars in yet, like Canada).</summary>
		public string RadarCountText { get; }

		private bool _isChecked;
		public bool IsChecked
		{
			get => _isChecked;
			set
			{
				if (SetProperty(ref _isChecked, value)) _changed();
			}
		}

		internal static string RadarWords(BasemapArea area, int count) =>
			count == 0 && area.RadarsComingSoon ? "Radars coming soon"
			: count == 1 ? "1 radar"
			: $"{count} radars";
	}
}
