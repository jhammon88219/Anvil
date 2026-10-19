using System;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using Anvil.Models;
using Microsoft.Extensions.Logging;

namespace Anvil.Services
{
	/// <summary>
	/// NOAA Weather Wire Service Open Interface as a live source: an XMPP client of <c>nwws-oi.weather.gov:5222</c>
	/// (STARTTLS → SASL PLAIN with the NWS-issued account → bind → join the ONE room <c>nwws@conference.…</c>), every
	/// product arriving as a groupchat message whose <c>&lt;x xmlns="nwws-oi"&gt;</c> carries the text and its header.
	/// </summary>
	/// <remarks>
	/// ⚠️ HAND-ROLLED, ON PURPOSE: Core's only runtime dependency is SharpCompress, and the slice of XMPP NWWS-OI needs is
	/// small. ⚠️ The XML is read element by element WITHOUT read-ahead (<see cref="ReadElementAsync"/>) — the
	/// server sends nothing after <c>&lt;proceed/&gt;</c> or a features list until we answer, so a reader that peeked one
	/// node further would wait forever (why <c>XNode.ReadFromAsync</c> is not used).
	/// ⚠️ GAPS: each product's <c>id</c> is "&lt;process&gt;.&lt;sequence&gt;"; within one process a skipped sequence is
	/// a missed product → <see cref="ILiveSourceSink.Gap"/>. Tracking restarts per connection (a reconnect's catch-up
	/// covers what fell in the drop). ⚠️ One account = one computer (NWS terms), so the room nick is randomised per
	/// connection only to dodge a stale nick after a drop.
	/// ⚠️ Untested against the live server until the user's account arrives (built 2026-10-19 from the protocol docs).
	/// </remarks>
	public sealed class NwwsOiSource : ILiveProductSource
	{
		internal const string Host = "nwws-oi.weather.gov";
		private const int Port = 5222;
		private const string Room = "nwws@conference.nwws-oi.weather.gov";
		private static readonly TimeSpan PingEvery = TimeSpan.FromSeconds(60);
		private static readonly TimeSpan SilentLimit = TimeSpan.FromSeconds(180);
		private static readonly TimeSpan RefusedRetry = TimeSpan.FromMinutes(5);

		private readonly string _user;
		private readonly string _password;
		private readonly ILogger _logger;

		public NwwsOiSource(string user, string password, ILogger logger)
		{
			_user = user;
			_password = password;
			_logger = logger;
		}

		public async Task RunAsync(ILiveSourceSink sink, CancellationToken ct)
		{
			var failures = 0;
			while (true)
			{
				ct.ThrowIfCancellationRequested();
				TimeSpan wait;
				try
				{
					await using var session = await Session.OpenAsync(_user, _password, _logger, ct).ConfigureAwait(false);
					failures = 0;
					await session.ListenAsync(sink, ct).ConfigureAwait(false);
					sink.Down("NOAA Weather Wire closed the connection");
					wait = TimeSpan.FromSeconds(5);
				}
				catch (OperationCanceledException) when (ct.IsCancellationRequested)
				{
					throw;
				}
				catch (SignInRefusedException)
				{
					sink.Down("NOAA Weather Wire refused the sign-in");
					wait = RefusedRetry;
				}
				catch (Exception ex)
				{
					failures++;
					_logger.LogInformation("NWWS-OI connection failed ({Count}): {Message}", failures, ex.Message);
					sink.Down("NOAA Weather Wire isn't answering");
					// 10 s, 20 s, 40 s… capped at 2 min.
					wait = TimeSpan.FromSeconds(Math.Min(120, 10 * Math.Pow(2, Math.Min(4, failures - 1))));
				}
				await Task.Delay(wait, ct).ConfigureAwait(false);
			}
		}

		/// <summary>Signs in and straight back out — Settings' "Test connection". Plain-words result, never throws.</summary>
		internal static async Task<string> TestAsync(string user, string password, ILogger logger, CancellationToken ct)
		{
			if (string.IsNullOrWhiteSpace(user) || string.IsNullOrEmpty(password)) { return "Enter the user name and password first."; }
			using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
			timeout.CancelAfter(TimeSpan.FromSeconds(20));
			try
			{
				await using var session = await Session.OpenAsync(user.Trim(), password, logger, timeout.Token).ConfigureAwait(false);
				return "Signed in. The account works.";
			}
			catch (SignInRefusedException)
			{
				return "NOAA Weather Wire refused that user name or password.";
			}
			catch (OperationCanceledException) when (!ct.IsCancellationRequested)
			{
				return "NOAA Weather Wire didn't answer within 20 seconds.";
			}
			catch (Exception ex)
			{
				return $"Couldn't reach NOAA Weather Wire: {ex.Message}";
			}
		}

