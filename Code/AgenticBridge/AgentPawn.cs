using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// The body an agent works through: a player pawn carrying its own toolgun, so a verb doesn't
/// reach into the tool the person is holding.
/// </summary>
/// <remarks>
/// The pawn is owned by the connection of the person it belongs to, and marked
/// <see cref="Player.IsAgent"/>. Sharing the owner is deliberate - it is what keeps undo, prop
/// protection and prop limits working without inventing a second identity. Props the agent makes
/// land on that person's undo stack, so they can ctrl+z them like their own, and count against
/// that person's budget, so a companion is not a way around either.
///
/// What separates the two is the pawn itself. A second toolgun means a second set of
/// <see cref="ToolMode"/> components, so the agent's rope slack, its selected tool and its aim are
/// its own and changing them doesn't disturb the player.
/// </remarks>
public sealed class AgentPawn : Component
{
	private const string PawnPrefab = "/prefabs/engine/player.prefab";
	private const string ToolgunPrefab = "weapons/toolgun/toolgun.prefab";

	/// <summary>Approximate standing hull, matching the one the game uses to place player spawns.</summary>
	private static readonly BBox Hull = new( new Vector3( -16f, -16f, 0f ), new Vector3( 16f, 16f, 72f ) );

	/// <summary>How far from what it's working on the pawn stands.</summary>
	private const float Standoff = 80f;

	/// <summary>Where it starts out relative to its owner - off their left shoulder, slightly behind.</summary>
	private static readonly Vector3 IdleOffset = new( -40f, 60f, 0f );

	/// <summary>Roughly eye height on the standing hull, for aiming without waiting for the controller.</summary>
	private const float EyeHeight = 64f;

	/// <summary>
	/// Approach directions to try, as a yaw offset from the owner's side of the target.
	/// </summary>
	/// <remarks>
	/// Zero would put the pawn directly between the person and what it's building, so the offsets
	/// either side come first and straight-on is a last resort.
	/// </remarks>
	private static readonly float[] ApproachYaws = { 45f, -45f, 90f, -90f, 135f, -135f, 0f, 180f };

	public Player Player => GetComponent<Player>();

	/// <summary>The pawn's own toolgun, whether or not it's the active weapon.</summary>
	public Toolgun Toolgun => GetComponentInChildren<Toolgun>( true );

	/// <summary>The person this pawn belongs to.</summary>
	public Player Owner => global::Player.FindForConnection( Network.Owner );

	// ---- finding and making one ----------------------------------------

	/// <summary>The agent pawn belonging to a connection, if it has one.</summary>
	public static AgentPawn Find( Connection connection ) =>
		Game.ActiveScene?.GetAll<AgentPawn>().FirstOrDefault( x => x.Network.Owner == connection );

	/// <summary>
	/// The local player's companion, creating it the first time it's asked for.
	/// </summary>
	/// <remarks>
	/// Creation has to happen host-side, because the pawn is a networked clone of the player prefab
	/// and its toolgun builds its tool components on the host. That matches the existing constraint
	/// on spawning at a marker, so an agent driving a non-hosting client hits the same wall it
	/// already does there.
	/// </remarks>
	public static AgentPawn ForLocalPlayer()
	{
		var existing = Find( Connection.Local );
		if ( existing.IsValid() )
			return existing;

		var owner = Player.FindLocalPlayer();
		if ( !owner.IsValid() )
			throw new InvalidOperationException( "No local player - is the game in a session rather than the main menu?" );

		if ( !Networking.IsHost )
			throw new InvalidOperationException( "The agent's companion has to be created by the host, and this client isn't hosting." );

		return Create( owner );
	}

	/// <summary>Send the local player's companion away, if they have one.</summary>
	public static void DespawnForLocalPlayer()
	{
		var pawn = Find( Connection.Local );
		if ( pawn.IsValid() )
			pawn.GameObject.Destroy();
	}

	private static AgentPawn Create( Player owner )
	{
		var go = GameObject.Clone( PawnPrefab, new CloneConfig
		{
			Name = NameFor( owner ),
			StartEnabled = false,
			Transform = IdlePlacement( owner )
		} );

		var player = go.Components.Get<Player>( true );
		player.IsAgent = true;
		player.AgentName = NameFor( owner );

		// The saved hotbar belongs to the person, not to their companion, which carries a toolgun
		// and nothing else. Left on the pawn this does damage in both directions: on spawn it
		// restores their whole loadout onto the agent, and its OnPickup handler then saves the
		// agent's toolgun-only inventory back over their hotbar.
		go.Components.Get<PlayerLoadout>( true )?.Destroy();

		var pawn = go.Components.Create<AgentPawn>();

		go.NetworkSpawn( owner.Network.Owner );

		pawn.GiveToolgun();

		return pawn;
	}

	/// <summary>What to call the companion: whose agent it is.</summary>
	private static string NameFor( Player owner )
	{
		var person = owner.IsValid() ? owner.Network.Owner?.DisplayName : null;

		return string.IsNullOrWhiteSpace( person ) ? "Agent" : $"{person}'s agent";
	}

	private void GiveToolgun()
	{
		var inventory = GetComponent<PlayerInventory>();
		if ( !inventory.IsValid() ) return;

		if ( !inventory.HasWeapon<Toolgun>() )
			inventory.Pickup( ToolgunPrefab, false );

		var toolgun = inventory.GetWeapon<Toolgun>();
		if ( !toolgun.IsValid() )
			return;

		// Clear this before deploying the weapon, or the engine builds the viewmodel on the way in.
		toolgun.ViewModelPrefab = null;

		inventory.SwitchWeapon( toolgun );

		SuppressViewModel();
	}

