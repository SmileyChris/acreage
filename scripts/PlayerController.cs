using Godot;

public sealed class PlayerController
{
    private const float Gravity = 30f;
    private const float MaxFallSpeed = 40f;
    private const float JumpImpulse = 10f;
    private const float WalkSpeed = 12f;
    private const float SprintSpeed = 24f;
    private const float PitchMin = -1.2f;
    private const float PitchMax = 0.5f;

    private const string ActionMoveForward = "move_forward";
    private const string ActionMoveBackward = "move_backward";
    private const string ActionMoveLeft = "move_left";
    private const string ActionMoveRight = "move_right";
    private const string ActionSprint = "move_sprint";
    private const string ActionJump = "move_jump";

    private readonly CharacterBody3D _player;
    private readonly Node3D _cameraPivot;
    private Vector3 _velocity;
    private float _yaw;
    private float _pitch;

    public PlayerController(CharacterBody3D player, Node3D cameraPivot)
    {
        _player = player;
        _cameraPivot = cameraPivot;
    }

    public float Yaw => _yaw;

    public void HandleMouseMotion(InputEventMouseMotion motion)
    {
        const float sensitivity = 0.0025f;
        _yaw -= motion.Relative.X * sensitivity;
        _pitch -= motion.Relative.Y * sensitivity;
        _pitch = Mathf.Clamp(_pitch, PitchMin, PitchMax);
        _player.Rotation = new Vector3(0f, _yaw, 0f);
        _cameraPivot.Rotation = new Vector3(_pitch, 0f, 0f);
    }

    public void Update(float delta)
    {
        if (_player.IsOnFloor())
        {
            _velocity.Y = 0f;
            if (Input.IsActionJustPressed(ActionJump))
            {
                _velocity.Y = JumpImpulse;
            }
        }
        else
        {
            _velocity.Y -= Gravity * delta;
            if (_velocity.Y < -MaxFallSpeed)
            {
                _velocity.Y = -MaxFallSpeed;
            }
        }

        var input = Vector2.Zero;
        if (Input.IsActionPressed(ActionMoveForward))
        {
            input.Y -= 1f;
        }

        if (Input.IsActionPressed(ActionMoveBackward))
        {
            input.Y += 1f;
        }

        if (Input.IsActionPressed(ActionMoveLeft))
        {
            input.X -= 1f;
        }

        if (Input.IsActionPressed(ActionMoveRight))
        {
            input.X += 1f;
        }

        var speed = Input.IsActionPressed(ActionSprint) ? SprintSpeed : WalkSpeed;
        var yawBasis = Basis.Identity.Rotated(Vector3.Up, _yaw);
        var direction = yawBasis * new Vector3(input.X, 0f, input.Y);
        if (direction.LengthSquared() > 0f)
        {
            direction = direction.Normalized();
        }

        _velocity.X = direction.X * speed;
        _velocity.Z = direction.Z * speed;

        _player.Velocity = _velocity;
        _player.MoveAndSlide();
        _velocity = _player.Velocity;
    }
}
