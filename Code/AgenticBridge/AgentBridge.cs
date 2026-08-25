using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Outbound WebSocket link to whatever an agent is running on this machine.
///
/// The game always dials out - s&box gives game code a WebSocket client and no
/// listener - so the other end has to be listening when we look. Normally that
/// is a single invocation of Assets/agent/sbx.ps1, which binds a port, takes one
/// call and exits, which is why we rescan every second.
///
/// Deliberately NOT a console pipe. The agent can only call the verbs declared in
/// <see cref="AgentVerbs"/>, each of which routes through the game's own commands
/// and RPCs, so host authority, ownership and prop limits all still apply.
/// </summary>
internal sealed class AgentBridge : GameObjectSystem<AgentBridge>
{
	/// <summary>
	/// Connect to the local bridge when a scene starts. Off by default - a game
	/// that quietly opens sockets on launch is not a nice surprise.
	/// </summary>
	[ConVar( "sb.bridge", ConVarFlags.Saved, Help = "Connect to a local agent bridge when a scene starts." )]
	public static bool Enabled { get; set; } = false;

	/// <summary>
	/// Explicit bridge URL, overriding the port search. Must be a hostname, not an
	/// IP literal, and one of the four ports below.
	/// </summary>
	/// <remarks>
	/// Setting this pins one game to one CLI instead of both scanning, which is what
	/// keeps two sessions on the same machine from answering each other's calls. It
	/// cannot widen the port list: <c>WebSocket.Connect</c> gates on
	/// <c>Http.IsAllowedAsync</c>, which only skips the loopback port check when
	/// <c>Http.IsLocalAllowed</c> is set - and that is false for game code, including
	/// game code running under the editor in play mode. Verified by measurement: with
	/// this set to 8443 the game connects, with it set to 9451 it never dials.
	/// </remarks>
	[ConVar( "sb.bridge_url", ConVarFlags.Saved, Help = "Explicit agent bridge URL. Empty tries the allowed local ports in order." )]
	public static string Url { get; set; } = "";

	/// <summary>
	/// The only ports localhost is reachable on, in the order we try them. 8080 first
	/// because 80 and 443 need elevation to bind on Windows, so a bridge is unlikely
	/// to be there. A busy port fails the upgrade and we move on to the next.
	/// </summary>
	/// <remarks>
	/// Four ports, of which two are practically usable, is also the ceiling on how many
	/// games can hold a bridge at once on one machine. Isolated envs past that have to
	/// work through the editor's MCP server instead, which has no such limit.
	/// </remarks>
	private static readonly string[] LocalCandidates =
	{
		"ws://localhost:8080/",
		"ws://localhost:8443/",
		"ws://localhost:80/",
		"ws://localhost:443/"
	};

	/// <summary>Last URL that worked, tried first next time so we stop re-scanning.</summary>
	private string _lastGood;

	private IEnumerable<string> Candidates
	{
		get
		{
			if ( !string.IsNullOrWhiteSpace( Url ) )
				return new[] { Url };

			if ( string.IsNullOrWhiteSpace( _lastGood ) )
				return LocalCandidates;

			return new[] { _lastGood }.Concat( LocalCandidates.Where( x => x != _lastGood ) );
		}
	}

	/// <summary>
	/// How long to wait before scanning the ports again. This is also the latency
	/// floor for a one-shot CLI: it binds a port and waits for us to notice, so a
	/// caller waits half this on average.
	/// </summary>
	private const float RetrySeconds = 1f;

	/// <summary>
	/// How long an attempt is believed to still be running before we start another regardless.
	/// </summary>
	/// <remarks>
	/// Generous - four ports each waiting to fail takes a few seconds. This exists only so a lost
	/// attempt can't wedge the bridge, not to cut a live one short.
	/// </remarks>
	private const float AttemptTimeoutSeconds = 15f;

	/// <summary>
	/// Whether we've already reported that nothing is listening. Retrying every
	/// second forever would otherwise bury the console in identical lines.
	/// </summary>
	private bool _reportedOffline;

	/// <summary>Set by bridge_disconnect, cleared by bridge_connect. Separate from the saved convar.</summary>
	private bool _suspended;

	/// <summary>True while a connect attempt is in flight, so the tick doesn't start a second one.</summary>
	private bool _connecting;

	private RealTimeSince _sinceAttempt;

	private WebSocket _socket;

	public static bool IsLinked => Current?._socket?.IsConnected ?? false;

	public AgentBridge( Scene scene ) : base( scene )
	{
		// unpack regardless of whether the bridge is on, so the Q menu can always
		// show the player what to run
		AgentCli.Install();

		Listen( Stage.StartUpdate, 0, Tick, "AgentBridge" );
	}

	/// <summary>
	/// Keep a link up, one frame at a time.
	/// </summary>
	/// <remarks>
	/// Deliberately a frame listener rather than the <c>while (true) { await }</c> loop this used
	/// to be. Hotload discards async state machines but leaves plain delegates alone, so every code
	/// edit silently killed the old loop and the bridge stayed dead until someone ran
	/// bridge_connect by hand. A tick comes back on its own.
	///
	/// The connect attempt is still async, but it is short-lived: if a hotload lands mid-attempt
	/// the next tick simply starts another.
	/// </remarks>
	private void Tick()
	{
		if ( !Enabled || _suspended )
			return;

		if ( _socket is { IsConnected: true } )
			return;

		// Either an attempt is in flight, or we're waiting before starting the next one. The
		// in-flight guard is bounded rather than believed outright: a hotload discards a running
		// task without unwinding it, so the finally that clears the flag never runs and the bridge
		// would sit forever waiting on an attempt that no longer exists.
		if ( _sinceAttempt < (_connecting ? AttemptTimeoutSeconds : RetrySeconds) )
			return;

		_sinceAttempt = 0;
		_connecting = true;

		_ = ConnectAsync();
	}

