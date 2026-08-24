using System;
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
	/// Where the bridge is listening. Must be a hostname, not an IP literal -
	/// Http.HasAllowedScheme rejects raw addresses - and localhost is limited to
	/// ports 80/443/8080/8443.
	/// </summary>
	[ConVar( "sb.bridge_url", ConVarFlags.Saved, Help = "WebSocket URL of the local agent bridge." )]
	public static string Url { get; set; } = "ws://localhost:8080/";

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
		Log.Info( $"[bridge] enabled={Enabled} url={Url} connected={IsLinked} verbs={AgentVerbs.All.Count}" );
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
			try
			{
				using var socket = new WebSocket();

				socket.OnMessageReceived += OnMessage;
				socket.OnDisconnected += ( status, reason ) => Log.Info( $"[bridge] disconnected: {status} {reason}" );

				await socket.Connect( Url, ct );

				_socket = socket;
				Log.Info( $"[bridge] connected to {Url}" );

				await SendHelloAsync();

				// hold the socket open; OnMessage does the real work
				while ( Current == this && socket.IsConnected && !ct.IsCancellationRequested )
				{
					await GameTask.DelayRealtimeSeconds( 0.25f );
				}
			}
			catch ( Exception e ) when ( !ct.IsCancellationRequested )
			{
				Log.Info( $"[bridge] link failed ({e.GetType().Name}: {e.Message}) - retrying in {RetrySeconds}s" );
			}
			finally
			{
				_socket = null;
			}

			if ( Current != this || ct.IsCancellationRequested )
				break;

			await GameTask.DelayRealtimeSeconds( RetrySeconds );
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
