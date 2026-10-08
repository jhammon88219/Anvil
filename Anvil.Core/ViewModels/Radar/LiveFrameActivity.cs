using System;

namespace Anvil.ViewModels
{
	/// <summary>
	/// One NowCast live poll, start to finish — the bar's activity slot follows EVERY poll (the user's call, 2026-10-07:
	/// "I like to see what's going on"), not only the ones that bring a scan.
	/// <list type="bullet">
	/// <item><c>Checking</c> — the poll is downloading the live volume's new chunks (<c>Done</c> of <c>Total</c>).</item>
	/// <item><c>Unchanged</c> / <c>Failed</c> — it ended with no newer scan / with an error. The poll is over.</item>
	/// <item><c>Found</c> — it brought a NEWER scan; <c>Decoding</c> — a page worker is building it; <c>Painting</c> — it
	/// is built and in the loop, waiting for the page to draw it; <c>Shown</c> — the page DREW it (radarPainted), or it
	/// is not the frame on screen, so there is nothing to draw.</item>
	/// <item><c>Dropped</c> — the poll or its frame will never finish (a site change, a reload, a tilt switch).</item>
	/// </list>
	/// </summary>
	public enum LiveFrameStage { Checking, Unchanged, Failed, Found, Decoding, Painting, Shown, Dropped }

	/// <summary>One step of a live poll (<see cref="RadarViewModel.LiveFrameActivity"/>) — the bar's activity slot follows
	/// it (<see cref="BarActivityKind.LiveFrame"/>). <c>VolumeTime</c> = the new scan's (Found onward) or the newest scan
	/// already shown (Unchanged); null when unknown. <c>Done</c>/<c>Total</c> = chunks, while Checking.</summary>
	/// <param name="Drawn">Shown only: the page DREW it (radarPainted). False = in the loop but not drawn — not the frame on
	/// screen, or no paint within <c>RadarViewModel.PaintTimeoutMs</c> (a minimized/hidden window draws nothing; found
	/// overnight 2026-10-08, when every frame stalled at "painting" and none reached the timing log).</param>
	public sealed record LiveFrameActivity(LiveFrameStage Stage, string SiteId, DateTimeOffset? VolumeTime, int Done = 0, int Total = 0,
		bool Drawn = false);
}