	[ConCmd( "bridge_connect", Help = "Connect to the local agent bridge now." )]
	public static void ConnectCommand()
	{
		if ( Current is null )
		{
			Log.Warning( "[bridge] no active scene" );
			return;
		}

		if ( IsLinked )
		{
			Log.Info( "[bridge] already connected" );
			return;
		}

		// The tick reconnects on its own now, so this only lifts a manual disconnect and skips
		// the wait until the next retry.
		Current._suspended = false;
		Current._sinceAttempt = RetrySeconds;
	}

	[ConCmd( "bridge_disconnect", Help = "Drop the agent bridge connection." )]
	public static void DisconnectCommand()
	{
		if ( Current is null ) return;

		Current._suspended = true;
		Current.Drop();

		Log.Info( "[bridge] disconnecting" );
	}

	[ConCmd( "bridge_status", Help = "Report agent bridge connection state." )]
	public static void StatusCommand()
	{
		var target = !string.IsNullOrWhiteSpace( Url ) ? Url
			: Current?._lastGood is { Length: > 0 } last ? $"{last} (auto)"
			: "auto";

		Log.Info( $"[bridge] enabled={Enabled} url={target} connected={IsLinked} verbs={AgentVerbs.All.Count}" );
	}

	/// <summary>
	/// One pass over the candidate ports, stopping at the first that answers.
	/// </summary>
	private async Task ConnectAsync()
	{
		try
		{
			foreach ( var url in Candidates )
			{
				if ( Current != this || _suspended )
					return;

				if ( await TryLinkAsync( url ) )
					return;
			}
		}
		finally
		{
			_connecting = false;
		}
	}

	/// <summary>
	/// Attempt one URL. Returns true once connected, without waiting for the link to drop.
	/// </summary>
	/// <remarks>
	/// Nothing holds the socket open, and it is deliberately not in a <c>using</c>. Messages arrive
	/// through <see cref="WebSocket.OnMessageReceived"/>, which is an event rather than something
	/// being awaited, so a live connection keeps serving calls with no task babysitting it - and
	/// survives a hotload that would have killed any loop doing the babysitting.
	/// </remarks>
	private async Task<bool> TryLinkAsync( string url )
	{
		var socket = new WebSocket();

		socket.OnMessageReceived += OnMessage;

		try
		{
			await socket.Connect( url, CancellationToken.None );

			// Subscribed only once we're actually up, for two reasons: disposing a socket raises
			// this as well, so a port scan would otherwise log a disconnection for every port it
			// failed to reach; and a method group survives a hotload, where the lambda this
			// replaced could not be remapped and left the handler dangling in engine state.
			socket.OnDisconnected += OnSocketDisconnected;

			_socket = socket;
			_lastGood = url;
			_reportedOffline = false;

			Log.Info( $"[bridge] connected to {url}" );

			await SendHelloAsync();

			return true;
		}
		catch ( Exception )
		{
			// Nothing listening here, or it isn't a WebSocket - try the next port.
			// Say so once and then stay quiet; with a one-shot CLI this is the
			// normal state and we're re-scanning every second.
			if ( !_reportedOffline )
			{
				_reportedOffline = true;
				Log.Info( $"[bridge] nothing listening on {url} - waiting for an agent" );
			}

			socket.Dispose();

			return false;
		}
	}

	private void OnSocketDisconnected( int status, string reason )
	{
		Log.Info( $"[bridge] disconnected: {status} {reason}" );

		_socket = null;
	}

	/// <summary>Close the current link, if there is one.</summary>
	private void Drop()
	{
		var socket = _socket;
		_socket = null;

		socket?.Dispose();
	}

	/// <summary>
	/// Announce the verb table on connect so the bridge can build its tool list
	/// without hardcoding one.
	/// </summary>
	private async Task SendHelloAsync()
	{
		var verbs = new JsonArray();

		foreach ( var verb in AgentVerbs.All.Values )
		{
			var args = new JsonObject();
			foreach ( var (name, description) in verb.Args )
				args[name] = description;

			verbs.Add( new JsonObject
			{
				["name"] = verb.Name,
				["description"] = verb.Description,
				["args"] = args
			} );
		}

		await SendAsync( new JsonObject
		{
			["type"] = "hello",
			["game"] = Game.Ident,
			["isHost"] = Networking.IsHost,
			["verbs"] = verbs
		} );
	}

	private async void OnMessage( string message )
	{
		string id = null;

		try
		{
			var node = JsonNode.Parse( message );

			id = node?["id"]?.ToString();

			var verb = node?["verb"]?.ToString();
			var args = node?["args"] as JsonObject ?? new JsonObject();

			var result = await AgentVerbs.InvokeAsync( verb, args );

			await SendAsync( new JsonObject
			{
				["id"] = id,
				["ok"] = true,
				["result"] = result
			} );
		}
		catch ( Exception e )
		{
			await SendAsync( new JsonObject
			{
				["id"] = id,
				["ok"] = false,
				["error"] = $"{e.GetType().Name}: {e.Message}"
			} );
		}
	}

	private async Task SendAsync( JsonNode payload )
	{
		var socket = _socket;
		if ( socket is null || !socket.IsConnected )
			return;

		await socket.Send( payload.ToJsonString() );
	}
}