		/// <summary>A product from a message's <c>&lt;x&gt;</c>, or null. Internal for tests.</summary>
		internal static LiveProduct? ProductOf(XElement x, DateTimeOffset received)
		{
			var awips = (string?)x.Attribute("awipsid");
			var id = (string?)x.Attribute("id");
			if (string.IsNullOrWhiteSpace(awips) || string.IsNullOrWhiteSpace(id)) { return null; }
			var issued = DateTimeOffset.TryParse((string?)x.Attribute("issue"), System.Globalization.CultureInfo.InvariantCulture,
				System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var t) ? t : received;
			return new LiveProduct(id.Trim(), awips.Trim().ToUpperInvariant(), ((string?)x.Attribute("cccc") ?? "").Trim(), issued, received);
		}

		/// <summary>Splits "&lt;process&gt;.&lt;sequence&gt;". Internal for tests.</summary>
		internal static bool TryParseSequence(string id, out long process, out long sequence)
		{
			process = sequence = 0;
			var dot = id.LastIndexOf('.');
			return dot > 0
				&& long.TryParse(id.AsSpan(0, dot), out process)
				&& long.TryParse(id.AsSpan(dot + 1), out sequence);
		}

		/// <summary>The no-read-ahead element builder (see the class remarks). Internal for tests.</summary>
		internal static Task<XElement> ReadElementAsync(XmlReader r) => Session.ReadElementAsync(r);

		private sealed class SignInRefusedException : Exception
		{
		}

		/// <summary>One XMPP connection: open (TLS + sign-in + bind), then <see cref="ListenAsync"/> until it drops.</summary>
		private sealed class Session : IAsyncDisposable
		{
			private static readonly XNamespace Streams = "http://etherx.jabber.org/streams";
			private static readonly XNamespace Tls = "urn:ietf:params:xml:ns:xmpp-tls";
			private static readonly XNamespace Sasl = "urn:ietf:params:xml:ns:xmpp-sasl";
			private static readonly XNamespace Nwws = "nwws-oi";

			private readonly TcpClient _tcp;
			private readonly ILogger _logger;
			private readonly SemaphoreSlim _writeLock = new(1, 1);
			private readonly string _nick;
			private Stream _stream;
			private XmlReader? _reader;
			private long _lastHeardTicks;

			private Session(TcpClient tcp, ILogger logger, string user)
			{
				_tcp = tcp;
				_logger = logger;
				_stream = tcp.GetStream();
				_nick = $"{user}-anvil-{Random.Shared.Next(1000, 9999)}";
			}

			public static async Task<Session> OpenAsync(string user, string password, ILogger logger, CancellationToken ct)
			{
				var tcp = new TcpClient();
				var session = new Session(tcp, logger, user);
				try
				{
					await tcp.ConnectAsync(Host, Port, ct).ConfigureAwait(false);
					session._stream = tcp.GetStream();

					// 1) Plain stream → STARTTLS.
					var features = await session.StartStreamAsync(ct).ConfigureAwait(false);
					if (features.Element(Tls + "starttls") is null) { throw new IOException("the server offered no TLS"); }
					await session.SendAsync("<starttls xmlns='urn:ietf:params:xml:ns:xmpp-tls'/>", ct).ConfigureAwait(false);
					if ((await session.NextElementAsync(ct).ConfigureAwait(false)).Name != Tls + "proceed") { throw new IOException("TLS was refused"); }
					var ssl = new SslStream(session._stream, leaveInnerStreamOpen: false);
					await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = Host }, ct).ConfigureAwait(false);
					session._stream = ssl;

					// 2) Encrypted stream → SASL PLAIN.
					await session.StartStreamAsync(ct).ConfigureAwait(false);
					var plain = Convert.ToBase64String(Encoding.UTF8.GetBytes($"\0{user}\0{password}"));
					await session.SendAsync($"<auth xmlns='urn:ietf:params:xml:ns:xmpp-sasl' mechanism='PLAIN'>{plain}</auth>", ct).ConfigureAwait(false);
					var answer = await session.NextElementAsync(ct).ConfigureAwait(false);
					if (answer.Name == Sasl + "failure") { throw new SignInRefusedException(); }
					if (answer.Name != Sasl + "success") { throw new IOException("unexpected sign-in answer"); }

