using UnityEngine;
using UnityEngine.Events;

public class DroneController : MonoBehaviour
{
    [Header("References")]
    public Camera droneCamera;
    public AudioSource audioSource;

    [Header("General")]
    public float gravityDownForce = 20f;
    public LayerMask groundCheckLayers = -1;
    public float groundCheckDistance = 0.05f;

    [Header("Movement")]
    public float maxSpeedOnGround = 10f;
    public float movementSharpnessOnGround = 15;
    public float maxSpeedInAir = 10f;
    public float accelerationSpeedInAir = 25f;
    public float sprintSpeedModifier = 2f;
    public float killHeight = -50f;

    [Header("Rotation")]
    public float rotationSpeed = 200f;
    [Range(0.1f, 1f)]
    public float aimingRotationMultiplier = 0.4f;

    [Header("Jump")]
    public float jumpForce = 9f;

    [Header("Fall Damage")]
    public bool recievesFallDamage;
    public float minSpeedForFallDamage = 10f;
    public float maxSpeedForFallDamage = 30f;
    public float fallDamageAtMinSpeed = 10f;
    public float fallDamageAtMaxSpeed = 50f;

    public UnityAction<bool> onStanceChanged;

    public Vector3 droneVelocity { get; set; }
    public bool isGrounded { get; private set; }
    public bool hasJumpedThisFrame { get; private set; }
    public bool isDead { get; private set; }
    public bool isCrouching { get; private set; }
    public float RotationMultiplier;

    
    DroneInputHandler m_InputHandler;
    CharacterController m_Controller;
    Vector3 m_GroundNormal;
    Vector3 m_DroneVelocity;
    Vector3 m_LatestImpactSpeed;
    float m_LastTimeJumped = 0f;
    float m_CameraVerticalAngle = 0f;
    float m_footstepDistanceCounter;
    float m_TargetCharacterHeight;

    const float k_JumpGroundingPreventionTime = 0.2f;
    const float k_GroundCheckDistanceInAir = 0.07f;

    private void Start()
    {
        m_Controller = this.GetComponent<CharacterController>();
        m_InputHandler = this.GetComponent<DroneInputHandler>();

        m_Controller.enableOverlapRecovery = true;

    }

    private void Update()
    {
        hasJumpedThisFrame = false;

        bool wasGrounded = isGrounded;
        GroundCheck();

        HandleDroneMovement();
    }

    void GroundCheck()
    {
        float chosenGroundCheckDistance = isGrounded ? (m_Controller.skinWidth + groundCheckDistance) : k_GroundCheckDistanceInAir;
        
        isGrounded = false;
        m_GroundNormal = Vector3.up;

        if(Time.time >= m_LastTimeJumped + k_JumpGroundingPreventionTime)
        {
            if (Physics.CapsuleCast(GetCapsuleBottomHemisphere(), GetCapsuleTopHemisphere(m_Controller.height), m_Controller.radius, Vector3.down, out RaycastHit hit, chosenGroundCheckDistance, groundCheckLayers, QueryTriggerInteraction.Ignore))
            {
                m_GroundNormal = hit.normal;
                
                if (Vector3.Dot(hit.normal, transform.up) > 0f &&
                    IsNormalUnderSlopeLimit(m_GroundNormal))
                {
                    isGrounded = true;

                    // handle snapping to the ground
                    if (hit.distance > m_Controller.skinWidth)
                    {
                        m_Controller.Move(Vector3.down * hit.distance);
                    }
                }
            }
        }
    }

