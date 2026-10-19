using System;

namespace Anvil.Models
{
	/// <summary>
	/// One NWS text product as a live source announced it. Only what the hub needs to route it: the source's id for it
	/// (dedupe), its AWIPS id ("SVROUN" — the feed it wakes, <see cref="LiveFeedKinds.KindOfProduct"/>), its issuing
	/// office, and when it was issued and received.
	/// </summary>
	public sealed record LiveProduct(string ProductId, string AwipsId, string Office, DateTimeOffset Issued, DateTimeOffset Received);
}