					// 3) Signed-in stream → bind a resource, open the session.
					await session.StartStreamAsync(ct).ConfigureAwait(false);
					var resource = SecurityElement.Escape($"anvil-{Random.Shared.Next(1000, 9999)}");
					await session.SendAsync($"<iq type='set' id='bind1'><bind xmlns='urn:ietf:params:xml:ns:xmpp-bind'><resource>{resource}</resource></bind></iq>", ct).ConfigureAwait(false);
					var bound = await session.AwaitIqAsync("bind1", ct).ConfigureAwait(false);
					if ((string?)bound.Attribute("type") != "result") { throw new IOException("the server refused the resource"); }
					await session.SendAsync("<iq type='set' id='sess1'><session xmlns='urn:ietf:params:xml:ns:xmpp-session'/></iq>", ct).ConfigureAwait(false);
					await session.AwaitIqAsync("sess1", ct).ConfigureAwait(false); // optional on modern servers; either answer is fine
					return session;
				}
				catch
				{
					await session.DisposeAsync().ConfigureAwait(false);
					throw;
				}
			}

			/// <summary>Joins the room and reports products until the connection drops (returns) or fails (throws).</summary>
			public async Task ListenAsync(ILiveSourceSink sink, CancellationToken ct)
			{
				await SendAsync("<presence/>", ct).ConfigureAwait(false);
				var nick = SecurityElement.Escape(_nick);
				await SendAsync($"<presence to='{Room}/{nick}'><x xmlns='http://jabber.org/protocol/muc'><history maxchars='0'/></x></presence>", ct).ConfigureAwait(false);

				using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
				MarkHeard();
				var keepAlive = KeepAliveAsync(stop.Token);
				var joined = false;
				long? process = null;
				long sequence = 0;
				try
				{
					while (true)
					{
						var el = await NextElementAsync(ct).ConfigureAwait(false);
						MarkHeard();
						switch (el.Name.LocalName)
						{
							case "presence" when ((string?)el.Attribute("from") ?? "").StartsWith(Room + "/" + _nick, StringComparison.OrdinalIgnoreCase):
								if ((string?)el.Attribute("type") == "error") { throw new IOException("the room refused the join"); }
								if (!joined) { joined = true; sink.Connected(); }
								break;
							case "message":
								if (el.Element(Nwws + "x") is not { } x || ProductOf(x, DateTimeOffset.UtcNow) is not { } product) { break; }
								if (!joined) { joined = true; sink.Connected(); }
								if (TryParseSequence(product.ProductId, out var p, out var s))
								{
									if (process == p && s > sequence + 1) { sink.Gap((int)Math.Min(int.MaxValue, s - sequence - 1)); }
									process = p;
									sequence = s;
								}
								sink.Product(product);
								break;
							case "iq" when (string?)el.Attribute("type") == "get":
								// Answer the server's pings (and refuse nothing else noisily): an empty result.
								var id = SecurityElement.Escape((string?)el.Attribute("id") ?? "");
								var from = SecurityElement.Escape((string?)el.Attribute("from") ?? Host);
								await SendAsync($"<iq type='result' id='{id}' to='{from}'/>", ct).ConfigureAwait(false);
								break;
						}
					}
				}
				catch (IOException ex) when (!ct.IsCancellationRequested)
				{
					_logger.LogInformation("NWWS-OI connection ended: {Message}", ex.Message);
				}
				finally
				{
					stop.Cancel();
					try { await keepAlive.ConfigureAwait(false); } catch (OperationCanceledException) { }
				}
			}

			// Pings the server every minute; a connection silent for 3 minutes is closed (the read then throws).
			private async Task KeepAliveAsync(CancellationToken ct)
			{
				var n = 0;
				while (true)
				{
					await Task.Delay(PingEvery, ct).ConfigureAwait(false);
					if (DateTime.UtcNow - new DateTime(Interlocked.Read(ref _lastHeardTicks), DateTimeKind.Utc) > SilentLimit)
					{
						_logger.LogInformation("NWWS-OI silent for {Seconds:0} s — reconnecting", SilentLimit.TotalSeconds);
						_tcp.Dispose();
						return;
					}
					try { await SendAsync($"<iq type='get' id='ping{++n}' to='{Host}'><ping xmlns='urn:xmpp:ping'/></iq>", ct).ConfigureAwait(false); }
					catch (Exception ex) when (ex is not OperationCanceledException) { _tcp.Dispose(); return; }
				}
			}

			private void MarkHeard() => Interlocked.Exchange(ref _lastHeardTicks, DateTime.UtcNow.Ticks);

