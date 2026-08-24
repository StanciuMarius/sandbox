using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Outbound WebSocket link to an agent bridge running on this machine.
///
/// The game always dials out - nothing listens inside the game process - so this
/// works from a published client behind NAT with no ports opened. The bridge on
/// the other end is what speaks MCP to an agent.
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
	/// IP literal - Http.HasAllowedScheme rejects raw addresses - and localhost is
	/// limited to ports 80/443/8080/8443.
	/// </summary>
	[ConVar( "sb.bridge_url", ConVarFlags.Saved, Help = "Explicit agent bridge URL. Empty tries the allowed local ports in order." )]
	public static string Url { get; set; } = "";

	/// <summary>
	/// The only ports localhost is reachable on, in the order we try them. 8080 first
	/// because 80 and 443 need elevation to bind on Windows, so a bridge is unlikely
	/// to be there. A busy port fails the upgrade and we move on to the next.
	/// </summary>
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

	private const float RetrySeconds = 5f;

	private WebSocket _socket;
	private CancellationTokenSource _cts;

	public static bool IsLinked => Current?._socket?.IsConnected ?? false;

	public AgentBridge( Scene scene ) : base( scene )
	{
		if ( !Enabled )
			return;

		_cts = new CancellationTokenSource();
		_ = MaintainAsync( _cts.Token );
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

		Current._cts?.Cancel();
		Current._cts = new CancellationTokenSource();
		_ = Current.MaintainAsync( Current._cts.Token );
	}

	[ConCmd( "bridge_disconnect", Help = "Drop the agent bridge connection." )]
	public static void DisconnectCommand()
	{
		Current?._cts?.Cancel();
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
	/// Connect, pump, and reconnect until the scene goes away or we're cancelled.
	/// </summary>
	private async Task MaintainAsync( CancellationToken ct )
	{
		// Current is assigned after our constructor returns, so yield once before
		// the loop - otherwise the first "are we still the live system" check
		// fails against a null Current and we exit before ever connecting.
		await GameTask.DelayRealtimeSeconds( 0.5f );

		while ( Current == this && !ct.IsCancellationRequested )
		{
			foreach ( var url in Candidates )
			{
				if ( Current != this || ct.IsCancellationRequested )
					return;

				if ( await TryLinkAsync( url, ct ) )
					break;
			}

			if ( Current != this || ct.IsCancellationRequested )
				break;

			await GameTask.DelayRealtimeSeconds( RetrySeconds );
		}
	}

	/// <summary>
	/// Attempt one URL. Returns true if we connected - in which case this doesn't
	/// return until the link drops - or false to let the caller try the next port.
	/// </summary>
	private async Task<bool> TryLinkAsync( string url, CancellationToken ct )
	{
		try
		{
			using var socket = new WebSocket();

			socket.OnMessageReceived += OnMessage;
			socket.OnDisconnected += ( status, reason ) => Log.Info( $"[bridge] disconnected: {status} {reason}" );

			await socket.Connect( url, ct );

			_socket = socket;
			_lastGood = url;
			Log.Info( $"[bridge] connected to {url}" );

			await SendHelloAsync();

			// hold the socket open; OnMessage does the real work
			while ( Current == this && socket.IsConnected && !ct.IsCancellationRequested )
			{
				await GameTask.DelayRealtimeSeconds( 0.25f );
			}

			return true;
		}
		catch ( Exception e ) when ( !ct.IsCancellationRequested )
		{
			// nothing listening here, or it isn't a WebSocket - try the next port
			Log.Info( $"[bridge] {url} unavailable ({e.GetType().Name})" );
			return false;
		}
		finally
		{
			_socket = null;
		}
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
