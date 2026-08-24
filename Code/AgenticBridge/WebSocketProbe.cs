using System;
using System.Threading;
using System.Threading.Tasks;

namespace Sandbox;

/// <summary>
/// Temporary probe. Answers one question: may whitelisted game code open an
/// outbound WebSocket to a local address, or does access control block it?
///
/// A policy block and a plain "nothing is listening" both throw, so we log the
/// exception type and message to tell them apart.
///
/// Standalone builds have no console, so this also runs itself on scene start -
/// the listening echo server is the oracle there, not the game log.
///
/// Delete this file once the answer is recorded.
/// </summary>
internal static class WebSocketProbe
{
	public const string DefaultUrl = "ws://localhost:8080/";

	[ConCmd( "as_probe_ws" )]
	public static void ProbeCommand( string url )
	{
		_ = Probe( string.IsNullOrWhiteSpace( url ) ? DefaultUrl : url );
	}

	public static async Task Probe( string url )
	{
		Log.Info( $"[ws-probe] ---- connecting to {url} ----" );
		Log.Info( $"[ws-probe] IsEditor={Application.IsEditor} IsStandalone={Application.IsStandalone} IsDedicatedServer={Application.IsDedicatedServer}" );

		var ws = new WebSocket();

		ws.OnMessageReceived += ( msg ) => Log.Info( $"[ws-probe] recv: {msg}" );
		ws.OnDisconnected += ( status, reason ) => Log.Info( $"[ws-probe] disconnected: status={status} reason={reason}" );

		try
		{
			using var cts = new CancellationTokenSource( 4000 );

			await ws.Connect( url, cts.Token );

			Log.Info( $"[ws-probe] RESULT=CONNECTED IsConnected={ws.IsConnected}" );

			await ws.Send( $"hello from sandbox (standalone={Application.IsStandalone})" );
			await Task.Delay( 750 );
		}
		catch ( Exception e )
		{
			Log.Info( $"[ws-probe] RESULT=FAILED type={e.GetType().FullName}" );
			Log.Info( $"[ws-probe] message={e.Message}" );

			if ( e.InnerException is not null )
				Log.Info( $"[ws-probe] inner={e.InnerException.GetType().FullName}: {e.InnerException.Message}" );
		}
		finally
		{
			ws.Dispose();
		}
	}
}

/// <summary>
/// Fires <see cref="WebSocketProbe"/> shortly after the scene starts, so the probe
/// runs in builds that have no console to type into.
/// </summary>
internal sealed class WebSocketProbeSystem : GameObjectSystem<WebSocketProbeSystem>
{
	public WebSocketProbeSystem( Scene scene ) : base( scene )
	{
		_ = RunSoon();
	}

	private static async Task RunSoon()
	{
		// let the scene settle before we touch the network stack
		await GameTask.DelayRealtimeSeconds( 2f );

		await WebSocketProbe.Probe( WebSocketProbe.DefaultUrl );
	}
}
