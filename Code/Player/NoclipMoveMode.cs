using Sandbox.Movement;

public sealed class NoclipMoveMode : Sandbox.Movement.MoveMode
{
	/// <summary>
	/// If true, the player will still collide with the world and other players. This probably
	/// means that the noclip mode is named wrong. But it's cool. It just becomes a fly around mode.
	/// </summary>
	[Property]
	public bool EnableCollision { get; set; }

	[Property]
	public float RunSpeed { get; set; } = 1200;

	[Property]
	public float WalkSpeed { get; set; } = 200;

	protected override void OnUpdateAnimatorState( SkinnedModelRenderer renderer )
	{
		renderer.Set( "b_noclip", true );
		renderer.Set( "duck", 0f );
	}

	public override int Score( PlayerController controller )
	{
		return 1000;
	}

	public override void UpdateRigidBody( Rigidbody body )
	{
		body.Gravity = false;
		body.LinearDamping = 5.0f;
		body.AngularDamping = 1f;

		body.Tags.Set( "noclip", !EnableCollision );
	}

	/// <summary>
	/// Whether the person at this keyboard is the one flying this body.
	/// </summary>
	/// <remarks>
	/// Not <c>!IsProxy</c>. An agent pawn is owned by its person's connection, so it isn't a proxy
	/// on their client - and the direct <see cref="Input"/> reads below go around the controller's
	/// <c>UseInputControls</c> switch, which is the usual way of telling a pawn not to listen.
	/// </remarks>
	private bool IsPlayerDriven => GetComponentInParent<Player>() is { IsLocalPlayer: true };

	public override void OnModeBegin()
	{
		Controller.IsClimbing = true;
		Controller.Body.Gravity = false;

		if ( IsPlayerDriven )
			Sandbox.Services.Stats.Increment( "move.noclip.use", 1 );
	}

	public override void OnModeEnd( MoveMode next )
	{
		Controller.IsClimbing = false;
		Controller.Body.Velocity = Controller.Body.Velocity.ClampLength( Controller.RunSpeed );
		Controller.Body.Tags.Set( "noclip", false );
	}

	public override Transform CalculateEyeTransform()
	{
		var transform = base.CalculateEyeTransform();

		// Undo the camera lowering that IsDucking causes
		if ( Controller.IsDucking )
			transform.Position += Vector3.Up * (Controller.BodyHeight - Controller.DuckedHeight);

		return transform;
	}

	public override Vector3 UpdateMove( Rotation eyes, Vector3 input )
	{
		// A noclipping agent pawn stays exactly where it was put - it's moved by the bridge, not
		// flown. Without this the jump and duck reads below would drift it up and down whenever
		// its owner pressed those keys.
		if ( !IsPlayerDriven )
			return Vector3.Zero;

		// don't normalize, because analog input might want to go slow
		input = input.ClampLength( 1 );

		var direction = eyes * input;

		// Run if we're holding down alt move button
		bool run = Input.Down( Controller.AltMoveButton );

		// if Run is default, flip that logic
		if ( Controller.RunByDefault ) run = !run;

		// if we're running, use run speed, if not use walk speed
		var velocity = run ? RunSpeed * 2.0f : RunSpeed;

		// Slow down when the walk modifier (Alt) is held
		if ( Input.Down( "walk" ) ) velocity = WalkSpeed;

		if ( direction.IsNearlyZero( 0.1f ) )
		{
			direction = 0;
		}

		// if we're hold down jump move upwards
		if ( Input.Down( "jump" ) ) direction += Vector3.Up;

		// if we're hold down duck move downwards
		if ( Input.Down( "duck" ) ) direction += Vector3.Down;

		return direction * velocity;
	}

}