    void HandleDroneMovement()
    {
        //horizontal camera rotation
        // rotate the transform with the input speed around its local Y axis
        transform.Rotate(new Vector3(0f, (m_InputHandler.GetLookInputsHorizontal() * rotationSpeed * RotationMultiplier), 0f), Space.Self);

        //vertical camera rotation
        {
            // add vertical inputs to the camera's vertical angle
            m_CameraVerticalAngle += m_InputHandler.GetLookInputsVertical() * rotationSpeed * RotationMultiplier;

            // limit the camera's vertical angle to min/max
            m_CameraVerticalAngle = Mathf.Clamp(m_CameraVerticalAngle, -45f, 30f);

            // apply the vertical angle as a local rotation to the camera transform along its right axis (makes it pivot up and down)
            droneCamera.transform.localEulerAngles = new Vector3(m_CameraVerticalAngle, 0, 0);
        }

        bool isSprinting = m_InputHandler.GetSprintInputHeld();

        float speedModifier = isSprinting ? sprintSpeedModifier : 1f;

        Vector3 worldspaceMoveInput = transform.TransformVector(m_InputHandler.GetMoveInput());
        
        if (isGrounded)
        {
            
            Vector3 targetVelocity = worldspaceMoveInput * maxSpeedOnGround * speedModifier;

            targetVelocity = GetDirectionReorientedOnSlope(targetVelocity.normalized, m_GroundNormal) * targetVelocity.magnitude;

            // smoothly interpolate between our current velocity and the target velocity based on acceleration speed
            droneVelocity = Vector3.Lerp(droneVelocity, targetVelocity, movementSharpnessOnGround * Time.deltaTime);

            // jumping
            if (isGrounded && m_InputHandler.GetJumpInputDown())
            {
                // start by canceling out the vertical component of our velocity
                droneVelocity = new Vector3(droneVelocity.x, 0f, droneVelocity.z);

                // then, add the jumpSpeed value upwards
                droneVelocity += Vector3.up * jumpForce;

                // remember last time we jumped because we need to prevent snapping to ground for a short time
                m_LastTimeJumped = Time.time;
                hasJumpedThisFrame = true;

                // Force grounding to false
                isGrounded = false;
                m_GroundNormal = Vector3.up;
            }
        }
        else
        {
            
            // add air acceleration
            droneVelocity += worldspaceMoveInput * accelerationSpeedInAir * Time.deltaTime;

            // limit air speed to a maximum, but only horizontally
            float verticalVelocity = droneVelocity.y;
            Vector3 horizontalVelocity = Vector3.ProjectOnPlane(droneVelocity, Vector3.up);
            horizontalVelocity = Vector3.ClampMagnitude(horizontalVelocity, maxSpeedInAir * speedModifier);
            droneVelocity = horizontalVelocity + (Vector3.up * verticalVelocity);

            // apply the gravity to the velocity
            droneVelocity += Vector3.down * gravityDownForce * Time.deltaTime;
        }

        // apply the final calculated velocity value as a character movement
        Vector3 capsuleBottomBeforeMove = GetCapsuleBottomHemisphere();
        Vector3 capsuleTopBeforeMove = GetCapsuleTopHemisphere(m_Controller.height);
        m_Controller.Move(droneVelocity * Time.deltaTime);

        // detect obstructions to adjust velocity accordingly
        m_LatestImpactSpeed = Vector3.zero;
        if (Physics.CapsuleCast(capsuleBottomBeforeMove, capsuleTopBeforeMove, m_Controller.radius, droneVelocity.normalized, out RaycastHit hit, droneVelocity.magnitude * Time.deltaTime, -1, QueryTriggerInteraction.Ignore))
        {
            // We remember the last impact speed because the fall damage logic might need it
            m_LatestImpactSpeed = droneVelocity;

            droneVelocity = Vector3.ProjectOnPlane(droneVelocity, hit.normal);
        }
    }



    // Returns true if the slope angle represented by the given normal is under the slope angle limit of the character controller
    bool IsNormalUnderSlopeLimit(Vector3 normal)
    {
        return Vector3.Angle(transform.up, normal) <= m_Controller.slopeLimit;
    }

    // Gets the center point of the bottom hemisphere of the character controller capsule    
    Vector3 GetCapsuleBottomHemisphere()
    {
        return transform.position + (transform.up * m_Controller.radius);
    }

    // Gets the center point of the top hemisphere of the character controller capsule    
    Vector3 GetCapsuleTopHemisphere(float atHeight)
    {
        return transform.position + (transform.up * (atHeight - m_Controller.radius));
    }

    public Vector3 GetDirectionReorientedOnSlope(Vector3 direction, Vector3 slopeNormal)
    {
        Vector3 directionRight = Vector3.Cross(direction, transform.up);
        return Vector3.Cross(slopeNormal, directionRight).normalized;
    }


}
