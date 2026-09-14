namespace Anvil.Models
{
	/// <summary>
	/// Whether Anvil is laid out for one screen or several. Chosen on the Settings window's Window Mode tab and
	/// read by <c>WindowManager</c> at every point where panel behaviour would differ between the two.
	/// </summary>
	/// <remarks>
	/// ⚠️ ONLY <see cref="Single"/> IS BUILT. Every current behaviour — panels placed off the main window,
	/// pinned = owned by the main window, default-locked — is the single-monitor design. <see cref="Multi"/> is
	/// a stub: it can't be selected (MapViewModel.IsMultiMonitorModeBuilt), and each decision point marked
	/// <c>MONITOR MODE (multi: TODO)</c> runs Single's rule for it until a second screen exists to build and
	/// test against.
	/// </remarks>
	public enum MonitorMode
	{
		Single,
		Multi,
	}
}
