using System;

namespace Anvil.ViewModels
{
	/// <summary>Where a NEW live frame is on its way to the screen. <c>Found</c> = the poll brought a newer scan (its
	/// bytes are on disk), <c>Decoding</c> = a page worker is building it, <c>Shown</c> = it is in the loop (the moment
	/// the sweep pulse used to fire), <c>Dropped</c> = it never will be (a site change, a reload, a tilt switch).</summary>
	public enum LiveFrameStage { Found, Decoding, Shown, Dropped }

	/// <summary>One step of a new live frame (<see cref="RadarViewModel.LiveFrameActivity"/>) — the bar's activity slot
	/// follows it (<see cref="BarActivityKind.LiveFrame"/>).</summary>
	public sealed record LiveFrameActivity(LiveFrameStage Stage, string SiteId, DateTimeOffset VolumeTime);
}