			// Opens a (new) stream on the current transport and returns the server's features.
			private async Task<XElement> StartStreamAsync(CancellationToken ct)
			{
				await SendAsync($"<?xml version='1.0'?><stream:stream to='{Host}' xmlns='jabber:client' xmlns:stream='http://etherx.jabber.org/streams' version='1.0'>", ct).ConfigureAwait(false);
				_reader = XmlReader.Create(_stream, new XmlReaderSettings
				{
					Async = true,
					CloseInput = false,
					DtdProcessing = DtdProcessing.Prohibit,
					IgnoreWhitespace = true,
					IgnoreComments = true,
				});
				while (await _reader.ReadAsync().ConfigureAwait(false))
				{
					if (_reader.NodeType == XmlNodeType.Element && _reader.Depth == 0) { break; }
				}
				var features = await NextElementAsync(ct).ConfigureAwait(false);
				if (features.Name != Streams + "features") { throw new IOException("unexpected stream start"); }
				return features;
			}

			// Waits for the answer to OUR iq (other traffic before it is dropped — only pings arrive this early).
			private async Task<XElement> AwaitIqAsync(string id, CancellationToken ct)
			{
				while (true)
				{
					var el = await NextElementAsync(ct).ConfigureAwait(false);
					if (el.Name.LocalName == "iq" && (string?)el.Attribute("id") == id) { return el; }
				}
			}

			// The next top-level element of the stream (depth 1).
			private async Task<XElement> NextElementAsync(CancellationToken ct)
			{
				var r = _reader ?? throw new IOException("no stream");
				while (true)
				{
					ct.ThrowIfCancellationRequested();
					if (!await r.ReadAsync().ConfigureAwait(false)) { throw new IOException("the connection closed"); }
					if (r.NodeType == XmlNodeType.EndElement && r.Depth == 0) { throw new IOException("the server ended the stream"); }
					if (r.NodeType != XmlNodeType.Element || r.Depth != 1) { continue; }
					var el = await ReadElementAsync(r).ConfigureAwait(false);
					if (el.Name == Streams + "error") { throw new IOException($"stream error: {el.Elements().FirstOrDefaultName()}"); }
					return el;
				}
			}

			/// <summary>Builds the element the reader is ON, stopping AT its end tag — never reading past it.</summary>
			internal static async Task<XElement> ReadElementAsync(XmlReader r)
			{
				var root = Start(r);
				if (r.IsEmptyElement) { return root; }
				var stack = new System.Collections.Generic.Stack<XElement>();
				stack.Push(root);
				while (await r.ReadAsync().ConfigureAwait(false))
				{
					switch (r.NodeType)
					{
						case XmlNodeType.Element:
							var child = Start(r);
							stack.Peek().Add(child);
							if (!r.IsEmptyElement) { stack.Push(child); }
							break;
						case XmlNodeType.Text:
						case XmlNodeType.CDATA:
						case XmlNodeType.SignificantWhitespace:
							stack.Peek().Add(new XText(await r.GetValueAsync().ConfigureAwait(false)));
							break;
						case XmlNodeType.EndElement:
							stack.Pop();
							if (stack.Count == 0) { return root; }
							break;
					}
				}
				throw new IOException("the connection closed mid-element");
			}

			private static XElement Start(XmlReader r)
			{
				var e = new XElement(XNamespace.Get(r.NamespaceURI) + r.LocalName);
				if (r.MoveToFirstAttribute())
				{
					do
					{
						if (r.Prefix == "xmlns" || (r.Prefix.Length == 0 && r.LocalName == "xmlns")) { continue; }
						e.SetAttributeValue(XNamespace.Get(r.NamespaceURI) + r.LocalName, r.Value);
					}
					while (r.MoveToNextAttribute());
					r.MoveToElement();
				}
				return e;
			}

			private async Task SendAsync(string xml, CancellationToken ct)
			{
				var bytes = Encoding.UTF8.GetBytes(xml);
				await _writeLock.WaitAsync(ct).ConfigureAwait(false);
				try
				{
					await _stream.WriteAsync(bytes, ct).ConfigureAwait(false);
					await _stream.FlushAsync(ct).ConfigureAwait(false);
				}
				finally
				{
					_writeLock.Release();
				}
			}

			public async ValueTask DisposeAsync()
			{
				try
				{
					// A polite close when the stream is still up; a dead one just throws, which is fine.
					using var quick = new CancellationTokenSource(TimeSpan.FromSeconds(2));
					await SendAsync("</stream:stream>", quick.Token).ConfigureAwait(false);
				}
				catch
				{
				}
				_reader?.Dispose();
				await _stream.DisposeAsync().ConfigureAwait(false);
				_tcp.Dispose();
				_writeLock.Dispose();
			}
		}
	}

	internal static class NwwsXmlExtensions
	{
		/// <summary>The first child's local name ("conflict", "system-shutdown"…) — a stream error's reason.</summary>
		public static string FirstOrDefaultName(this System.Collections.Generic.IEnumerable<XElement> elements)
		{
			foreach (var e in elements) { return e.Name.LocalName; }
			return "unknown";
		}
	}
}
