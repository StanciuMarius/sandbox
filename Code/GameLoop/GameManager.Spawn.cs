public sealed partial class GameManager 
{
	[ConCmd( "spawn" )]
	private static void SpawnCommand( string ident )
	{
		Spawn( ident );
	}

	/// <summary>
	/// Spawn from a string identifier (e.g. "prop:path", "entity:path", "dupe.local:id", "dupe.workshop:id").
	/// Optional metadata string is passed through to the spawner for type-specific use (e.g. mount bounds/title).
	/// </summary>
	[Rpc.Broadcast]
	public static async void Spawn( string ident, string metadata = null, bool forceWorld = false )
	{
		// if we're the person calling this, then we don't do anything but add the spawn stat
		if ( Rpc.Caller == Connection.Local )
		{
			var data = new Dictionary<string, object>();
			data["ident"] = ident;
			Sandbox.Services.Stats.Increment( "spawn", 1, data );

			Sound.Play( "sounds/ui/ui.spawn.sound" );
		}

		// Only actually spawn it on the host
		if ( !Networking.IsHost )
			return;

		var player = Player.FindForConnection( Rpc.Caller );
		if ( player is null ) return;

		await SpawnAt( ident, AimTransform( player ), player, metadata, forceWorld );
	}

	/// <summary>
	/// Where a spawn lands when the player doesn't say - on the first surface along their view,
	/// turned to face them.
	/// </summary>
	public static Transform AimTransform( Player player )
	{
		var eyes = player.EyeTransform;

		var trace = Game.SceneTrace.Ray( eyes.Position, eyes.Position + eyes.Forward * 2048 )
			.IgnoreGameObject( player.GameObject )
			.WithoutTags( "player" )
			.Run();

		var up = trace.Normal;
		var backward = -eyes.Forward;

		var right = Vector3.Cross( up, backward ).Normal;
		var forward = Vector3.Cross( right, up ).Normal;
		var facingAngle = Rotation.LookAt( forward, up );

		return new Transform( trace.EndPosition, facingAngle );
	}

	/// <summary>
	/// Spawn at an explicit transform and hand back what was created.
	/// </summary>
	/// <remarks>
	/// Must run on the host. Split out of <see cref="Spawn"/> so a caller that knows where it wants
	/// something - the agent bridge placing a prop on a marker - can say so, and can then go on to
	/// weld or bolt to what it just made. The broadcast <see cref="Spawn"/> can't do either: it
	/// always uses the player's aim, and being fire-and-forget it has nothing to return.
	/// </remarks>
	/// <returns>The spawned roots, or null if the ident didn't resolve or a limit refused it.</returns>
	public static async Task<List<GameObject>> SpawnAt( string ident, Transform transform, Player player, string metadata = null, bool forceWorld = false )
	{
		// TODO - can this user spawn this package?

		var (type, path, source) = SpawnlistItem.ParseIdent( ident );

		var spawner = ISpawner.Create( type, path, source, metadata );

		if ( spawner is not null && await spawner.Loading )
			return await SpawnAndUndo( spawner, transform, player, forceWorld );

		Log.Warning( $"Couldn't resolve '{ident}'" );

		return null;
	}

	private static async Task<List<GameObject>> SpawnAndUndo( ISpawner spawner, Transform transform, Player player, bool forceWorld = false )
	{
		var spawnData = new Global.ISpawnEvents.SpawnData
		{
			Spawner = spawner,
			Transform = transform,
			Player = player?.Network.Owner
		};

		Game.ActiveScene.RunEvent<Global.ISpawnEvents>( x => x.OnSpawn( spawnData ) );

		if ( spawnData.Cancelled )
			return null;

		if ( !player.IsValid() )
			return null;

		// If the prefab is a weapon, pick it up directly instead of spawning into the world
		if ( !forceWorld )
		{
			var prefab = spawner.Prefab;
			if ( prefab is not null && prefab.GetComponent<BaseSandboxWeapon>( true ) is not null )
			{
				var inventory = player.GetComponent<PlayerInventory>();
				inventory.Pickup( prefab, true );
				return null;
			}
		}

		var objects = await spawner.Spawn( transform, player );

		if ( objects is { Count: > 0 } )
		{
			var undo = player.Undo.Create();
			undo.Name = $"Spawn {spawner.DisplayName}";

			foreach ( var go in objects )
			{
				undo.Add( go );
			}

			Game.ActiveScene.RunEvent<Global.ISpawnEvents>( x => x.OnPostSpawn( new Global.ISpawnEvents.PostSpawnData
			{
				Spawner = spawner,
				Transform = transform,
				Player = player?.Network.Owner,
				Objects = objects
			} ) );
		}

		return objects;
	}
}