	/// <summary>
	/// Stop the companion's toolgun drawing a first-person viewmodel.
	/// </summary>
	/// <remarks>
	/// The engine builds a viewmodel for whatever weapon it believes the local player is holding,
	/// and the pawn isn't a proxy on its owner's client - so without this the agent's toolgun draws
	/// a second pair of arms in front of the person's camera, trailing them around the map and
	/// changing tool whenever the agent does.
	///
	/// Only the view model goes. The world model stays, so the companion still visibly carries a
	/// toolgun and the beam still comes from its muzzle.
	/// </remarks>
	private void SuppressViewModel()
	{
		var toolgun = Toolgun;
		if ( !toolgun.IsValid() ) return;

		toolgun.ViewModelPrefab = null;
		toolgun.ViewModel?.Destroy();
	}

	protected override void OnStart()
	{
		DetachFromTheKeyboard();

		// Noclip suits a companion better than walking does: no gravity, so it holds the spot its
		// last action left it in rather than falling off whatever it was working on, and no
		// collision, so it can't be shoved around or left blocking a doorway.
		//
		// Owner only - IsNoclipping is a synced property and the move mode's enabled state is
		// networked, neither of which a proxy may set.
		if ( !IsProxy )
			Player?.SetNoclip( true );

		// Covers a pawn that already existed - on creation the toolgun isn't there yet and this
		// no-ops, with GiveToolgun doing the real work.
		SuppressViewModel();

		// A pawn made before this named them, or carried across a hotload, arrives blank.
		if ( Networking.IsHost && Player is { } player && string.IsNullOrWhiteSpace( player.AgentName ) )
			player.AgentName = NameFor( player );
	}

	/// <summary>
	/// Stop the engine controller treating this pawn as the person's own body.
	/// </summary>
	/// <remarks>
	/// The pawn shares its owner's connection, so it isn't a proxy on their client and the
	/// controller's own "am I being played" checks all say yes. Each of these would otherwise be
	/// visible: it would walk around with them, its head would snap to their mouse the instant
	/// <see cref="LookAt"/> pointed it anywhere, it would fight for the camera, and being in first
	/// person it would hide the very body we want them to see.
	/// </remarks>
	private void DetachFromTheKeyboard()
	{
		var controller = GetComponent<PlayerController>();
		if ( !controller.IsValid() ) return;

		controller.UseInputControls = false;
		controller.UseLookControls = false;
		controller.UseCameraControls = false;
		controller.EnablePressing = false;

		// Keep the animator - it's what makes the companion look alive rather than sliding about.
		controller.UseAnimatorControls = true;

		controller.ThirdPerson = true;
		controller.HideBodyInFirstPerson = false;
	}

	// There is deliberately no OnUpdate. The companion stays where its last action left it rather
	// than trailing the player around: it reads as having stopped where it was working, it keeps a
	// finished build framed by whoever built it, and nothing in the world moves that an agent
	// didn't ask to move. `companion --action summon` is how it gets called back.

	// ---- getting about --------------------------------------------------

	/// <summary>
	/// Stand the pawn where it can work on <paramref name="target"/>, facing it.
	/// </summary>
	public void PoseAt( Vector3 target )
	{
		var placement = SpawnPlacement.FindSpawnPosition( Approaches( target ), Hull );

		MoveTo( placement.Position );
		LookAt( target );
	}

	/// <summary>Bring the pawn back to its owner's shoulder.</summary>
	public void ReturnToOwner()
	{
		var owner = Owner;
		if ( !owner.IsValid() ) return;

		MoveTo( IdlePlacement( owner ).Position );
		LookAt( owner.WorldPosition + Vector3.Up * EyeHeight );
	}

	/// <summary>
	/// Places to stand to work on a target, best first.
	/// </summary>
	/// <remarks>
	/// Seeded from the owner's side of the target so the companion appears in front of them rather
	/// than behind whatever it's building, then fanned out to either side so the first clear spot
	/// is the one least in their way.
	/// </remarks>
	private IEnumerable<Transform> Approaches( Vector3 target )
	{
		var owner = Owner;

		var toOwner = owner.IsValid()
			? (owner.WorldPosition - target).WithZ( 0 )
			: Vector3.Forward;

		if ( toOwner.IsNearlyZero( 1f ) )
			toOwner = Vector3.Forward;

		var seed = toOwner.Normal;

		foreach ( var yaw in ApproachYaws )
		{
			yield return new Transform( target + Rotation.FromYaw( yaw ) * seed * Standoff );
		}
	}

	private static Transform IdlePlacement( Player owner )
	{
		var seed = owner.WorldTransform.PointToWorld( IdleOffset );

		return SpawnPlacement.FindSpawnPosition( [new Transform( seed )], Hull, scanRadius: 64f );
	}

	private void MoveTo( Vector3 position )
	{
		WorldPosition = position;

		// It arrives standing still rather than carrying whatever it was doing into the new spot.
		var body = GetComponent<Rigidbody>();
		if ( body.IsValid() )
			body.Velocity = Vector3.Zero;
	}

	/// <summary>
	/// Turn the pawn to face a point. Tools aim through their own override, so this is purely so
	/// the companion looks like it's doing what it's doing.
	/// </summary>
	private void LookAt( Vector3 target )
	{
		var controller = GetComponent<PlayerController>();
		if ( !controller.IsValid() ) return;

		// Measured off the hull rather than the controller's eye transform, which may not have
		// caught up with the move we just made.
		var eyes = WorldPosition + Vector3.Up * EyeHeight;

		var toTarget = target - eyes;
		if ( toTarget.IsNearlyZero( 1f ) ) return;

		controller.EyeAngles = Rotation.LookAt( toTarget.Normal ).Angles();
	}
}
